using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Microsoft.Win32;
using Window = System.Windows.Window;
using Thickness = System.Windows.Thickness;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using CornerRadius = System.Windows.CornerRadius;
using WindowStartupLocation = System.Windows.WindowStartupLocation;
using ResizeMode = System.Windows.ResizeMode;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;

namespace NearbyBoostSenderGui;

public class QueueItemVM
{
    public string Path { get; }
    public bool IsFolder { get; }
    public string DisplayName { get; }
    public string BadgeLabel { get; }
    public System.Windows.Media.Brush BadgeColor { get; }

    public QueueItemVM(string path, bool isFolder)
    {
        Path = path;
        IsFolder = isFolder;
        DisplayName = isFolder ? new DirectoryInfo(path).Name : System.IO.Path.GetFileName(path);
        var badge = isFolder ? FileTypeBadge.ForFolder() : FileTypeBadge.ForFile(DisplayName);
        BadgeLabel = badge.Label;
        BadgeColor = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(badge.ColorHex)!);
    }
}

public partial class MainWindow : Window
{
    private const int BufferSize = 1 << 20; // 1 MB read/write chunks

    private readonly ObservableCollection<QueueItemVM> _sendQueue = new();
    private readonly ObservableCollection<FileProgressVM> _fileProgress = new();
    private readonly ObservableCollection<HistoryRecord> _history = new();
    private readonly ObservableCollection<DeviceInfo> _nearbyDevices = new();
    private readonly ObservableCollection<DeviceInfo> _pairedDevices = new();
    private bool _sending;
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _currentReceiveCts;

    private TcpListener? _listener;
    private CancellationTokenSource? _receiveCts;
    private CancellationTokenSource? _discoveryCts;
    private bool _receiving;
    private string _receiveDestination = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NearbyBoost");

    private readonly object _batchLock = new();
    private BatchState? _activeBatch;

    private class BatchState
    {
        public string DestRoot;
        public string Name;
        public long TotalBytes;
        public int RemainingFiles;
        public long ReceivedBytes;
        public long StartTimeMs;
        public long LastReportMs;
        public long LastReportBytes;
        public HistoryRecord? Record;
        public string? PeerId;
        public string? PeerName;
        public CancellationTokenSource Cts = new();

        public BatchState(string destRoot, string name, long totalBytes, int remainingFiles)
        {
            DestRoot = destRoot;
            Name = name;
            TotalBytes = totalBytes;
            RemainingFiles = remainingFiles;
            StartTimeMs = Environment.TickCount64;
            LastReportMs = StartTimeMs;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = _sendQueue;
        FileProgressList.ItemsSource = _fileProgress;
        HistoryListView.ItemsSource = _history;
        NearbyList.ItemsSource = _nearbyDevices;
        PairedList.ItemsSource = _pairedDevices;
        QuickPairedList.ItemsSource = _pairedDevices;
        SaveLocationText.Text = _receiveDestination;

        var identity = Identity.Load();
        if (string.IsNullOrWhiteSpace(identity.DisplayName))
        {
            identity.DisplayName = PromptForDisplayName(Environment.MachineName);
            Identity.Save(identity);
        }
        RefreshIdentityUi();

        foreach (var r in KnownDevices.Load())
            _pairedDevices.Add(new DeviceInfo(r.DeviceId, r.Name, r.Ip, r.Port, r.LastSeenUtc));
        RefreshPairedEmptyState();

        foreach (var h in HistoryStore.Load())
            _history.Add(h);

        var settings = AppSettings.Load();
        ParallelTransferCheck.IsChecked = settings.ParallelTransferEnabled;
        switch (settings.ThemeMode)
        {
            case "dark": ThemeDarkRadio.IsChecked = true; break;
            case "system": ThemeSystemRadio.IsChecked = true; break;
            default: ThemeLightRadio.IsChecked = true; break;
        }
    }

    // ================= NAVIGATION =================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (TransferPage == null) return; // fires during XAML load
        TransferPage.Visibility = Visibility.Collapsed;
        DevicesPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        if (sender == NavTransfer) TransferPage.Visibility = Visibility.Visible;
        else if (sender == NavDevices) DevicesPage.Visibility = Visibility.Visible;
        else if (sender == NavHistory) HistoryPage.Visibility = Visibility.Visible;
        else if (sender == NavSettings) SettingsPage.Visibility = Visibility.Visible;
    }

