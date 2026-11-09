using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Services;

public sealed record NotificationItem(string AppName, string Title, string Body, DateTimeOffset Timestamp);

/// <summary>
/// Notification ingestion. It first tries the Windows UserNotificationListener
/// (available to packaged apps with the userNotificationListener capability)
/// and always exposes a named-pipe endpoint so other tools can push alerts:
///
///   DynamicIsland.exe --notify "Build finished"
///   echo {"app":"CI","title":"Build","body":"Green"} | nc ...
///
/// The pipe is named DynamicIsland.Notify and accepts one JSON object per line.
/// </summary>
public sealed class NotificationService : IDisposable
{
    public const string PipeName = "DynamicIsland.Notify";

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pipeTask;
    private UserNotificationListener? _listener;
    private bool _disposed;

    public event Action<NotificationItem>? NotificationReceived;

    public bool ListenerAvailable { get; private set; }

    public NotificationService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        _pipeTask = Task.Run(() => PipeLoopAsync(_cts.Token));
        _ = InitializeListenerAsync();
    }

    public void PublishLocal(string title, string body, string appName = "Dynamic Island")
        => Publish(new NotificationItem(appName, title, body, DateTimeOffset.Now));

    public void Publish(NotificationItem item)
    {
        if (_disposed)
            return;

        if (_dispatcher.CheckAccess())
            NotificationReceived?.Invoke(item);
        else
            _dispatcher.BeginInvoke(() => NotificationReceived?.Invoke(item));
    }

    private async Task InitializeListenerAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Debug.WriteLine($"Notifications: listener access is {access}.");
                return;
            }

            _listener.NotificationChanged += OnNotificationChanged;
            ListenerAvailable = true;
            Debug.WriteLine("Notifications: UserNotificationListener active.");
        }
        catch (Exception ex)
        {
            // Unpackaged WPF apps normally do not have notification-listener
            // capability. The pipe endpoint and in-app alerts still work.
            Debug.WriteLine($"Notifications: listener unavailable: {ex.Message}");
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
            return;

        _ = LoadNotificationAsync(sender, args.UserNotificationId);
    }

    private async Task LoadNotificationAsync(UserNotificationListener listener, uint notificationId)
    {
        try
        {
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var notification = notifications.FirstOrDefault(n => n.Id == notificationId);
            if (notification is null)
                return;

            var app = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Notification";
            var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var elements = binding?.GetTextElements();

            var lines = new List<string>();
            if (elements is not null)
            {
                foreach (var element in elements)
                {
                    if (!string.IsNullOrWhiteSpace(element.Text))
                        lines.Add(element.Text);
                }
            }

            var title = lines.Count > 0 ? lines[0] : app;
            var body = lines.Count > 1 ? string.Join(" ", lines.Skip(1)) : string.Empty;

            Publish(new NotificationItem(app, title, body, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Notifications: read failed: {ex.Message}");
        }
    }

    private async Task PipeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server, new UTF8Encoding(false));
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line is null)
                        break;
                    if (!string.IsNullOrWhiteSpace(line))
                        HandlePayload(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Notifications: pipe error: {ex.Message}");
                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void HandlePayload(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var app = ReadString(root, "app") ?? "Dynamic Island";
            var title = ReadString(root, "title") ?? string.Empty;
            var body = ReadString(root, "body")
                       ?? ReadString(root, "text")
                       ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                return;

            if (string.IsNullOrWhiteSpace(title))
                title = app;

            Publish(new NotificationItem(app, title, body, DateTimeOffset.Now));
        }
        catch (JsonException)
        {
            Publish(new NotificationItem("Dynamic Island", "Notification", line, DateTimeOffset.Now));
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    /// <summary>Used by a second process instance to forward --notify arguments.</summary>
    public static bool TrySend(string payload, int timeoutMilliseconds = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(payload);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Notifications: pipe send failed: {ex.Message}");
            return false;
        }
    }

    public static string CreatePayload(string title, string body, string app = "Dynamic Island")
        => JsonSerializer.Serialize(new { app, title, body });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            if (_listener is not null && ListenerAvailable)
                _listener.NotificationChanged -= OnNotificationChanged;
        }
        catch
        {
            // Ignore shutdown races.
        }

        _cts.Dispose();
    }
}
