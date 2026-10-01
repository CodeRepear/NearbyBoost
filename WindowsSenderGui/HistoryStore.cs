using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NearbyBoostSenderGui;

public class HistoryRecord
{
    public string Time { get; set; } = "";
    public string Direction { get; set; } = ""; // "Sent" | "Received"
    public string Name { get; set; } = "";
    public string Peer { get; set; } = "";
    public string Size { get; set; } = "";
    public string AvgSpeed { get; set; } = "";
    public string Status { get; set; } = "";
    /// <summary>Full local path to the file/folder — the received save location, or the sent
    /// source location. Null if unknown (e.g. a rejected/failed transfer before anything landed).</summary>
    public string? LocalPath { get; set; }
}

public static class HistoryStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NearbyBoost", "history.json");

    public static List<HistoryRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(FilePath));
                if (list != null) return list;
            }
        }
        catch { /* ignore, start fresh */ }
        return new List<HistoryRecord>();
    }

    public static void Add(List<HistoryRecord> current, HistoryRecord record)
    {
        current.Insert(0, record);
        while (current.Count > 100) current.RemoveAt(current.Count - 1);
        Save(current);
    }

    public static void Save(List<HistoryRecord> list)
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
