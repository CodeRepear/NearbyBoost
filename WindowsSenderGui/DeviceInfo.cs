using System;
using System.ComponentModel;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace NearbyBoostSenderGui;

/// <summary>Deterministic colored avatar (no image transfer needed) — same key always
/// produces the same color, so a device's avatar looks consistent every time it's seen.</summary>
public static class AvatarColor
{
    private static readonly string[] Palette =
    {
        "#7C5CFC", "#FF6B6B", "#4ECDC4", "#FFD166", "#06D6A0", "#118AB2", "#EF476F", "#8AC926"
    };

    public static SolidColorBrush ForKey(string key)
    {
        int hash = 0;
        foreach (char c in key) hash = (hash * 31 + c) & 0x7FFFFFFF;
        string hex = Palette[hash % Palette.Length];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A device seen via discovery or remembered from a past transfer.</summary>
public class DeviceInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string DeviceId { get; }
    public string Name { get; }
    public Brush AvatarBrush { get; }
    public string Initial { get; }

    private string _ip;
    public string Ip { get => _ip; set { _ip = value; Notify(nameof(Ip)); } }

    private int _port;
    public int Port { get => _port; set { _port = value; Notify(nameof(Port)); } }

    private DateTime _lastSeenUtc;
    public DateTime LastSeenUtc { get => _lastSeenUtc; set { _lastSeenUtc = value; Notify(nameof(LastSeenUtc)); } }

    public string DisplayText => $"{Name}  ({Ip})";

    public DeviceInfo(string deviceId, string name, string ip, int port, DateTime lastSeenUtc)
    {
        DeviceId = deviceId;
        Name = string.IsNullOrWhiteSpace(name) ? "Device" : name;
        _ip = ip;
        _port = port;
        _lastSeenUtc = lastSeenUtc;
        Initial = Name.Trim().Length > 0 ? Name.Trim()[0].ToString().ToUpperInvariant() : "?";
        AvatarBrush = AvatarColor.ForKey(string.IsNullOrEmpty(deviceId) ? Name : deviceId);
    }

    public override string ToString() => DisplayText;
}
