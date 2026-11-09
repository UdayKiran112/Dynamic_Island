using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland.Core;

/// <summary>
/// Detects whether a fullscreen app (game, video, presentation) owns the screen.
/// Uses a foreground WinEvent hook plus a slow safety poll for cases where the
/// same window changes its own size.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private readonly DispatcherTimer _safetyTimer;
    private readonly NativeMethods.WinEventDelegate _callback;
    private IntPtr _hook;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenChanged;

    public bool IsFullscreen { get; private set; }

    public FullscreenWatcher()
    {
        _callback = OnForegroundChanged;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _safetyTimer.Tick += (_, _) => Evaluate();
        _safetyTimer.Start();

        Evaluate();
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        Application.Current?.Dispatcher.BeginInvoke(Evaluate);
    }

    private void Evaluate()
    {
        if (_disposed)
            return;

        var fullscreen = QueryNotificationState() || ForegroundCoversMonitor();
        if (fullscreen == IsFullscreen)
            return;

        IsFullscreen = fullscreen;
        FullscreenChanged?.Invoke(this, fullscreen);
    }

    private static bool QueryNotificationState()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0)
                return false;

            // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
            return state is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }

    private static bool ForegroundCoversMonitor()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        var className = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        var name = className.ToString();
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        const int tolerance = 2;
        return windowRect.Left <= info.rcMonitor.Left + tolerance &&
               windowRect.Top <= info.rcMonitor.Top + tolerance &&
               windowRect.Right >= info.rcMonitor.Right - tolerance &&
               windowRect.Bottom >= info.rcMonitor.Bottom - tolerance;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _safetyTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
