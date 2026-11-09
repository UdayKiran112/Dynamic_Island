using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;
using DynamicIsland.ViewModels;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private enum IslandState
    {
        Compact,
        Peek,
        Expanded
    }

    private const int HotkeyToggle = 0xD101;
    private const int HotkeyMedia = 0xD102;
    private const int HotkeyTimer = 0xD103;

    private const double BaseCompactWidth = 320;
    private const double BaseCompactHeight = 46;
    private const double BaseExpandedWidth = 440;
    private const double BaseExpandedHeight = 480;
    private const double BasePeekHeight = 104;

    // Predefined hover activation zone at the top-centre of the display.
    private const double ActivationWidth = 380;
    private const double ActivationHeight = 80;
    private const int HoverExitGraceMs = 550;

    private static readonly TimeSpan AutoCollapseDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExplicitShowHold = TimeSpan.FromSeconds(20);

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _inlineTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private readonly Queue<NotificationItem> _notificationQueue = new();

    private IslandState _state = IslandState.Compact;
    private bool _showingPeek;
    private bool _isHiddenForFullscreen;
    private bool _isHoverHidden = true;
    private bool _isHiding;
    private bool _pinnedVisible;
    private DateTime _pinExpiresUtc;
    private DateTime? _cursorOutsideSince;
    private bool _allowClose;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private FullscreenWatcher? _fullscreen;
    private int _animationToken;
    private int _visibilityToken;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        _peekTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _peekTimer.Tick += (_, _) => DismissPeek();

        _inlineTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _inlineTimer.Tick += (_, _) =>
        {
            _inlineTimer.Stop();
            InlineBanner.Visibility = Visibility.Collapsed;
        };

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_settings.HoverToExpand && _state == IslandState.Compact && !_showingPeek)
                Expand();
        };

        _autoCollapseTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = AutoCollapseDelay
        };
        _autoCollapseTimer.Tick += OnAutoCollapseTick;

        _visibilityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _visibilityTimer.Tick += OnVisibilityTick;

        _viewModel.ExpandRequested += Expand;
        _viewModel.CollapseRequested += Collapse;
        _viewModel.NotificationArrived += OnNotificationArrived;
        _settings.PropertyChanged += OnSettingsChanged;

        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) => ApplyLayout(false);
        DpiChanged += (_, _) => ApplyLayout(false);
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        MouseMove += OnWindowMouseMove;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        PreviewMouseWheel += OnWindowPreviewMouseWheel;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    public void PrepareForShutdown() => _allowClose = true;

    /// <summary>
    /// Creates the window handle while keeping the island hidden. The island is
    /// revealed by the hover watcher, the tray menu or a notification.
    /// </summary>
    public void InitializeHidden()
    {
        _isHoverHidden = true;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Show();
        Hide();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    public void ShowIsland()
    {
        _isHiddenForFullscreen = false;
        Reveal(pin: true);
    }

    public void ToggleIsland()
    {
        if (_state == IslandState.Expanded)
        {
            Collapse();
            return;
        }

        Reveal(pin: true);
        Expand();
    }

    public void Expand()
    {
        if (!IsVisible || _isHoverHidden || _isHiding)
            Reveal();

        _peekTimer.Stop();
        _inlineTimer.Stop();
        InlineBanner.Visibility = Visibility.Collapsed;
        _showingPeek = false;
        _notificationQueue.Clear();

        if (_state == IslandState.Expanded)
        {
            RestartAutoCollapse();
            return;
        }

        _state = IslandState.Expanded;
        _viewModel.IsExpanded = true;
        SetContentVisibility(IslandState.Expanded);
        ApplyLayout(true);
        RestartAutoCollapse();
    }

    public void Collapse()
    {
        _pinnedVisible = false;
        StopAutoCollapse();
        _peekTimer.Stop();
        _inlineTimer.Stop();
        InlineBanner.Visibility = Visibility.Collapsed;
        _showingPeek = false;
        _notificationQueue.Clear();

        if (_state == IslandState.Compact)
            return;

        _state = IslandState.Compact;
        _viewModel.IsExpanded = false;
        SetContentVisibility(IslandState.Compact);
        ApplyLayout(true);
    }

    public void OpenSettings()
    {
        _viewModel.ActiveTabIndex = 3;
        Reveal(pin: true);
        Expand();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        style = (style | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        UpdateFullscreenWatcher();
        RefreshHotkeys();
        ApplyLayout(false);
        _visibilityTimer.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _viewModel.RefreshMonitors();
            ApplyLayout(false);
        });
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Scale):
            case nameof(AppSettings.MonitorDeviceName):
            case nameof(AppSettings.TopOffset):
                ApplyLayout(false);
                break;
            case nameof(AppSettings.AutoHideFullscreen):
                UpdateFullscreenWatcher();
                break;
            case nameof(AppSettings.EnableHotkeys):
                RefreshHotkeys();
                break;
        }
    }

    #region Layout / animation

    private void ApplyLayout(bool animate)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var (left, top, width, height) = ComputeLayout(_state);
        var cornerRadius = ComputeCornerRadius(_state, height);

        if (!animate || !IsVisible)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Shell.BeginAnimation(Border.CornerRadiusProperty, null);
            Shell.CornerRadius = new CornerRadius(cornerRadius);
            ClearAnimations();
            return;
        }

        AnimateTo(left, top, width, height, cornerRadius);
    }

    private double ComputeCornerRadius(IslandState state, double height) => state switch
    {
        IslandState.Compact => height / 2,
        IslandState.Peek => Math.Min(height / 2, 26 * _settings.Scale),
        _ => Math.Min(height / 2, 30 * _settings.Scale)
    };

    private (double Left, double Top, double Width, double Height) ComputeLayout(IslandState state)
    {
        var scale = _settings.Scale;

        var width = (state == IslandState.Compact ? BaseCompactWidth : BaseExpandedWidth) * scale;
        var height = state switch
        {
            IslandState.Compact => BaseCompactHeight * scale,
            IslandState.Peek => BasePeekHeight * scale,
            _ => BaseExpandedHeight * scale
        };

        var screen = MonitorHelper.GetScreen(_settings.MonitorDeviceName);
        var dpiScale = GetDpiScaleForScreen(screen);
        var work = screen.WorkingArea;

        var left = work.Left / dpiScale + (work.Width / dpiScale - width) / 2;
        var top = work.Top / dpiScale + _settings.TopOffset;

        return (left, top, width, height);
    }

    private double GetDpiScaleForScreen(System.Windows.Forms.Screen screen)
    {
        var point = new NativeMethods.POINT
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2
        };

        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        var windowDpi = _hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_hwnd) : 0;
        return windowDpi > 0 ? windowDpi / 96.0 : 1.0;
    }

    private void AnimateTo(double left, double top, double width, double height, double cornerRadius)
    {
        var fromLeft = Left;
        var fromTop = Top;
        var fromWidth = Width;
        var fromHeight = Height;

        if (double.IsNaN(fromLeft)) fromLeft = left;
        if (double.IsNaN(fromTop)) fromTop = top;
        if (double.IsNaN(fromWidth)) fromWidth = width;
        if (double.IsNaN(fromHeight)) fromHeight = height;

        var token = ++_animationToken;
        var duration = TimeSpan.FromMilliseconds(300);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var widthAnimation = new DoubleAnimation(fromWidth, width, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var heightAnimation = new DoubleAnimation(fromHeight, height, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var leftAnimation = new DoubleAnimation(fromLeft, left, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var topAnimation = new DoubleAnimation(fromTop, top, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        Shell.CornerRadius = new CornerRadius(cornerRadius);

        heightAnimation.Completed += (_, _) =>
        {
            if (token != _animationToken)
                return;

            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Shell.CornerRadius = new CornerRadius(cornerRadius);
            ClearAnimations();
        };

        BeginAnimation(WidthProperty, widthAnimation);
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(LeftProperty, leftAnimation);
        BeginAnimation(TopProperty, topAnimation);
    }

    private void ClearAnimations()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    private void SetContentVisibility(IslandState state)
    {
        CompactRoot.Visibility = state == IslandState.Compact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedRoot.Visibility = state == IslandState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        PeekRoot.Visibility = state == IslandState.Peek ? Visibility.Visible : Visibility.Collapsed;

        var target = state switch
        {
            IslandState.Compact => (FrameworkElement)CompactRoot,
            IslandState.Peek => PeekRoot,
            _ => ExpandedRoot
        };

        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    #endregion

    #region Notifications

    private void OnNotificationArrived(NotificationItem item)
    {
        if (_isHiddenForFullscreen)
            return;

        if (_state == IslandState.Expanded && IsVisible && !_isHoverHidden)
        {
            ShowInlineNotification(item);
            return;
        }

        // Alerts should appear even when the island is resting out of sight.
        Reveal();
        _notificationQueue.Enqueue(item);
        if (!_showingPeek)
            ShowNextPeek();
    }

    private void ShowNextPeek()
    {
        if (_notificationQueue.Count == 0)
        {
            _showingPeek = false;
            return;
        }

        var item = _notificationQueue.Dequeue();
        _showingPeek = true;
        _viewModel.SetPeekNotification(item);

        StopAutoCollapse();
        _state = IslandState.Peek;
        SetContentVisibility(IslandState.Peek);
        ApplyLayout(true);

        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(_settings.NotificationDurationSeconds);
        _peekTimer.Start();
    }

    private void DismissPeek(bool expand = false)
    {
        _peekTimer.Stop();
        StopAutoCollapse();

        if (expand)
        {
            _showingPeek = false;
            _notificationQueue.Clear();
            Expand();
            return;
        }

        if (_notificationQueue.Count > 0)
        {
            ShowNextPeek();
            return;
        }

        _showingPeek = false;

        if (_state == IslandState.Peek)
        {
            _state = IslandState.Compact;
            SetContentVisibility(IslandState.Compact);
            ApplyLayout(true);
        }
    }

    private void ShowInlineNotification(NotificationItem item)
    {
        var text = string.IsNullOrWhiteSpace(item.Body)
            ? $"{item.AppName}: {item.Title}"
            : $"{item.AppName}: {item.Title} — {item.Body}";

        InlineBannerText.Text = text;
        InlineBanner.Visibility = Visibility.Visible;

        _inlineTimer.Stop();
        _inlineTimer.Interval = TimeSpan.FromSeconds(_settings.NotificationDurationSeconds);
        _inlineTimer.Start();
        RestartAutoCollapse();
    }

    private void CompactRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state == IslandState.Compact)
            Expand();

        e.Handled = true;
    }

    private void PeekRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        DismissPeek(expand: true);
        e.Handled = true;
    }

    private void PeekClose_Click(object sender, RoutedEventArgs e)
    {
        DismissPeek();
        e.Handled = true;
    }

    #endregion

    #region Hover / fullscreen / hotkeys

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _cursorOutsideSince = null;
        _pinnedVisible = false;
        RestartAutoCollapse();

        if (!_settings.HoverToExpand || _state != IslandState.Compact || _showingPeek)
            return;

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer.Stop();
        RestartAutoCollapse();
        _cursorOutsideSince ??= DateTime.UtcNow;
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e) => RestartAutoCollapse();

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e) => RestartAutoCollapse();

    private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e) => RestartAutoCollapse();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e) => RestartAutoCollapse();

    private void OnVisibilityTick(object? sender, EventArgs e)
    {
        if (_isHiddenForFullscreen || _hwnd == IntPtr.Zero)
            return;

        if (!NativeMethods.GetCursorPos(out var cursor))
            return;

        var (inActivation, inIsland) = GetCursorZones(cursor);

        if (_isHoverHidden || !IsVisible)
        {
            if (inActivation)
                Reveal();

            return;
        }

        if (_pinnedVisible)
        {
            if (inIsland || inActivation)
            {
                // The pointer has reached the island; hand over to normal hover behaviour.
                _pinnedVisible = false;
            }
            else if (DateTime.UtcNow >= _pinExpiresUtc)
            {
                _pinnedVisible = false;
            }
        }

        var keepVisible = inIsland
            || inActivation
            || HasOpenComboBox(this)
            || Mouse.Captured is not null
            || _state == IslandState.Peek
            || _pinnedVisible;

        if (keepVisible)
        {
            _cursorOutsideSince = null;
            return;
        }

        _cursorOutsideSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _cursorOutsideSince.Value).TotalMilliseconds < HoverExitGraceMs)
            return;

        _cursorOutsideSince = null;
        HideSmooth();
    }

    private (bool InActivation, bool InIsland) GetCursorZones(NativeMethods.POINT cursor)
    {
        var screen = MonitorHelper.GetScreen(_settings.MonitorDeviceName);
        var dpiScale = GetDpiScaleForScreen(screen);
        var work = screen.WorkingArea;

        var activationWidth = ActivationWidth * _settings.Scale * dpiScale;
        var activationHeight = (_settings.TopOffset + ActivationHeight * _settings.Scale) * dpiScale;
        var activationLeft = work.Left + (work.Width - activationWidth) / 2.0;

        // Reach all the way to the top edge so the island can be summoned by
        // pushing the pointer towards the top-centre of the screen.
        var activationTop = work.Top;

        var inActivation = cursor.X >= activationLeft &&
                           cursor.X <= activationLeft + activationWidth &&
                           cursor.Y >= activationTop &&
                           cursor.Y <= activationTop + activationHeight;

        var inIsland = false;
        if (IsVisible && NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            var margin = (int)Math.Round(8 * dpiScale);
            inIsland = cursor.X >= rect.Left - margin &&
                       cursor.X <= rect.Right + margin &&
                       cursor.Y >= rect.Top - margin &&
                       cursor.Y <= rect.Bottom + margin;
        }

        return (inActivation, inIsland);
    }

    private bool IsCursorOverIsland()
    {
        if (!IsVisible ||
            !NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(_hwnd, out var rect))
        {
            return false;
        }

        return cursor.X >= rect.Left &&
               cursor.X <= rect.Right &&
               cursor.Y >= rect.Top &&
               cursor.Y <= rect.Bottom;
    }

    private void Reveal(bool pin = false)
    {
        if (_isHiddenForFullscreen)
            return;

        if (pin)
        {
            _pinnedVisible = true;
            _pinExpiresUtc = DateTime.UtcNow + ExplicitShowHold;
        }

        _isHoverHidden = false;
        _cursorOutsideSince = null;
        _isHiding = false;
        _visibilityToken++;

        if (!IsVisible)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            ResetShellScale(0.94);
            ApplyLayout(false);
            Show();
        }
        else
        {
            var currentOpacity = Opacity;
            BeginAnimation(OpacityProperty, null);
            Opacity = Math.Max(currentOpacity, 0.05);
        }

        Topmost = true;
        FadeTo(1, 200);
        AnimateShellScale(1, 240);

        if (_state == IslandState.Expanded)
        {
            RestartAutoCollapse();
        }
        else if (_settings.HoverToExpand && _state == IslandState.Compact && IsCursorOverIsland())
        {
            _hoverTimer.Stop();
            _hoverTimer.Start();
        }
    }

    private void HideSmooth()
    {
        if (!IsVisible || _isHiddenForFullscreen || _isHiding)
            return;

        _isHiding = true;
        _isHoverHidden = true;
        _pinnedVisible = false;
        _cursorOutsideSince = null;
        StopAutoCollapse();
        _peekTimer.Stop();
        _inlineTimer.Stop();
        InlineBanner.Visibility = Visibility.Collapsed;
        _showingPeek = false;
        _notificationQueue.Clear();

        var token = ++_visibilityToken;

        if (_state != IslandState.Compact)
        {
            _state = IslandState.Compact;
            _viewModel.IsExpanded = false;
            SetContentVisibility(IslandState.Compact);
            ApplyLayout(true);
        }

        AnimateShellScale(0.96, 180);
        FadeTo(0, 200, () =>
        {
            if (token != _visibilityToken)
                return;

            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _isHiding = false;
            ResetToCompact();
        });
    }

    private void ResetToCompact()
    {
        _state = IslandState.Compact;
        _viewModel.IsExpanded = false;
        SetContentVisibility(IslandState.Compact);
        ApplyLayout(false);
        ResetShellScale(1);
    }

    private void FadeTo(double to, int milliseconds, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };

        if (completed is not null)
            animation.Completed += (_, _) => completed();

        BeginAnimation(OpacityProperty, animation);
    }

    private void AnimateShellScale(double to, int milliseconds)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scaleY = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void ResetShellScale(double value)
    {
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShellScale.ScaleX = value;
        ShellScale.ScaleY = value;
    }

    private void RestartAutoCollapse()
    {
        if (_state != IslandState.Expanded || _isHiddenForFullscreen)
            return;

        _autoCollapseTimer.Stop();
        _autoCollapseTimer.Start();
    }

    private void StopAutoCollapse() => _autoCollapseTimer.Stop();

    private void OnAutoCollapseTick(object? sender, EventArgs e)
    {
        _autoCollapseTimer.Stop();

        if (_state != IslandState.Expanded || _isHiddenForFullscreen)
            return;

        // Do not collapse in the middle of choosing a value from a drop-down.
        // Any real input (mouse move, click, wheel or key) restarts the countdown.
        if (HasOpenComboBox(this))
        {
            _autoCollapseTimer.Start();
            return;
        }

        Collapse();
    }

    private static bool HasOpenComboBox(DependencyObject root)
    {
        if (root is System.Windows.Controls.ComboBox { IsDropDownOpen: true })
            return true;

        if (root is not Visual)
            return false;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (HasOpenComboBox(VisualTreeHelper.GetChild(root, i)))
                return true;
        }

        return false;
    }

    private void UpdateFullscreenWatcher()
    {
        if (_settings.AutoHideFullscreen)
        {
            if (_fullscreen is null)
            {
                _fullscreen = new FullscreenWatcher();
                _fullscreen.FullscreenChanged += OnFullscreenChanged;
            }
        }
        else
        {
            if (_fullscreen is not null)
            {
                _fullscreen.FullscreenChanged -= OnFullscreenChanged;
                _fullscreen.Dispose();
                _fullscreen = null;
            }

            if (_isHiddenForFullscreen)
            {
                _isHiddenForFullscreen = false;
                _isHoverHidden = true;
            }
        }
    }

    private void OnFullscreenChanged(object? sender, bool isFullscreen)
    {
        if (!_settings.AutoHideFullscreen)
            return;

        if (isFullscreen)
        {
            _isHiddenForFullscreen = true;
            _isHoverHidden = true;
            _isHiding = false;
            _pinnedVisible = false;
            _visibilityToken++;
            StopAutoCollapse();

            if (IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                Hide();
            }
        }
        else if (_isHiddenForFullscreen)
        {
            // Stay out of sight; the cursor watcher reveals the island on hover.
            _isHiddenForFullscreen = false;
            _isHoverHidden = true;
        }
    }

    public void RefreshHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        UnregisterHotkeys();

        if (!_settings.EnableHotkeys)
            return;

        const uint modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        NativeMethods.RegisterHotKey(_hwnd, HotkeyToggle, modifiers, 0x44); // D
        NativeMethods.RegisterHotKey(_hwnd, HotkeyMedia, modifiers, 0x4D);  // M
        NativeMethods.RegisterHotKey(_hwnd, HotkeyTimer, modifiers, 0x54);  // T
    }

    private void UnregisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyToggle);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyMedia);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyTimer);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyToggle:
                    ToggleIsland();
                    handled = true;
                    break;
                case HotkeyMedia:
                    _viewModel.PlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case HotkeyTimer:
                    _viewModel.StartTimer(5);
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    #endregion

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmooth();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        _viewModel.ExpandRequested -= Expand;
        _viewModel.CollapseRequested -= Collapse;
        _viewModel.NotificationArrived -= OnNotificationArrived;
        _settings.PropertyChanged -= OnSettingsChanged;

        _peekTimer.Stop();
        _inlineTimer.Stop();
        _hoverTimer.Stop();
        _autoCollapseTimer.Stop();
        _visibilityTimer.Stop();

        UnregisterHotkeys();
        _source?.RemoveHook(WndProc);
        _source = null;

        _fullscreen?.Dispose();
        _fullscreen = null;
    }
}
