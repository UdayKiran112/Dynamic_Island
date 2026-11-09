using System.Text.Json.Serialization;

namespace DynamicIsland.Core;

public enum AppTheme
{
    Dark,
    Light,
    Aurora
}

/// <summary>
/// All user preferences. The object is serialized as-is to
/// %APPDATA%\DynamicIsland\settings.json.
/// </summary>
public sealed class AppSettings : ObservableObject
{
    private AppTheme _theme = AppTheme.Dark;
    private double _scale = 1.0;
    private double _backgroundOpacity = 0.94;
    private string _monitorDeviceName = string.Empty;
    private double _topOffset = 10;
    private bool _autoHideFullscreen = true;
    private bool _hoverToExpand = true;
    private bool _notificationsEnabled = true;
    private bool _startWithWindows;
    private bool _enableHotkeys = true;
    private int _notificationDurationSeconds = 6;
    private bool _showClock = true;
    private bool _pauseMediaOnTimerEnd;

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public double Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, Math.Clamp(value, 0.75, 1.5));
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => SetProperty(ref _backgroundOpacity, Math.Clamp(value, 0.35, 1.0));
    }

    /// <summary>WinForms device name, e.g. \\.\DISPLAY1. Empty means the primary display.</summary>
    public string MonitorDeviceName
    {
        get => _monitorDeviceName;
        set => SetProperty(ref _monitorDeviceName, value ?? string.Empty);
    }

    /// <summary>Distance in DIPs from the top edge of the monitor work area.</summary>
    public double TopOffset
    {
        get => _topOffset;
        set => SetProperty(ref _topOffset, Math.Clamp(value, 0, 300));
    }

    public bool AutoHideFullscreen
    {
        get => _autoHideFullscreen;
        set => SetProperty(ref _autoHideFullscreen, value);
    }

    public bool HoverToExpand
    {
        get => _hoverToExpand;
        set => SetProperty(ref _hoverToExpand, value);
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => SetProperty(ref _notificationsEnabled, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool EnableHotkeys
    {
        get => _enableHotkeys;
        set => SetProperty(ref _enableHotkeys, value);
    }

    public int NotificationDurationSeconds
    {
        get => _notificationDurationSeconds;
        set => SetProperty(ref _notificationDurationSeconds, Math.Clamp(value, 2, 30));
    }

    public bool ShowClock
    {
        get => _showClock;
        set => SetProperty(ref _showClock, value);
    }

    public bool PauseMediaOnTimerEnd
    {
        get => _pauseMediaOnTimerEnd;
        set => SetProperty(ref _pauseMediaOnTimerEnd, value);
    }

    /// <summary>Restores factory defaults in place so existing bindings stay valid.</summary>
    public void Reset()
    {
        var defaults = new AppSettings();
        Theme = defaults.Theme;
        Scale = defaults.Scale;
        BackgroundOpacity = defaults.BackgroundOpacity;
        MonitorDeviceName = defaults.MonitorDeviceName;
        TopOffset = defaults.TopOffset;
        AutoHideFullscreen = defaults.AutoHideFullscreen;
        HoverToExpand = defaults.HoverToExpand;
        NotificationsEnabled = defaults.NotificationsEnabled;
        StartWithWindows = defaults.StartWithWindows;
        EnableHotkeys = defaults.EnableHotkeys;
        NotificationDurationSeconds = defaults.NotificationDurationSeconds;
        ShowClock = defaults.ShowClock;
        PauseMediaOnTimerEnd = defaults.PauseMediaOnTimerEnd;
    }
}

internal static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
