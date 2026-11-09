using System.Drawing.Drawing2D;
using DynamicIsland.Core;
using DynamicIsland.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynamicIsland;

/// <summary>System tray icon and context menu. Uses the WinForms NotifyIcon.</summary>
public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private bool _disposed;

    public TrayService(MainViewModel viewModel, MainWindow window, Action quit)
    {
        _window = window;
        _icon = CreateIcon();

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };

        _menu.Items.Add(CreateItem("Show island", () => _window.ShowIsland()));
        _menu.Items.Add(CreateItem("Expand / collapse", () => _window.ToggleIsland()));
        _menu.Items.Add(CreateItem("Play / pause", () => viewModel.PlayPauseCommand.Execute(null)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("Timer · 5 minutes", () => viewModel.StartTimer(5)));
        _menu.Items.Add(CreateItem("Settings", () => _window.OpenSettings()));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateItem("Exit", quit));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Dynamic Island",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatch(() => _window.ToggleIsland());
    }

    private Forms.ToolStripMenuItem CreateItem(string text, Action action)
        => new(text, null, (_, _) => Dispatch(action));

    private void Dispatch(Action action)
    {
        if (_window.Dispatcher.CheckAccess())
            action();
        else
            _window.Dispatcher.BeginInvoke(action);
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);

            using var path = RoundedRectangle(new Drawing.RectangleF(2, 9, 28, 14), 7);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 124, 92, 255));
            graphics.FillPath(brush, path);

            using var dot = new Drawing.SolidBrush(Drawing.Color.White);
            graphics.FillEllipse(dot, 13, 14, 6, 6);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(Drawing.RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
