using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

public partial class App : Application
{
    private const string MutexName = @"Local\DynamicIsland.SingleInstance";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _shuttingDown;
    private DispatcherTimer? _saveTimer;
    private MediaService? _media;
    private StatsService? _stats;
    private TimerService? _timers;
    private NotificationService? _notifications;
    private TrayService? _tray;

    public AppSettings Settings { get; private set; } = new();
    public MainViewModel? ViewModel { get; private set; }
    public MainWindow? Island { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            ForwardNotificationArgs(e.Args);
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();
        ThemeManager.Apply(Settings.Theme);
        StartupManager.SetEnabled(Settings.StartWithWindows);

        _media = new MediaService(Dispatcher);
        _stats = new StatsService(Dispatcher);
        _timers = new TimerService(Dispatcher);
        _notifications = new NotificationService(Dispatcher);

        ViewModel = new MainViewModel(Settings, _media, _stats, _timers, _notifications);
        ViewModel.QuitRequested += ShutdownApp;

        Island = new MainWindow(ViewModel, Settings);
        MainWindow = Island;
        Island.Show();

        _tray = new TrayService(ViewModel, Island, ShutdownApp);
        _notifications.Start();
        _ = ViewModel.InitializeAsync();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer?.Stop();
            SettingsStore.Save(Settings);
        };
        Settings.PropertyChanged += OnSettingsChanged;

        HandleStartupArgs(e.Args);
    }

    private void HandleStartupArgs(string[] args)
    {
        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => Island?.OpenSettings());

        var notifyIndex = Array.FindIndex(args, a => a.Equals("--notify", StringComparison.OrdinalIgnoreCase));
        if (notifyIndex >= 0 && notifyIndex + 1 < args.Length)
        {
            var payload = string.Join(" ", args.Skip(notifyIndex + 1));
            Dispatcher.BeginInvoke(() => ShowLocalPayload(payload));
        }
    }

    private void ShowLocalPayload(string payload)
    {
        var (title, body) = ParseNotificationText(payload);
        ViewModel?.ShowNotification(title, body);
    }

    private static (string Title, string Body) ParseNotificationText(string payload)
    {
        var separator = payload.IndexOf('|');
        if (separator > 0)
            return (payload[..separator].Trim(), payload[(separator + 1)..].Trim());

        return ("Notification", payload.Trim());
    }

    private static void ForwardNotificationArgs(string[] args)
    {
        var notifyIndex = Array.FindIndex(args, a => a.Equals("--notify", StringComparison.OrdinalIgnoreCase));
        if (notifyIndex < 0 || notifyIndex + 1 >= args.Length)
            return;

        var payload = string.Join(" ", args.Skip(notifyIndex + 1));
        var (title, body) = ParseNotificationText(payload);
        NotificationService.TrySend(NotificationService.CreatePayload(title, body));
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                ThemeManager.Apply(Settings.Theme);
                break;
            case nameof(AppSettings.StartWithWindows):
                StartupManager.SetEnabled(Settings.StartWithWindows);
                break;
            case nameof(AppSettings.EnableHotkeys):
                Island?.RefreshHotkeys();
                break;
        }

        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void ShutdownApp()
    {
        if (_shuttingDown)
            return;

        _shuttingDown = true;

        SettingsStore.Save(Settings);
        _saveTimer?.Stop();
        Settings.PropertyChanged -= OnSettingsChanged;

        _tray?.Dispose();
        _tray = null;

        _notifications?.Dispose();
        _timers?.Dispose();
        _stats?.Dispose();
        _media?.Dispose();

        Island?.PrepareForShutdown();
        Island?.Close();

        try
        {
            if (_ownsMutex)
                _mutex?.ReleaseMutex();
        }
        catch
        {
            // The mutex may already have been released during shutdown.
        }

        _mutex?.Dispose();
        _mutex = null;

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_shuttingDown)
        {
            SettingsStore.Save(Settings);
            _notifications?.Dispose();
            _timers?.Dispose();
            _stats?.Dispose();
            _media?.Dispose();
        }

        base.OnExit(e);
    }
}
