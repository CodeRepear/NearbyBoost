using System;
using System.IO;
using System.Text.Json;

namespace NearbyBoostSenderGui;

public class AppSettingsData
{
    public string ThemeMode { get; set; } = "light"; // "light" | "dark" | "system"
    public bool ParallelTransferEnabled { get; set; } = true;
}

public static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NearbyBoost", "settings.json");

    private static AppSettingsData? _cached;

    public static AppSettingsData Load()
    {
        if (_cached != null) return _cached;
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(FilePath));
                if (data != null) { _cached = data; return data; }
            }
        }
        catch { /* fall through to defaults */ }

        var fresh = new AppSettingsData();
        _cached = fresh;
        return fresh;
    }

    public static void Save(AppSettingsData data)
    {
        _cached = data;
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch { /* best effort */ }
    }
}
