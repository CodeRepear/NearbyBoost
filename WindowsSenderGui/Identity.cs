using System;
using System.IO;
using System.Text.Json;

namespace NearbyBoostSenderGui;

public class IdentityData
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public int AvatarIndex { get; set; } = 0;
}

/// <summary>Loads/creates a persistent device identity (survives reinstalls of NearbyBoost
/// itself as long as %AppData% isn't wiped) so other devices can recognize this one across
/// sessions even if its IP address changes.</summary>
public static class Identity
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NearbyBoost", "identity.json");

    private static IdentityData? _cached;

    public static IdentityData Load()
    {
        if (_cached != null) return _cached;
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<IdentityData>(File.ReadAllText(FilePath));
                if (data != null && !string.IsNullOrEmpty(data.DeviceId))
                {
                    _cached = data;
                    return data;
                }
            }
        }
        catch { /* fall through to create fresh */ }

        var fresh = new IdentityData();
        Save(fresh);
        return fresh;
    }

    public static void Save(IdentityData data)
    {
        _cached = data;
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch { /* best effort — identity just won't persist this run */ }
    }
}
