using System.ComponentModel;

namespace NearbyBoostSenderGui;

public class FileProgressVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name { get; }

    private double _percent;
    public double Percent { get => _percent; set { _percent = value; Notify(nameof(Percent)); } }

    private string _status = "Pending";
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }

    public FileProgressVM(string name)
    {
        Name = name;
    }
}
