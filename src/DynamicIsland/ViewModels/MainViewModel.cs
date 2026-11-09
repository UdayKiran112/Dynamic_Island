using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Media;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly MediaService _media;
    private readonly StatsService _stats;
    private readonly TimerService _timers;
    private readonly NotificationService _notifications;
    private readonly DispatcherTimer _clockTimer;

    private string _mediaTitle = string.Empty;
    private string _mediaArtist = string.Empty;
    private string _mediaAlbum = string.Empty;
    private string _mediaSource = string.Empty;
    private ImageSource? _mediaArtwork;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _hasTimeline;
    private double _mediaProgress;
    private string _positionText = "0:00";
    private string _durationText = "0:00";

    private string _clockText = string.Empty;
    private string _currentDate = string.Empty;

    private int _activeTabIndex;
    private bool _isExpanded;

    private string _customMinutes = "15";
    private bool _hasTimers;
    private string _stopwatchText = "00:00.00";
    private bool _isStopwatchRunning;

    private double _cpuPercent;
    private string _cpuText = "0%";
    private double _memoryPercent;
    private string _memoryText = "0.0 / 0.0 GB";
    private string _networkDown = "0 B/s";
    private string _networkUp = "0 B/s";
    private bool _hasBattery;
    private bool _isCharging;
    private double _batteryPercent;
    private string _batteryText = "No battery";

    private string _peekApp = string.Empty;
    private string _peekTitle = string.Empty;
    private string _peekBody = string.Empty;
    private string _peekTime = string.Empty;

    public MainViewModel(
        AppSettings settings,
        MediaService media,
        StatsService stats,
        TimerService timers,
        NotificationService notifications)
    {
        Settings = settings;
        _media = media;
        _stats = stats;
        _timers = timers;
        _notifications = notifications;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _media.MediaChanged += OnMediaChanged;
        _stats.StatsUpdated += OnStatsUpdated;
        _timers.TimerFinished += OnTimerFinished;
        _timers.StopwatchChanged += OnStopwatchChanged;
        _timers.Timers.CollectionChanged += OnTimersCollectionChanged;
        _notifications.NotificationReceived += OnNotificationReceived;

        ExpandCommand = new RelayCommand(() => ExpandRequested?.Invoke());
        CollapseCommand = new RelayCommand(() => CollapseRequested?.Invoke());
        PlayPauseCommand = new RelayCommand(() => _ = _media.TogglePlayPauseAsync());
        NextCommand = new RelayCommand(() => _ = _media.SkipNextAsync());
        PreviousCommand = new RelayCommand(() => _ = _media.SkipPreviousAsync());
        ShowTabCommand = new RelayCommand(ShowTab);
        StartTimerCommand = new RelayCommand(StartTimerFromParameter);
        StartCustomTimerCommand = new RelayCommand(StartCustomTimer);
        CancelTimerCommand = new RelayCommand(CancelTimer);
        StopwatchToggleCommand = new RelayCommand(ToggleStopwatch);
        StopwatchResetCommand = new RelayCommand(ResetStopwatch);
        TestNotificationCommand = new RelayCommand(() =>
            PushNotification(new NotificationItem("Dynamic Island", "Test notification", "Alerts show up right here.", DateTimeOffset.Now), force: true));
        ResetSettingsCommand = new RelayCommand(() => Settings.Reset());
        OpenSettingsCommand = new RelayCommand(() =>
        {
            ActiveTabIndex = 3;
            ExpandRequested?.Invoke();
        });
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());

        RefreshMonitors();
        UpdateClock();
    }

    public AppSettings Settings { get; }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    public ObservableCollection<CountdownTimer> Timers => _timers.Timers;

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public event Action? ExpandRequested;
    public event Action? CollapseRequested;
    public event Action? QuitRequested;
    public event Action<NotificationItem>? NotificationArrived;

    public ICommand ExpandCommand { get; }
    public ICommand CollapseCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ShowTabCommand { get; }
    public ICommand StartTimerCommand { get; }
    public ICommand StartCustomTimerCommand { get; }
    public ICommand CancelTimerCommand { get; }
    public ICommand StopwatchToggleCommand { get; }
    public ICommand StopwatchResetCommand { get; }
    public ICommand TestNotificationCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand QuitCommand { get; }

    public async Task InitializeAsync()
    {
        _clockTimer.Start();
        await _media.InitializeAsync();
    }

    #region Media

    public string MediaTitle
    {
        get => _mediaTitle;
        private set => SetProperty(ref _mediaTitle, value);
    }

    public string MediaArtist
    {
        get => _mediaArtist;
        private set => SetProperty(ref _mediaArtist, value);
    }

    public string MediaAlbum
    {
        get => _mediaAlbum;
        private set => SetProperty(ref _mediaAlbum, value);
    }

    public string MediaSource
    {
        get => _mediaSource;
        private set => SetProperty(ref _mediaSource, value);
    }

    public ImageSource? MediaArtwork
    {
        get => _mediaArtwork;
        private set
        {
            if (SetProperty(ref _mediaArtwork, value))
                OnPropertyChanged(nameof(HasArtwork));
        }
    }

    public bool HasArtwork => MediaArtwork is not null;

    public bool HasMedia
    {
        get => _hasMedia;
        private set
        {
            if (SetProperty(ref _hasMedia, value))
                OnPropertyChanged(nameof(CompactTitle));
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool HasTimeline
    {
        get => _hasTimeline;
        private set => SetProperty(ref _hasTimeline, value);
    }

    public double MediaProgress
    {
        get => _mediaProgress;
        private set => SetProperty(ref _mediaProgress, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string CompactTitle => HasMedia ? MediaTitle : "Dynamic Island";

    public string CompactSubtitle
    {
        get
        {
            if (!HasMedia)
                return CurrentDate;

            if (!string.IsNullOrWhiteSpace(MediaArtist))
                return MediaArtist;
            if (!string.IsNullOrWhiteSpace(MediaAlbum))
                return MediaAlbum;
            return "Now playing";
        }
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        MediaTitle = _media.Title;
        MediaArtist = _media.Artist;
        MediaAlbum = _media.Album;
        MediaSource = _media.SourceApp;
        MediaArtwork = _media.Artwork;
        HasMedia = _media.HasMedia;
        IsPlaying = _media.IsPlaying;
        HasTimeline = _media.HasTimeline;
        MediaProgress = _media.ProgressPercent;
        PositionText = _media.PositionText;
        DurationText = _media.DurationText;

        OnPropertyChanged(nameof(CompactSubtitle));
    }

    #endregion

    #region Clock / tabs

    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    public string CurrentDate
    {
        get => _currentDate;
        private set
        {
            if (SetProperty(ref _currentDate, value))
                OnPropertyChanged(nameof(CompactSubtitle));
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm:ss");
        CurrentDate = now.ToString("ddd, MMM d");
        if (!HasMedia)
            OnPropertyChanged(nameof(CompactSubtitle));
    }

    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (!SetProperty(ref _activeTabIndex, value))
                return;

            OnPropertyChanged(nameof(IsMediaTab));
            OnPropertyChanged(nameof(IsTimerTab));
            OnPropertyChanged(nameof(IsStatsTab));
            OnPropertyChanged(nameof(IsSettingsTab));
            UpdateStatsActivity();
        }
    }

    public bool IsMediaTab
    {
        get => ActiveTabIndex == 0;
        set { if (value) ActiveTabIndex = 0; }
    }

    public bool IsTimerTab
    {
        get => ActiveTabIndex == 1;
        set { if (value) ActiveTabIndex = 1; }
    }

    public bool IsStatsTab
    {
        get => ActiveTabIndex == 2;
        set { if (value) ActiveTabIndex = 2; }
    }

    public bool IsSettingsTab
    {
        get => ActiveTabIndex == 3;
        set { if (value) ActiveTabIndex = 3; }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private void UpdateStatsActivity()
    {
        if (ActiveTabIndex == 2)
            _stats.Start();
        else
            _stats.Stop();
    }

    private void ShowTab(object? parameter)
    {
        ActiveTabIndex = parameter?.ToString() switch
        {
            "timer" => 1,
            "stats" => 2,
            "settings" => 3,
            _ => 0
        };
        ExpandRequested?.Invoke();
    }

    #endregion

    #region Timers

    public string CustomMinutes
    {
        get => _customMinutes;
        set => SetProperty(ref _customMinutes, value);
    }

    public bool HasTimers
    {
        get => _hasTimers;
        private set => SetProperty(ref _hasTimers, value);
    }

    public string StopwatchText
    {
        get => _stopwatchText;
        private set => SetProperty(ref _stopwatchText, value);
    }

    public bool IsStopwatchRunning
    {
        get => _isStopwatchRunning;
        private set
        {
            if (SetProperty(ref _isStopwatchRunning, value))
                OnPropertyChanged(nameof(StopwatchToggleText));
        }
    }

    public string StopwatchToggleText => IsStopwatchRunning ? "Pause" : "Start";

    public void StartTimer(int minutes)
    {
        if (minutes <= 0)
            minutes = 1;

        _timers.AddTimer(TimeSpan.FromMinutes(minutes));
    }

    private void StartTimerFromParameter(object? parameter)
    {
        if (int.TryParse(parameter?.ToString(), out var minutes))
            StartTimer(minutes);
    }

    private void StartCustomTimer()
    {
        if (!double.TryParse(CustomMinutes, out var minutes) || minutes <= 0)
            minutes = 5;

        if (minutes > 24 * 60)
            minutes = 24 * 60;

        _timers.AddTimer(TimeSpan.FromMinutes(minutes));
    }

    private void CancelTimer(object? parameter)
    {
        if (parameter is CountdownTimer timer)
            _timers.CancelTimer(timer);
    }

    private void ToggleStopwatch()
    {
        if (_timers.IsStopwatchRunning)
            _timers.PauseStopwatch();
        else
            _timers.StartStopwatch();
    }

    private void ResetStopwatch() => _timers.ResetStopwatch();

    private void OnTimersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => HasTimers = _timers.Timers.Count > 0;

    private void OnStopwatchChanged(object? sender, EventArgs e)
    {
        StopwatchText = FormatStopwatch(_timers.StopwatchElapsed);
        IsStopwatchRunning = _timers.IsStopwatchRunning;
    }

    private void OnTimerFinished(object? sender, CountdownTimer timer)
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
            // Audio may be unavailable.
        }

        if (Settings.PauseMediaOnTimerEnd && _media.IsPlaying)
            _ = _media.TogglePlayPauseAsync();

        PushNotification(
            new NotificationItem("Timer", timer.Label, "Time's up!", DateTimeOffset.Now),
            force: true);
    }

    private static string FormatStopwatch(TimeSpan time)
    {
        var hundredths = time.Milliseconds / 10;
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{hundredths:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{hundredths:00}";
    }

    #endregion

    #region Stats

    public double CpuPercent
    {
        get => _cpuPercent;
        private set => SetProperty(ref _cpuPercent, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set => SetProperty(ref _memoryPercent, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public string NetworkDown
    {
        get => _networkDown;
        private set => SetProperty(ref _networkDown, value);
    }

    public string NetworkUp
    {
        get => _networkUp;
        private set => SetProperty(ref _networkUp, value);
    }

    public bool HasBattery
    {
        get => _hasBattery;
        private set => SetProperty(ref _hasBattery, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => SetProperty(ref _isCharging, value);
    }

    public double BatteryPercent
    {
        get => _batteryPercent;
        private set => SetProperty(ref _batteryPercent, value);
    }

    public string BatteryText
    {
        get => _batteryText;
        private set => SetProperty(ref _batteryText, value);
    }

    private void OnStatsUpdated(object? sender, SystemStats stats)
    {
        CpuPercent = stats.CpuPercent;
        CpuText = $"{stats.CpuPercent:0}%";
        MemoryPercent = stats.MemoryPercent;
        MemoryText = $"{stats.UsedMemoryBytes / 1024.0 / 1024.0 / 1024.0:0.0} / " +
                     $"{stats.TotalMemoryBytes / 1024.0 / 1024.0 / 1024.0:0.0} GB";
        NetworkDown = FormatRate(stats.DownloadBytesPerSecond);
        NetworkUp = FormatRate(stats.UploadBytesPerSecond);
        HasBattery = stats.HasBattery;
        IsCharging = stats.IsCharging;
        BatteryPercent = stats.BatteryPercent ?? 0;
        BatteryText = stats.HasBattery
            ? $"{stats.BatteryPercent}%{(stats.IsCharging ? " · Charging" : string.Empty)}"
            : "No battery";
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
            return $"{bytesPerSecond:0} B/s";
        if (bytesPerSecond < 1024 * 1024)
            return $"{bytesPerSecond / 1024:0.0} KB/s";
        return $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";
    }

    #endregion

    #region Notifications

    public string PeekApp
    {
        get => _peekApp;
        private set => SetProperty(ref _peekApp, value);
    }

    public string PeekTitle
    {
        get => _peekTitle;
        private set => SetProperty(ref _peekTitle, value);
    }

    public string PeekBody
    {
        get => _peekBody;
        private set => SetProperty(ref _peekBody, value);
    }

    public string PeekTime
    {
        get => _peekTime;
        private set => SetProperty(ref _peekTime, value);
    }

    public void SetPeekNotification(NotificationItem item)
    {
        PeekApp = item.AppName;
        PeekTitle = string.IsNullOrWhiteSpace(item.Title) ? item.AppName : item.Title;
        PeekBody = item.Body;
        PeekTime = item.Timestamp.ToLocalTime().ToString("HH:mm");
    }

    /// <summary>Shows an alert in the island immediately (used by CLI and the tray).</summary>
    public void ShowNotification(string title, string body, string appName = "Dynamic Island")
        => PushNotification(new NotificationItem(appName, title, body, DateTimeOffset.Now), force: true);

    private void OnNotificationReceived(NotificationItem item)
        => PushNotification(item);

    private void PushNotification(NotificationItem item, bool force = false)
    {
        if (!force && !Settings.NotificationsEnabled)
            return;

        NotificationArrived?.Invoke(item);
    }

    #endregion

    public void RefreshMonitors()
    {
        var monitors = MonitorHelper.GetMonitors();
        Monitors.Clear();
        foreach (var monitor in monitors)
            Monitors.Add(monitor);

        if (monitors.Count == 0)
            return;

        var current = Settings.MonitorDeviceName;
        if (string.IsNullOrWhiteSpace(current) || monitors.All(m => m.DeviceName != current))
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            Settings.MonitorDeviceName = primary.DeviceName;
        }
    }
}
