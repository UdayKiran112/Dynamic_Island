using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Wraps the Global System Media Transport Controls (GSMTC) API. Everything is
/// event driven: the service only re-reads a session when Windows reports a
/// change, and a one-second timer runs only while media is playing (for the
/// progress bar).
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public event EventHandler? MediaChanged;

    public bool IsAvailable { get; private set; }
    public bool HasMedia => !string.IsNullOrWhiteSpace(Title);
    public string Title { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string Album { get; private set; } = string.Empty;
    public string SourceApp { get; private set; } = string.Empty;
    public ImageSource? Artwork { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool HasTimeline { get; private set; }
    public double ProgressPercent { get; private set; }
    public string PositionText { get; private set; } = "0:00";
    public string DurationText { get; private set; } = "0:00";

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _positionTimer.Tick += (_, _) => RefreshTimeline();
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachCurrentSession();
            IsAvailable = true;
            Debug.WriteLine("Media: GSMTC session manager ready.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: GSMTC unavailable: {ex.Message}");
        }
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
        => _dispatcher.BeginInvoke(AttachCurrentSession);

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
        => _dispatcher.BeginInvoke(AttachCurrentSession);

    private void AttachCurrentSession()
    {
        if (_disposed)
            return;

        var next = _manager?.GetCurrentSession();
        if (ReferenceEquals(next, _session))
        {
            RefreshPlaybackInfo();
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = next;

        if (_session is null)
        {
            Clear();
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        _ = UpdateMediaPropertiesAsync();
        RefreshPlaybackInfo();
        RefreshTimeline();
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
        => _ = UpdateMediaPropertiesAsync();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
        => _dispatcher.BeginInvoke(() =>
        {
            RefreshPlaybackInfo();
            RefreshTimeline();
        });

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
        => _dispatcher.BeginInvoke(RefreshTimeline);

    private async Task UpdateMediaPropertiesAsync()
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var artwork = await LoadThumbnailAsync(properties.Thumbnail);

            if (_disposed)
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(session, _session))
                    return;

                Title = properties.Title ?? string.Empty;
                Artist = properties.Artist ?? string.Empty;
                Album = properties.AlbumTitle ?? string.Empty;
                SourceApp = FriendlySourceName(session.SourceAppUserModelId);
                Artwork = artwork;
                RefreshPlaybackInfoCore();
                MediaChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: could not read properties: {ex.Message}");
        }
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
            return null;

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > int.MaxValue)
                return null;

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);

            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: artwork failed: {ex.Message}");
            return null;
        }
    }

    private void RefreshPlaybackInfo()
    {
        if (_disposed)
            return;

        RefreshPlaybackInfoCore();
        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshPlaybackInfoCore()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (IsPlaying)
            {
                if (!_positionTimer.IsEnabled)
                    _positionTimer.Start();
            }
            else
            {
                _positionTimer.Stop();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: playback info failed: {ex.Message}");
        }
    }

    private void RefreshTimeline()
    {
        if (_disposed)
            return;

        try
        {
            var timeline = _session?.GetTimelineProperties();
            if (timeline is null)
            {
                HasTimeline = false;
                ProgressPercent = 0;
                return;
            }

            var start = timeline.StartTime;
            var end = timeline.EndTime;
            var position = timeline.Position;

            if (IsPlaying)
                position += DateTimeOffset.Now - timeline.LastUpdatedTime;

            if (position < start)
                position = start;
            if (position > end)
                position = end;

            var total = (end - start).TotalSeconds;
            var current = (position - start).TotalSeconds;

            HasTimeline = total > 0;
            ProgressPercent = total > 0 ? Math.Clamp(current / total * 100, 0, 100) : 0;
            PositionText = FormatTime(position);
            DurationText = FormatTime(end);
            MediaChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: timeline failed: {ex.Message}");
        }
    }

    private void Clear()
    {
        Title = string.Empty;
        Artist = string.Empty;
        Album = string.Empty;
        SourceApp = string.Empty;
        Artwork = null;
        IsPlaying = false;
        HasTimeline = false;
        ProgressPercent = 0;
        PositionText = "0:00";
        DurationText = "0:00";
        _positionTimer.Stop();
        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task TogglePlayPauseAsync()
    {
        try
        {
            if (_session is not null)
                await _session.TryTogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: toggle failed: {ex.Message}");
        }
    }

    public async Task SkipNextAsync()
    {
        try
        {
            if (_session is not null)
                await _session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: next failed: {ex.Message}");
        }
    }

    public async Task SkipPreviousAsync()
    {
        try
        {
            if (_session is not null)
                await _session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: previous failed: {ex.Message}");
        }
    }

    public async Task SeekAsync(double percent)
    {
        try
        {
            if (_session is null)
                return;

            var timeline = _session.GetTimelineProperties();
            var total = timeline.EndTime - timeline.StartTime;
            if (total <= TimeSpan.Zero)
                return;

            var target = timeline.StartTime + TimeSpan.FromTicks((long)(total.Ticks * Math.Clamp(percent, 0, 1)));
            await _session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: seek failed: {ex.Message}");
        }
    }

    private static string FriendlySourceName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return string.Empty;

        var value = aumid;
        var bang = value.IndexOf('!');
        if (bang > 0)
            value = value[..bang];

        if (value.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            return "Spotify";
        if (value.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            return "Chrome";
        if (value.Contains("msedge", StringComparison.OrdinalIgnoreCase))
            return "Edge";
        if (value.Contains("firefox", StringComparison.OrdinalIgnoreCase))
            return "Firefox";
        if (value.Contains("zune", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("mediaplayer", StringComparison.OrdinalIgnoreCase))
            return "Media Player";
        if (value.Contains("vlc", StringComparison.OrdinalIgnoreCase))
            return "VLC";
        if (value.Contains("music", StringComparison.OrdinalIgnoreCase))
            return "Music";

        var lastDot = value.LastIndexOf('.');
        var lastSlash = value.LastIndexOf('\\');
        var start = Math.Max(lastDot, lastSlash) + 1;
        return start > 0 && start < value.Length ? value[start..] : value;
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _positionTimer.Stop();

        try
        {
            if (_manager is not null)
            {
                _manager.SessionsChanged -= OnSessionsChanged;
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }

            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Media: dispose failed: {ex.Message}");
        }
    }
}