    private void GoToSendWith(string ip, int port)
    {
        NavTransfer.IsChecked = true;
        SendModeRadio.IsChecked = true;
        IpBox.Text = ip;
        PortBox.Text = port.ToString();
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (SendPanel == null || ReceivePanel == null) return;
        bool sendMode = SendModeRadio.IsChecked == true;
        SendPanel.Visibility = sendMode ? Visibility.Visible : Visibility.Collapsed;
        ReceivePanel.Visibility = sendMode ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================= SEND: QUEUE =================

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select files to send", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
                _sendQueue.Add(new QueueItemVM(file, false));
            RefreshQueueVisibility();
        }
    }

    private void FolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Select a folder to send" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _sendQueue.Add(new QueueItemVM(dialog.SelectedPath, true));
            RefreshQueueVisibility();
        }
    }

    private void RemoveQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueItemVM item)
        {
            _sendQueue.Remove(item);
            RefreshQueueVisibility();
        }
    }

    private void RefreshQueueVisibility()
    {
        bool has = _sendQueue.Count > 0;
        QueueList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        QueueEmptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================= SEND =================

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;

        if (_sendQueue.Count == 0)
        {
            MessageBox.Show("Pick at least one file or folder first.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out int port))
        {
            MessageBox.Show("Port must be a number.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string ip = IpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            MessageBox.Show("Enter the receiving device's IP address.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _sending = true;
        _sendCts = new CancellationTokenSource();
        SendButton.IsEnabled = false;
        StopTransferButton.Visibility = Visibility.Visible;
        StatusText.Text = "Connecting...";
        ProgressBarCtl.Value = 0;
        SpeedText.Text = "0.0 MB/s";
        PercentText.Text = "0%";
        EtaText.Text = "--";
        _fileProgress.Clear();
        FileProgressList.Visibility = Visibility.Collapsed;

        var queueSnapshot = _sendQueue.ToList();
        _sendQueue.Clear();
        RefreshQueueVisibility();

        var record = new HistoryRecord
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Direction = "Sent",
            Peer = $"{ip}:{port}"
        };

        try
        {
            var entries = new List<(string LocalPath, string RelativePath, long Size)>();
            foreach (var item in queueSnapshot)
            {
                if (item.IsFolder)
                {
                    string folderName = new DirectoryInfo(item.Path).Name;
                    foreach (var full in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                    {
                        string rel = folderName + "/" + Path.GetRelativePath(item.Path, full).Replace('\\', '/');
                        entries.Add((full, rel, new FileInfo(full).Length));
                    }
                }
                else
                {
                    entries.Add((item.Path, Path.GetFileName(item.Path), new FileInfo(item.Path).Length));
                }
            }
            if (entries.Count == 0) throw new IOException("Nothing to send");

            long totalBytes = entries.Sum(en => en.Size);
            var progress = new Progress<(double percent, double mbPerSec, long bytesDone, long totalBytes)>(p =>
            {
                ProgressBarCtl.Value = p.percent;
                PercentText.Text = $"{p.percent:F0}%";
                SpeedText.Text = $"{p.mbPerSec:F1} MB/s";
                EtaText.Text = FormatEta(p.mbPerSec, p.bytesDone, p.totalBytes);
            });

            double avgMbPerSec;
            string? peerId = null;
            string? peerName = null;
            if (entries.Count == 1)
            {
                var only = entries[0];
                record.Name = Path.GetFileName(only.LocalPath);
                record.Size = FormatSize(only.Size);
                record.LocalPath = only.LocalPath;
                _history.Insert(0, record);
                CurrentFileText.Text = $"Sending {record.Name} \u2192 {ip}:{port}";
                StatusText.Text = "Sending...";
                var result = await SendFileAsync(ip, port, only.LocalPath, only.RelativePath, progress, _sendCts.Token);
                avgMbPerSec = result.avgMbPerSec;
                peerId = result.peerId;
                peerName = result.peerName;
            }
            else
            {
                string batchName = (queueSnapshot.Count == 1 && queueSnapshot[0].IsFolder)
                    ? new DirectoryInfo(queueSnapshot[0].Path).Name
                    : $"{entries.Count} files";
                record.Name = $"{batchName} ({entries.Count} files)";
                record.Size = FormatSize(totalBytes);
                record.LocalPath = queueSnapshot.Count == 1 && queueSnapshot[0].IsFolder ? queueSnapshot[0].Path : null;
                _history.Insert(0, record);
                CurrentFileText.Text = $"Sending {batchName} ({entries.Count} files) \u2192 {ip}:{port}";
                StatusText.Text = "Sending...";

                var fileVms = entries.Select(en => new FileProgressVM(en.RelativePath)).ToList();
                foreach (var vm in fileVms) _fileProgress.Add(vm);
                FileProgressList.Visibility = Visibility.Visible;

                var result = await SendBatchAsync(ip, port, batchName, entries, totalBytes, progress, fileVms, _sendCts.Token);
                avgMbPerSec = result.avgMbPerSec;
                peerId = result.peerId;
                peerName = result.peerName;
            }

            StatusText.Text = "Done";
            ProgressBarCtl.Value = 100;
            PercentText.Text = "100%";
            EtaText.Text = "0s";
            record.AvgSpeed = $"{avgMbPerSec:F1} MB/s";
            record.Status = "Done";
            HistoryStore.Save(_history.ToList());
            if (peerId != null) RememberPairedDevice(peerId, peerName, ip, port);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            record.Status = "Cancelled";
            if (!_history.Contains(record)) _history.Insert(0, record);
            HistoryStore.Save(_history.ToList());
            foreach (var item in queueSnapshot)
                if (!_sendQueue.Contains(item)) _sendQueue.Add(item);
            RefreshQueueVisibility();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
            record.Status = "Failed";
            if (!_history.Contains(record)) _history.Insert(0, record);
            HistoryStore.Save(_history.ToList());
            foreach (var item in queueSnapshot)
                if (!_sendQueue.Contains(item)) _sendQueue.Add(item);
            RefreshQueueVisibility();
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
            StopTransferButton.Visibility = Visibility.Collapsed;
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    private void StopTransferButton_Click(object sender, RoutedEventArgs e)
    {
        _sendCts?.Cancel();
        _currentReceiveCts?.Cancel();
    }

    private static Task<(double avgMbPerSec, string? peerId, string? peerName)> SendFileAsync(
        string ip, int port, string filePath, string relativePath,
        IProgress<(double percent, double mbPerSec, long bytesDone, long totalBytes)> progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            long fileSize = new FileInfo(filePath).Length;

            using var client = new TcpClient();
            client.NoDelay = true;
            client.SendBufferSize = BufferSize;
            client.Connect(ip, port);

            using var netStream = client.GetStream();

            netStream.WriteByte(0);
            byte[] nameBytes = Encoding.UTF8.GetBytes(relativePath);
            WriteInt32(netStream, nameBytes.Length);
            netStream.Write(nameBytes, 0, nameBytes.Length);
            WriteInt64(netStream, fileSize);
            WriteIdentityBlock(netStream);
            netStream.Flush();

            netStream.ReadTimeout = 35000;
            int response;
            try { response = netStream.ReadByte(); }
            catch (IOException) { throw new IOException("No response from receiver (timed out)"); }
            if (response != 1) throw new IOException("Rejected by receiver");
            var peer = ReadIdentityBlock(netStream);

            using var fileStream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

            byte[] buffer = new byte[BufferSize];
            long sent = 0;
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            long lastReportBytes = 0;
            int read;

            while ((read = fileStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                netStream.Write(buffer, 0, read);
                sent += read;

                long nowMs = sw.ElapsedMilliseconds;
                if (nowMs - lastReportMs >= 200)
                {
                    double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                    double mbPerSec = ((sent - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                    double pct = (sent * 100.0) / fileSize;
                    progress.Report((pct, mbPerSec, sent, fileSize));
                    lastReportMs = nowMs;
                    lastReportBytes = sent;
                }
            }

            sw.Stop();
            double avg = (fileSize / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            return (avg, peer?.Item1, peer?.Item2);
        });
    }

    /// <summary>Announces the batch (one accept prompt), then streams files across up to 4
    /// concurrent connections for real parallel throughput (or 1 if parallel transfer is off).</summary>
    private static Task<(double avgMbPerSec, string? peerId, string? peerName)> SendBatchAsync(
        string ip, int port, string batchName, List<(string LocalPath, string RelativePath, long Size)> entries,
        long totalBytes, IProgress<(double percent, double mbPerSec, long bytesDone, long totalBytes)> progress,
        List<FileProgressVM> fileVms, CancellationToken token)
    {
        return Task.Run(() =>
        {
            (string, string)? peer;
            using (var control = new TcpClient())
            {
                control.NoDelay = true;
                control.Connect(ip, port);
                using var netStream = control.GetStream();
                netStream.WriteByte(2); // transferType = batch announcement
                byte[] nameBytes = Encoding.UTF8.GetBytes(batchName);
                WriteInt32(netStream, nameBytes.Length);
                netStream.Write(nameBytes, 0, nameBytes.Length);
                WriteInt32(netStream, entries.Count);
                WriteInt64(netStream, totalBytes);
                WriteIdentityBlock(netStream);
                netStream.Flush();

                netStream.ReadTimeout = 35000;
                int response;
                try { response = netStream.ReadByte(); }
                catch (IOException) { throw new IOException("No response from receiver (timed out)"); }
                if (response != 1) throw new IOException("Rejected by receiver");
                peer = ReadIdentityBlock(netStream);
            }

            int streamCount = AppSettings.Load().ParallelTransferEnabled ? Math.Min(4, entries.Count) : 1;
            var lanes = new List<(string LocalPath, string RelativePath, long Size)>[streamCount];
            for (int i = 0; i < streamCount; i++) lanes[i] = new List<(string, string, long)>();
            for (int i = 0; i < entries.Count; i++) lanes[i % streamCount].Add(entries[i]);

            long totalSent = 0;
            var sw = Stopwatch.StartNew();
            object progressLock = new object();
            long lastReportMs = 0;
            long lastReportBytes = 0;
            Exception? laneError = null;
            var vmByPath = fileVms.ToDictionary(v => v.Name);

            var tasks = lanes.Where(l => l.Count > 0).Select(lane => Task.Run(() =>
            {
                try
                {
                    foreach (var entry in lane)
                    {
                        token.ThrowIfCancellationRequested();
                        var vm = vmByPath.TryGetValue(entry.RelativePath, out var v) ? v : null;
                        if (vm != null) System.Windows.Application.Current.Dispatcher.Invoke(() => vm.Status = "Sending");

                        using var socket = new TcpClient();
                        socket.NoDelay = true;
                        socket.SendBufferSize = BufferSize;
                        socket.Connect(ip, port);
                        using var netStream = socket.GetStream();

                        netStream.WriteByte(0);
                        byte[] relBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
                        WriteInt32(netStream, relBytes.Length);
                        netStream.Write(relBytes, 0, relBytes.Length);
                        WriteInt64(netStream, entry.Size);
                        netStream.Flush();

                        netStream.ReadTimeout = 35000;
                        int ack;
                        try { ack = netStream.ReadByte(); }
                        catch (IOException) { throw new IOException($"No response for {entry.RelativePath}"); }
                        if (ack != 1) throw new IOException($"Receiver did not accept {entry.RelativePath}");

                        using var fileStream = new FileStream(
                            entry.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                        byte[] buffer = new byte[BufferSize];
                        long fileSent = 0;
                        int read;
                        while ((read = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            netStream.Write(buffer, 0, read);
                            fileSent += read;
                            long total = Interlocked.Add(ref totalSent, read);

                            if (vm != null)
                            {
                                double filePct = entry.Size > 0 ? (fileSent * 100.0) / entry.Size : 100.0;
                                System.Windows.Application.Current.Dispatcher.Invoke(() => vm.Percent = filePct);
                            }

                            lock (progressLock)
                            {
                                long nowMs = sw.ElapsedMilliseconds;
                                if (nowMs - lastReportMs >= 200)
                                {
                                    double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                                    double mbPerSec = ((total - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                                    double pct = totalBytes > 0 ? (total * 100.0) / totalBytes : 100.0;
                                    progress.Report((pct, mbPerSec, total, totalBytes));
                                    lastReportMs = nowMs;
                                    lastReportBytes = total;
                                }
                            }
                        }
                        if (vm != null)
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                vm.Percent = 100;
                                vm.Status = "Done";
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (progressLock) { laneError ??= ex; }
                }
            })).ToArray();

            Task.WaitAll(tasks);
            if (laneError != null) throw laneError;

            sw.Stop();
            double avg = (totalBytes / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            return (avg, peer?.Item1, peer?.Item2);
        });
    }

    /// <summary>Writes this device's identity: [int32 idLen][id][int32 nameLen][name]. Used on the
    /// two "entry point" messages (standalone file, batch announcement) — not on every batch-member
    /// file connection, so a large batch doesn't pay that cost per file.</summary>
    private static void WriteIdentityBlock(Stream s)
    {
        var identity = Identity.Load();
        byte[] idBytes = Encoding.UTF8.GetBytes(identity.DeviceId);
        byte[] nameBytes = Encoding.UTF8.GetBytes(identity.DisplayName);
        WriteInt32(s, idBytes.Length);
        s.Write(idBytes, 0, idBytes.Length);
        WriteInt32(s, nameBytes.Length);
        s.Write(nameBytes, 0, nameBytes.Length);
    }

    /// <summary>Reads a peer identity block. Returns null on any framing problem (e.g. an older
    /// build on the other end) rather than throwing.</summary>
    private static (string, string)? ReadIdentityBlock(Stream s)
    {
        try
        {
            int idLen = ReadInt32(s);
            if (idLen < 0 || idLen > 4096) return null;
            byte[] idBytes = ReadExact(s, idLen);
            int nameLen = ReadInt32(s);
            if (nameLen < 0 || nameLen > 4096) return null;
            byte[] nameBytes = ReadExact(s, nameLen);
            return (Encoding.UTF8.GetString(idBytes), Encoding.UTF8.GetString(nameBytes));
        }
        catch
        {
            return null;
        }
    }

    // ================= PAIRED DEVICES =================

    private void RememberPairedDevice(string deviceId, string? name, string ip, int port)
    {
        KnownDevices.Remember(deviceId, name ?? "Device", ip, port);
        var existing = _pairedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (existing != null)
        {
            existing.Ip = ip;
            existing.Port = port;
            existing.LastSeenUtc = DateTime.UtcNow;
        }
        else
        {
            _pairedDevices.Add(new DeviceInfo(deviceId, name ?? "Device", ip, port, DateTime.UtcNow));
        }
        RefreshPairedEmptyState();
    }

    private void RefreshPairedEmptyState()
    {
        PairedEmptyText.Visibility = _pairedDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QuickPairedList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QuickPairedList.SelectedItem is DeviceInfo d) { IpBox.Text = d.Ip; PortBox.Text = d.Port.ToString(); }
    }

    private void PairedList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PairedList.SelectedItem is DeviceInfo d) GoToSendWith(d.Ip, d.Port);
    }

    private void NearbyList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NearbyList.SelectedItem is DeviceInfo d) GoToSendWith(d.Ip, d.Port);
    }

    // ================= SCAN + RIPPLE =================

    private async void DevicesScanButton_Click(object sender, RoutedEventArgs e)
    {
        DevicesScanButton.IsEnabled = false;
        DevicesScanButton.Content = "Scanning...";
        StartRipple();
        try
        {
            var found = await Task.Run(() => Discovery.Scan());
            _nearbyDevices.Clear();
            foreach (var d in found)
                _nearbyDevices.Add(new DeviceInfo(d.DeviceId, d.Name, d.Ip, d.Port, DateTime.UtcNow));
            NearbyEmptyText.Visibility = _nearbyDevices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Count == 0)
            {
                MessageBox.Show("No devices found \u2014 make sure the other device is on Receive and on the same network.",
                    "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            DevicesScanButton.IsEnabled = true;
            DevicesScanButton.Content = "Scan for devices";
            StopRipple();
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        NavDevices.IsChecked = true;
        DevicesScanButton_Click(sender, e);
    }

    private void StartRipple()
    {
        var ellipses = new[] { Ripple1, Ripple2, Ripple3 };
        for (int i = 0; i < ellipses.Length; i++)
        {
            var el = ellipses[i];
            el.Visibility = Visibility.Visible;
            var widthAnim = new DoubleAnimation(40, 180, TimeSpan.FromSeconds(1.8))
            { BeginTime = TimeSpan.FromSeconds(i * 0.5), RepeatBehavior = RepeatBehavior.Forever };
            var heightAnim = new DoubleAnimation(40, 180, TimeSpan.FromSeconds(1.8))
            { BeginTime = TimeSpan.FromSeconds(i * 0.5), RepeatBehavior = RepeatBehavior.Forever };
            var opacityAnim = new DoubleAnimation(0.6, 0, TimeSpan.FromSeconds(1.8))
            { BeginTime = TimeSpan.FromSeconds(i * 0.5), RepeatBehavior = RepeatBehavior.Forever };
            el.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            el.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            el.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }
    }

    private void StopRipple()
    {
        foreach (var el in new[] { Ripple1, Ripple2, Ripple3 })
        {
            el.BeginAnimation(FrameworkElement.WidthProperty, null);
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Visibility = Visibility.Hidden;
        }
    }

    // ================= HISTORY =================

    private void HistoryListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryListView.SelectedItem is HistoryRecord rec) OpenHistoryLocation(rec);
    }

    private void OpenHistoryLocation(HistoryRecord rec)
    {
        try
        {
            if (rec.Direction == "Received")
            {
                Process.Start("explorer.exe", $"\"{_receiveDestination}\"");
            }
            else if (!string.IsNullOrEmpty(rec.LocalPath) && (File.Exists(rec.LocalPath) || Directory.Exists(rec.LocalPath)))
            {
                if (File.Exists(rec.LocalPath))
                    Process.Start("explorer.exe", $"/select,\"{rec.LocalPath}\"");
                else
                    Process.Start("explorer.exe", $"\"{rec.LocalPath}\"");
            }
            else
            {
                MessageBox.Show("Original location isn't available for this entry.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch { /* best effort */ }
    }

    // ================= SETTINGS =================

    private void RefreshIdentityUi()
    {
        var identity = Identity.Load();
        var avatar = AvatarCatalog.Get(identity.AvatarIndex);
        SettingsNameInput.Text = identity.DisplayName;
        SettingsAvatarEmoji.Text = avatar.Emoji;
        SettingsAvatarBorder.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(avatar.ColorHex)!);
        ScanCenterEmoji.Text = avatar.Emoji;
        ScanCenterAvatar.Background = SettingsAvatarBorder.Background;
    }

    private void SaveIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        var identity = Identity.Load();
        identity.DisplayName = string.IsNullOrWhiteSpace(SettingsNameInput.Text) ? Environment.MachineName : SettingsNameInput.Text.Trim();
        Identity.Save(identity);
        RefreshIdentityUi();
        MessageBox.Show("Saved.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ChooseAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Choose an avatar",
            Width = 380,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var wrap = new System.Windows.Controls.WrapPanel { Margin = new Thickness(12) };
        for (int i = 0; i < AvatarCatalog.Options.Count; i++)
        {
            int index = i;
            var avatar = AvatarCatalog.Options[i];
            var cell = new System.Windows.Controls.Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                Margin = new Thickness(4),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(avatar.ColorHex)!),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = avatar.Emoji,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            cell.MouseLeftButtonUp += (_, _) =>
            {
                var identity = Identity.Load();
                identity.AvatarIndex = index;
                Identity.Save(identity);
                RefreshIdentityUi();
                dialog.Close();
            };
            wrap.Children.Add(cell);
        }
        dialog.Content = new System.Windows.Controls.ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (ThemeLightRadio == null) return;
        var settings = AppSettings.Load();
        settings.ThemeMode = ThemeDarkRadio.IsChecked == true ? "dark" : ThemeSystemRadio.IsChecked == true ? "system" : "light";
        AppSettings.Save(settings);
    }

    private void ParallelTransferCheck_Changed(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load();
        settings.ParallelTransferEnabled = ParallelTransferCheck.IsChecked == true;
        AppSettings.Save(settings);
    }

    /// <summary>Minimal self-built prompt — avoids pulling in a VB reference just for an input box.</summary>
    private static string PromptForDisplayName(string defaultName)
    {
        var dialog = new Window
        {
            Title = "NearbyBoost",
            Width = 360,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "What should other devices call this computer?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var textBox = new System.Windows.Controls.TextBox { Text = defaultName, Padding = new Thickness(6) };
        panel.Children.Add(textBox);

        var button = new System.Windows.Controls.Button
        {
            Content = "Continue",
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        string result = defaultName;
        button.Click += (_, _) =>
        {
            result = string.IsNullOrWhiteSpace(textBox.Text) ? defaultName : textBox.Text.Trim();
            dialog.DialogResult = true;
        };
        panel.Children.Add(button);

        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    // ================= RECEIVE =================

    private void CopyAddressButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(LocalAddressText.Text);
    }

    private void ChooseSaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where received files are saved",
            SelectedPath = Directory.Exists(_receiveDestination) ? _receiveDestination : ""
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _receiveDestination = dialog.SelectedPath;
            SaveLocationText.Text = _receiveDestination;
        }
    }

    private async void ReceiveToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_receiving) StopReceiving();
        else await StartReceivingAsync();
    }

    private async Task StartReceivingAsync()
    {
        if (!int.TryParse(ReceivePortBox.Text.Trim(), out int port))
        {
            MessageBox.Show("Port must be a number.", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReceiveToggleButton.IsEnabled = false;
        ReceiveToggleButton.Content = "Starting...";

        string? localIp = NetworkHelper.GetLocalIPv4();
        var bindAddress = localIp != null ? IPAddress.Parse(localIp) : IPAddress.Any;

        TcpListener? listener = null;
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                listener = new TcpListener(bindAddress, port);
                listener.Start();
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                listener = null;
                await Task.Delay(300);
            }
        }

        if (listener == null)
        {
            ReceiveToggleButton.IsEnabled = true;
            ReceiveToggleButton.Content = "Start Server";
            MessageBox.Show($"Could not start server: {lastError?.Message}", "NearbyBoost", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _listener = listener;
        _receiveCts = new CancellationTokenSource();
        _discoveryCts = new CancellationTokenSource();
        _receiving = true;
        ReceiveToggleButton.IsEnabled = true;
        ReceiveToggleButton.Content = "Stop Server";
        ReceivePortBox.IsEnabled = false;
        LocalAddressText.Text = $"{localIp ?? "0.0.0.0"}:{port}";
        StatusText.Text = "Waiting for a connection...";

        _ = Task.Run(() => AcceptLoopAsync(_listener, _receiveCts.Token));
        var identity = Identity.Load();
        _ = Task.Run(() => Discovery.RunResponder(port, identity.DeviceId, identity.DisplayName, _discoveryCts.Token));
    }

    private void StopReceiving()
    {
        ReceiveToggleButton.IsEnabled = false;
        _receiveCts?.Cancel();
        _discoveryCts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _receiving = false;
        ReceiveToggleButton.IsEnabled = true;
        ReceiveToggleButton.Content = "Start Server";
        ReceivePortBox.IsEnabled = true;
        StatusText.Text = "Stopped";
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Error: server stopped unexpectedly";
                        _receiving = false;
                        ReceiveToggleButton.Content = "Start Server";
                        ReceivePortBox.IsEnabled = true;
                    });
                }
                break;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    HandleIncoming(client);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"Error: {ex.Message}";
                        var rec = new HistoryRecord
                        {
                            Time = DateTime.Now.ToString("HH:mm:ss"), Direction = "Received",
                            Name = "-", Peer = "-", Size = "-", AvgSpeed = "-", Status = $"Failed: {ex.Message}"
                        };
                        _history.Insert(0, rec);
                        HistoryStore.Save(_history.ToList());
                    });
                }
                finally
                {
                    client.Close();
                }
            });
        }
    }

    private void HandleIncoming(TcpClient client)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = BufferSize;
        string senderAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

        using var netStream = client.GetStream();

        int transferType = netStream.ReadByte();
        if (transferType == -1) throw new IOException("Connection closed unexpectedly");

        if (transferType == 2)
            HandleBatchAnnouncement(netStream, senderAddress);
        else
            HandleSingleFileOrBatchMember(netStream, senderAddress);
    }

    private void HandleBatchAnnouncement(NetworkStream netStream, string senderAddress)
    {
        int nameLen = ReadInt32(netStream);
        if (nameLen <= 0 || nameLen > 4096) throw new IOException("Bad batch name length");
        byte[] nameBytes = ReadExact(netStream, nameLen);
        string batchName = SanitizeFileName(Encoding.UTF8.GetString(nameBytes));
        int fileCount = ReadInt32(netStream);
        if (fileCount <= 0 || fileCount > 100000) throw new IOException("Bad file count");
        long totalBytes = ReadInt64(netStream);
        if (totalBytes < 0) throw new IOException("Bad total size");
        var senderIdentity = ReadIdentityBlock(netStream);

        lock (_batchLock)
        {
            if (_activeBatch != null)
            {
                netStream.WriteByte(0);
                netStream.Flush();
                return;
            }
        }

        bool approved = false;
        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                $"From {senderAddress}\n\n{batchName}\n{fileCount} files, {FormatSize(totalBytes)}\n\nAccept?",
                "Incoming files", MessageBoxButton.YesNo, MessageBoxImage.Question);
            approved = result == MessageBoxResult.Yes;
        });

        netStream.WriteByte((byte)(approved ? 1 : 0));
        if (approved) WriteIdentityBlock(netStream);
        netStream.Flush();
        if (!approved)
        {
            Dispatcher.Invoke(() =>
            {
                var rec = new HistoryRecord
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"), Direction = "Received",
                    Name = $"{batchName} ({fileCount} files)", Peer = senderAddress,
                    Size = FormatSize(totalBytes), AvgSpeed = "-", Status = "Rejected"
                };
                _history.Insert(0, rec);
                HistoryStore.Save(_history.ToList());
            });
            return;
        }

        Directory.CreateDirectory(_receiveDestination);
        string destRoot = Path.Combine(_receiveDestination, GetNonCollidingFolderName(_receiveDestination, batchName));
        Directory.CreateDirectory(destRoot);

        var batch = new BatchState(destRoot, batchName, totalBytes, fileCount)
        {
            PeerId = senderIdentity?.Item1,
            PeerName = senderIdentity?.Item2
        };
        var record = new HistoryRecord
        {
            Time = DateTime.Now.ToString("HH:mm:ss"), Direction = "Received",
            Name = $"{batchName} ({fileCount} files)", Peer = senderIdentity?.Item2 ?? senderAddress,
            Size = FormatSize(totalBytes), Status = "In progress", LocalPath = destRoot
        };
        batch.Record = record;
        lock (_batchLock) { _activeBatch = batch; }
        _currentReceiveCts = batch.Cts;

        Dispatcher.Invoke(() =>
        {
            _history.Insert(0, record);
            CurrentFileText.Text = $"Receiving {batchName} from {senderAddress}";
            StatusText.Text = "Receiving...";
            ProgressBarCtl.Value = 0;
            StopTransferButton.Visibility = Visibility.Visible;
            _fileProgress.Clear();
            FileProgressList.Visibility = Visibility.Visible;
        });
    }

    private void HandleSingleFileOrBatchMember(NetworkStream netStream, string senderAddress)
    {
        int nameLen = ReadInt32(netStream);
        if (nameLen <= 0 || nameLen > 4096) throw new IOException("Bad filename length");
        byte[] nameBytes = ReadExact(netStream, nameLen);
        string rawName = Encoding.UTF8.GetString(nameBytes);
        long fileSize = ReadInt64(netStream);
        if (fileSize < 0) throw new IOException("Bad file size");

        BatchState? batch;
        lock (_batchLock) { batch = _activeBatch; }

        if (batch != null)
        {
            netStream.WriteByte(1);
            netStream.Flush();

            string relPath = SanitizeRelativePath(rawName);
            var fileVm = new FileProgressVM(relPath);
            Dispatcher.Invoke(() => { fileVm.Status = "Receiving"; _fileProgress.Add(fileVm); });

            try
            {
                string fullSavePath = Path.Combine(batch.DestRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                string? fileDir = Path.GetDirectoryName(fullSavePath);
                if (!string.IsNullOrEmpty(fileDir)) Directory.CreateDirectory(fileDir);

                ReceiveFileBytes(netStream, fullSavePath, fileSize, n =>
                {
                    long total = Interlocked.Add(ref batch.ReceivedBytes, n);
                    lock (batch)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - batch.LastReportMs >= 200)
                        {
                            double deltaSec = Math.Max(nowMs - batch.LastReportMs, 1) / 1000.0;
                            double mbPerSec = ((total - batch.LastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                            double pct = batch.TotalBytes > 0 ? (total * 100.0) / batch.TotalBytes : 100.0;
                            Dispatcher.Invoke(() =>
                            {
                                ProgressBarCtl.Value = pct;
                                PercentText.Text = $"{pct:F0}%";
                                SpeedText.Text = $"{mbPerSec:F1} MB/s";
                                EtaText.Text = FormatEta(mbPerSec, total, batch.TotalBytes);
                            });
                            batch.LastReportMs = nowMs;
                            batch.LastReportBytes = total;
                        }
                    }
                }, batch.Cts.Token);

                Dispatcher.Invoke(() => { fileVm.Percent = 100; fileVm.Status = "Done"; });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => fileVm.Status = "Cancelled");
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref batch.RemainingFiles) <= 0)
                {
                    lock (_batchLock) { if (_activeBatch == batch) _activeBatch = null; }
                    bool wasCancelled = batch.Cts.IsCancellationRequested;
                    double elapsed = Math.Max(Environment.TickCount64 - batch.StartTimeMs, 1) / 1000.0;
                    double avgMbPerSec = (batch.TotalBytes / (1024.0 * 1024.0)) / elapsed;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = wasCancelled ? "Cancelled" : "Done";
                        if (!wasCancelled) { ProgressBarCtl.Value = 100; PercentText.Text = "100%"; }
                        EtaText.Text = "0s";
                        if (batch.Record != null)
                        {
                            batch.Record.AvgSpeed = wasCancelled ? "-" : $"{avgMbPerSec:F1} MB/s";
                            batch.Record.Status = wasCancelled ? "Cancelled" : "Done";
                        }
                        HistoryStore.Save(_history.ToList());
                        CurrentFileText.Text = $"Saved to {batch.DestRoot}";
                        StopTransferButton.Visibility = Visibility.Collapsed;
                        if (_currentReceiveCts == batch.Cts) _currentReceiveCts = null;
                        if (!wasCancelled && batch.PeerId != null) RememberPairedDevice(batch.PeerId, batch.PeerName, senderAddress, 52525);
                    });
                }
            }
            return;
        }

        // Standalone single file — read the sender's identity, then the normal one-file prompt flow.
        var senderIdentity = ReadIdentityBlock(netStream);
        string fileName = SanitizeFileName(rawName);
        bool approved = false;
        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                $"From {senderAddress}\n\n{fileName}\n{FormatSize(fileSize)}\n\nAccept this file?",
                "Incoming file", MessageBoxButton.YesNo, MessageBoxImage.Question);
            approved = result == MessageBoxResult.Yes;
        });

        netStream.WriteByte((byte)(approved ? 1 : 0));
        if (approved) WriteIdentityBlock(netStream);
        netStream.Flush();

        if (!approved)
        {
            Dispatcher.Invoke(() =>
            {
                var rec = new HistoryRecord
                {
                    Time = DateTime.Now.ToString("HH:mm:ss"), Direction = "Received",
                    Name = fileName, Peer = senderAddress, Size = FormatSize(fileSize), AvgSpeed = "-", Status = "Rejected"
                };
                _history.Insert(0, rec);
                HistoryStore.Save(_history.ToList());
            });
            return;
        }

        Directory.CreateDirectory(_receiveDestination);
        string savePath = GetNonCollidingPath(Path.Combine(_receiveDestination, fileName));

        var record = new HistoryRecord
        {
            Time = DateTime.Now.ToString("HH:mm:ss"), Direction = "Received",
            Name = fileName, Peer = senderIdentity?.Item2 ?? senderAddress,
            Size = FormatSize(fileSize), Status = "In progress", LocalPath = savePath
        };
        var soloCts = new CancellationTokenSource();
        _currentReceiveCts = soloCts;
        Dispatcher.Invoke(() =>
        {
            _history.Insert(0, record);
            CurrentFileText.Text = $"Receiving {fileName} from {senderAddress}";
            StatusText.Text = "Receiving...";
            ProgressBarCtl.Value = 0;
            StopTransferButton.Visibility = Visibility.Visible;
            FileProgressList.Visibility = Visibility.Collapsed;
        });

        long received = 0;
        var sw = Stopwatch.StartNew();
        long lastReportMs = 0;
        long lastReportBytes = 0;

        try
        {
            ReceiveFileBytes(netStream, savePath, fileSize, n =>
            {
                received += n;
                long nowMs = sw.ElapsedMilliseconds;
                if (nowMs - lastReportMs >= 200)
                {
                    double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                    double mbPerSec = ((received - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                    double pct = (received * 100.0) / fileSize;
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBarCtl.Value = pct;
                        PercentText.Text = $"{pct:F0}%";
                        SpeedText.Text = $"{mbPerSec:F1} MB/s";
                        EtaText.Text = FormatEta(mbPerSec, received, fileSize);
                    });
                    lastReportMs = nowMs;
                    lastReportBytes = received;
                }
            }, soloCts.Token);

            sw.Stop();
            double avgMbPerSec2 = (fileSize / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Done";
                ProgressBarCtl.Value = 100;
                PercentText.Text = "100%";
                EtaText.Text = "0s";
                record.AvgSpeed = $"{avgMbPerSec2:F1} MB/s";
                record.Status = "Done";
                HistoryStore.Save(_history.ToList());
                CurrentFileText.Text = $"Saved to {savePath}";
                if (senderIdentity != null) RememberPairedDevice(senderIdentity.Value.Item1, senderIdentity.Value.Item2, senderAddress, 52525);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Cancelled";
                record.Status = "Cancelled";
                HistoryStore.Save(_history.ToList());
            });
            throw;
        }
        finally
        {
            if (_currentReceiveCts == soloCts) _currentReceiveCts = null;
            Dispatcher.Invoke(() => StopTransferButton.Visibility = Visibility.Collapsed);
            soloCts.Dispose();
        }
    }

    private static void ReceiveFileBytes(Stream stream, string savePath, long fileSize, Action<int> onChunk, CancellationToken token)
    {
        byte[] buffer = new byte[BufferSize];
        long received = 0;
        using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        while (received < fileSize)
        {
            token.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, fileSize - received);
            int n = stream.Read(buffer, 0, toRead);
            if (n == 0) throw new IOException("Connection closed before transfer finished");
            fileStream.Write(buffer, 0, n);
            received += n;
            onChunk(n);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _receiveCts?.Cancel();
        _discoveryCts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
    }

    // ================= SHARED HELPERS =================

    private static void WriteInt32(Stream s, int value)
    {
        int network = IPAddress.HostToNetworkOrder(value);
        s.Write(BitConverter.GetBytes(network), 0, 4);
    }

    private static void WriteInt64(Stream s, long value)
    {
        long network = IPAddress.HostToNetworkOrder(value);
        s.Write(BitConverter.GetBytes(network), 0, 8);
    }

    private static int ReadInt32(Stream s)
    {
        byte[] buf = ReadExact(s, 4);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buf, 0));
    }

    private static long ReadInt64(Stream s)
    {
        byte[] buf = ReadExact(s, 8);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt64(buf, 0));
    }

    private static byte[] ReadExact(Stream s, int count)
    {
        byte[] buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = s.Read(buf, offset, count - offset);
            if (n == 0) throw new IOException("Connection closed unexpectedly");
            offset += n;
        }
        return buf;
    }

    private static string GetNonCollidingPath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            i++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private static string GetNonCollidingFolderName(string parentDir, string name)
    {
        string candidate = name;
        int i = 1;
        while (Directory.Exists(Path.Combine(parentDir, candidate)))
        {
            candidate = $"{name} ({i})";
            i++;
        }
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "received_file" : name;
    }

    private static string SanitizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/')
            .Where(s => s.Length > 0 && s != "." && s != "..")
            .Select(seg =>
            {
                foreach (char c in Path.GetInvalidFileNameChars())
                    seg = seg.Replace(c, '_');
                seg = seg.Trim();
                return string.IsNullOrEmpty(seg) ? "_" : seg;
            });
        var result = string.Join("/", segments);
        return string.IsNullOrEmpty(result) ? "file" : result;
    }

    private static string FormatSize(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";
    }

    private static string FormatEta(double mbPerSec, long bytesDone, long totalBytes)
    {
        if (mbPerSec <= 0.05) return "calculating...";
        long remaining = Math.Max(totalBytes - bytesDone, 0);
        double seconds = (remaining / (1024.0 * 1024.0)) / mbPerSec;
        int s = (int)Math.Max(seconds, 0);
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {(s % 3600) / 60}m";
    }
}
