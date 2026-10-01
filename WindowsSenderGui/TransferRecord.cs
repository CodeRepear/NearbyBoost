using System.ComponentModel;

namespace NearbyBoostSenderGui;

public class TransferRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Time { get; set; } = "";
    public string Direction { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Size { get; set; } = "";

    private string _avgSpeed = "-";
    public string AvgSpeed
    {
        get => _avgSpeed;
        set { _avgSpeed = value; OnChanged(nameof(AvgSpeed)); }
    }

    private string _status = "In progress";
    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(nameof(Status)); }
    }

    private void OnChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
