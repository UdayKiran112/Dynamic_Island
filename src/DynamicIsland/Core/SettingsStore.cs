using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DynamicIsland.Core;

/// <summary>Loads and saves settings under %APPDATA%\DynamicIsland.</summary>
public static class SettingsStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not load settings: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(settings, JsonDefaults.Options);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}
