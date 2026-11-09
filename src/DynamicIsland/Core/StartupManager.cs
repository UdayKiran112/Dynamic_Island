using System.Diagnostics;
using Microsoft.Win32;

namespace DynamicIsland.Core;

/// <summary>Registers the app under HKCU\...\Run when "Start with Windows" is enabled.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DynamicIsland";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Startup registration failed: {ex.Message}");
        }
    }
}
