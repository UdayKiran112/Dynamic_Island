using Forms = System.Windows.Forms;

namespace DynamicIsland.Core;

public sealed record MonitorInfo(string DeviceName, string DisplayName, bool IsPrimary, int Width, int Height);

public static class MonitorHelper
{
    public static List<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        var screens = Forms.Screen.AllScreens;

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = $"Display {i + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}";
            if (screen.Primary)
                label += " (Primary)";

            result.Add(new MonitorInfo(screen.DeviceName, label, screen.Primary, screen.Bounds.Width, screen.Bounds.Height));
        }

        return result;
    }

    public static Forms.Screen GetScreen(string? deviceName)
    {
        var screens = Forms.Screen.AllScreens;
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = screens.FirstOrDefault(s => s.DeviceName == deviceName);
            if (match is not null)
                return match;
        }

        return Forms.Screen.PrimaryScreen ?? screens[0];
    }
}
