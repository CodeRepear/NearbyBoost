using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NearbyBoostSenderGui;

public class KnownDeviceRecord
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Devices this app has successfully discovered or transferred with before,
/// so they show up again next time without needing a fresh scan first.</summary>
public static class KnownDevices
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NearbyBoost", "known_devices.json");

    public static List<KnownDeviceRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<KnownDeviceRecord>>(File.ReadAllText(FilePath));
                if (list != null) return list;
            }
        }
        catch { /* ignore, start fresh */ }
        return new List<KnownDeviceRecord>();
    }

    public static void Remember(string deviceId, string name, string ip, int port)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        var list = Load();
        var existing = list.FirstOrDefault(d => d.DeviceId == deviceId);
        if (existing != null)
        {
            existing.Name = name;
            existing.Ip = ip;
            existing.Port = port;
            existing.LastSeenUtc = DateTime.UtcNow;
        }
        else
        {
            list.Add(new KnownDeviceRecord { DeviceId = deviceId, Name = name, Ip = ip, Port = port, LastSeenUtc = DateTime.UtcNow });
        }
        Save(list);
    }

    private static void Save(List<KnownDeviceRecord> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch { /* best effort */ }
    }
}
