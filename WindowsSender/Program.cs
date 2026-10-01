using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// Wire protocol, all integers big-endian / network order:
//   transferType 0 (single file OR one file of an active batch):
//     [int32 nameLen][name][int64 fileSize] -> [1 accept/reject byte] -> raw bytes if accepted.
//     "name" may contain '/' as a relative-path separator when it's a batch member.
//   transferType 2 (batch announcement — one accept decision covers the whole batch):
//     [int32 batchNameLen][batchName][int32 fileCount][int64 totalBytes] -> [1 accept/reject byte]
//
// Parallelism: after a batch is accepted, the sender opens several *additional* ordinary
// transferType-0 connections concurrently and streams different files down each one. The
// receiver auto-accepts those (the batch itself already got the one prompt) and tracks
// aggregate progress. Assumes at most one batch is in flight at a time — a reasonable
// simplification for a point-to-point personal tool.

record DiscoveredDevice(string DeviceId, string Name, string Ip, int Port);

class Program
{
    const int BufferSize = 1 << 20; // 1 MB read/write chunks

    static readonly object ConsoleLock = new();
    static readonly object BatchLock = new();
    static BatchState? ActiveBatchState;

    class BatchState
    {
        public string DestRoot;
        public string Name;
        public long TotalBytes;
        public int RemainingFiles;
        public long ReceivedBytes;
        public long StartTimeMs;
        public long LastReportMs;
        public long LastReportBytes;

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

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Scanning...");
            var devices = ScanForDevices();
            if (devices.Count == 0)
                Console.WriteLine("No devices found \u2014 make sure the other device is on Receive and on the same network.");
            else
                for (int i = 0; i < devices.Count; i++)
                    Console.WriteLine($"  {i + 1}. {devices[i].Name} ({devices[i].Ip}:{devices[i].Port})");
            return 0;
        }

        if (args.Length >= 1 && args[0].Equals("receive", StringComparison.OrdinalIgnoreCase))
        {
            int recvPort = 52525;
            if (args.Length >= 2 && int.TryParse(args[1], out int p)) recvPort = p;
            string? dest = args.Length >= 3 ? args[2] : null;
            RunReceiver(recvPort, dest);
            return 0;
        }

        string ip;
        int port;
        List<string> paths;

        if (args.Length >= 3)
        {
            ip = args[0];
            port = int.Parse(args[1]);
            paths = args.Skip(2).ToList();
        }
        else
        {
            Console.Write("Send or receive? [S/r]: ");
            var choice = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (choice == "r" || choice == "receive")
            {
                Console.Write("Port to listen on [52525]: ");
                var portInput = (Console.ReadLine() ?? "").Trim();
                int recvPort = string.IsNullOrEmpty(portInput) ? 52525 : int.Parse(portInput);
                Console.Write("Save to folder [Downloads\\NearbyBoost]: ");
                var destInput = (Console.ReadLine() ?? "").Trim();
                string? dest = string.IsNullOrEmpty(destInput) ? null : destInput;
                RunReceiver(recvPort, dest);
                return 0;
            }

            Console.Write("Receiving device's IP address (or 'scan' to find one): ");
            ip = (Console.ReadLine() ?? "").Trim();

            if (ip.Equals("scan", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Scanning...");
                var devices = ScanForDevices();
                if (devices.Count == 0)
                {
                    Console.WriteLine("No devices found \u2014 make sure the other device is on Receive and on the same network.");
                    return 1;
                }
                for (int i = 0; i < devices.Count; i++)
                    Console.WriteLine($"  {i + 1}. {devices[i].Name} ({devices[i].Ip}:{devices[i].Port})");
                Console.Write("Pick a number: ");
                if (!int.TryParse((Console.ReadLine() ?? "").Trim(), out int choiceNum)
                    || choiceNum < 1 || choiceNum > devices.Count)
                {
                    Console.WriteLine("Invalid choice.");
                    return 1;
                }
                var picked = devices[choiceNum - 1];
                ip = picked.Ip;
                port = picked.Port;
            }
            else
            {
                Console.Write("Port [52525]: ");
                var portInput = (Console.ReadLine() ?? "").Trim();
                port = string.IsNullOrEmpty(portInput) ? 52525 : int.Parse(portInput);
            }

            paths = new List<string>();
            while (true)
            {
                Console.Write(paths.Count == 0 ? "File or folder path to send: " : "Add another (blank to finish): ");
                var p = (Console.ReadLine() ?? "").Trim().Trim('"');
                if (string.IsNullOrEmpty(p)) break;
                paths.Add(p);
            }
        }

        if (string.IsNullOrWhiteSpace(ip))
        {
            Console.WriteLine("No IP address given.");
            return 1;
        }
        if (paths.Count == 0)
        {
            Console.WriteLine("No file or folder given.");
            return 1;
        }

        try
        {
            var entries = new List<(string LocalPath, string RelativePath, long Size)>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    string folderName = new DirectoryInfo(p).Name;
                    foreach (var full in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        string rel = folderName + "/" + Path.GetRelativePath(p, full).Replace('\\', '/');
                        entries.Add((full, rel, new FileInfo(full).Length));
                    }
                }
                else if (File.Exists(p))
                {
                    entries.Add((p, Path.GetFileName(p), new FileInfo(p).Length));
                }
                else
                {
                    Console.WriteLine($"Not found, skipping: {p}");
                }
            }
            if (entries.Count == 0)
            {
                Console.WriteLine("Nothing to send.");
                return 1;
            }

            if (entries.Count == 1)
            {
                SendFile(ip, port, entries[0].LocalPath, entries[0].RelativePath);
            }
            else
            {
                string batchName = (paths.Count == 1 && Directory.Exists(paths[0]))
                    ? new DirectoryInfo(paths[0]).Name
                    : $"{entries.Count} files";
                SendBatch(ip, port, batchName, entries);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nTransfer failed: {ex.Message}");
            return 1;
        }
    }

    // ================= SEND =================

    static void SendFile(string ip, int port, string filePath, string relativePath)
    {
        long fileSize = new FileInfo(filePath).Length;
        Console.WriteLine($"Connecting to {ip}:{port} ...");

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

        netStream.ReadTimeout = 35000;
        int response;
        try { response = netStream.ReadByte(); }
        catch (IOException) { Console.WriteLine("\nNo response from the receiver in time."); return; }
        if (response != 1) { Console.WriteLine("\nTransfer was rejected by the receiver."); return; }
        var peer = ReadIdentityBlock(netStream);
        if (peer != null) Console.WriteLine($"Accepted by {peer.Value.Item2}");

        Console.WriteLine($"Sending {relativePath} ({fileSize / (1024.0 * 1024.0):F1} MB)...");

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
            netStream.Write(buffer, 0, read);
            sent += read;

            long nowMs = sw.ElapsedMilliseconds;
            if (nowMs - lastReportMs >= 250)
            {
                double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                double mbPerSec = ((sent - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                double pct = (sent * 100.0) / fileSize;
                string eta = FormatEta(mbPerSec, sent, fileSize);
                Console.Write($"\r{pct,5:F1}%   {mbPerSec,6:F1} MB/s   ETA {eta,-10}");
                lastReportMs = nowMs;
                lastReportBytes = sent;
            }
        }

        sw.Stop();
        double avgMbPerSec = (fileSize / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"\rDone: {relativePath}  \u2014  {avgMbPerSec:F1} MB/s average ({sw.Elapsed.TotalSeconds:F1}s)                ");
    }

    /// <summary>Announces the batch (one accept prompt), then streams files across up to 4
    /// concurrent connections for real parallel throughput.</summary>
    static void SendBatch(string ip, int port, string batchName, List<(string LocalPath, string RelativePath, long Size)> entries)
    {
        long totalBytes = entries.Sum(en => en.Size);

        Console.WriteLine($"Connecting to {ip}:{port} ...");
        using (var control = new TcpClient())
        {
            control.NoDelay = true;
            control.Connect(ip, port);
            using var netStream = control.GetStream();
            netStream.WriteByte(2);
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
            catch (IOException) { Console.WriteLine("\nNo response from the receiver in time."); return; }
            if (response != 1) { Console.WriteLine("\nTransfer was rejected by the receiver."); return; }
            var peer = ReadIdentityBlock(netStream);
            if (peer != null) Console.WriteLine($"Accepted by {peer.Value.Item2}");
        }

        int streamCount = Math.Min(4, entries.Count);
        Console.WriteLine($"Sending {batchName} ({entries.Count} files, {totalBytes / (1024.0 * 1024.0):F1} MB) across {streamCount} parallel streams...");

        var lanes = new List<(string LocalPath, string RelativePath, long Size)>[streamCount];
        for (int i = 0; i < streamCount; i++) lanes[i] = new List<(string, string, long)>();
        for (int i = 0; i < entries.Count; i++) lanes[i % streamCount].Add(entries[i]);

        long totalSent = 0;
        int completedFiles = 0;
        var sw = Stopwatch.StartNew();
        object progressLock = new object();
        long lastReportMs = 0;
        long lastReportBytes = 0;
        Exception? laneError = null;

        var threads = lanes.Where(l => l.Count > 0).Select(lane => new Thread(() =>
        {
            try
            {
                foreach (var entry in lane)
                {
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
                    int read;
                    while ((read = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        netStream.Write(buffer, 0, read);
                        long total = Interlocked.Add(ref totalSent, read);

                        lock (progressLock)
                        {
                            long nowMs = sw.ElapsedMilliseconds;
                            if (nowMs - lastReportMs >= 250)
                            {
                                double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                                double mbPerSec = ((total - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                                double pct = totalBytes > 0 ? (total * 100.0) / totalBytes : 100.0;
                                string eta = FormatEta(mbPerSec, total, totalBytes);
                                Console.Write($"\r{pct,5:F1}%   {mbPerSec,6:F1} MB/s   ETA {eta,-10}");
                                lastReportMs = nowMs;
                                lastReportBytes = total;
                            }
                        }
                    }
                    int done = Interlocked.Increment(ref completedFiles);
                    lock (progressLock)
                    {
                        Console.WriteLine($"\r  [{done}/{entries.Count}] done: {entry.RelativePath}                    ");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (progressLock) { laneError ??= ex; }
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        if (laneError != null)
        {
            Console.WriteLine($"\nTransfer failed: {laneError.Message}");
            return;
        }

        sw.Stop();
        double avgMbPerSec = (totalBytes / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"\rDone: {batchName}  \u2014  {avgMbPerSec:F1} MB/s average ({sw.Elapsed.TotalSeconds:F1}s)                ");
    }

    // ================= RECEIVE =================

    static void RunReceiver(int port, string? destinationOverride)
    {
        string? localIp = GetLocalIPv4();
        var bindAddress = localIp != null ? IPAddress.Parse(localIp) : IPAddress.Any;

        string destinationRoot = destinationOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NearbyBoost");

        var listener = new TcpListener(bindAddress, port);
        listener.Start();
        Console.WriteLine($"Listening on {localIp ?? "0.0.0.0"}:{port}");
        Console.WriteLine($"Saving to {destinationRoot}  (Ctrl+C to stop)");

        var discoveryCts = new CancellationTokenSource();
        string deviceId = GetOrCreateDeviceId();
        new Thread(() => RunDiscoveryResponder(port, deviceId, Environment.MachineName, discoveryCts.Token))
        {
            IsBackground = true
        }.Start();

        // Each connection is dispatched to its own thread immediately so several parallel-batch
        // connections (and independent standalone transfers) can be handled concurrently.
        while (true)
        {
            var client = listener.AcceptTcpClient();
            new Thread(() =>
            {
                try
                {
                    HandleIncoming(client, destinationRoot);
                }
                catch (Exception ex)
                {
                    lock (ConsoleLock) Console.WriteLine($"\nTransfer failed: {ex.Message}");
                }
                finally
                {
                    client.Close();
                }
            })
            { IsBackground = true }.Start();
        }
    }

    static void HandleIncoming(TcpClient client, string destinationRoot)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = BufferSize;
        string senderAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        using var netStream = client.GetStream();

        int transferType = netStream.ReadByte();
        if (transferType == -1) throw new IOException("Connection closed unexpectedly");

        if (transferType == 2)
            HandleBatchAnnouncement(netStream, senderAddress, destinationRoot);
        else
            HandleSingleFileOrBatchMember(netStream, senderAddress, destinationRoot);
    }

    static void HandleBatchAnnouncement(NetworkStream netStream, string senderAddress, string destinationRoot)
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

        lock (BatchLock)
        {
            if (ActiveBatchState != null)
            {
                netStream.WriteByte(0);
                netStream.Flush();
                return;
            }
        }

        bool approved;
        lock (ConsoleLock)
        {
            string fromWho = senderIdentity != null ? $"{senderIdentity.Value.Item2} ({senderAddress})" : senderAddress;
            Console.WriteLine($"\nIncoming: {batchName} ({fileCount} files, {totalBytes / (1024.0 * 1024.0):F1} MB) from {fromWho}");
            Console.Write("Accept? [y/N]: ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            approved = answer == "y" || answer == "yes";
        }

        netStream.WriteByte((byte)(approved ? 1 : 0));
        if (approved) WriteIdentityBlock(netStream);
        netStream.Flush();
        if (!approved)
        {
            lock (ConsoleLock) Console.WriteLine("Rejected.");
            return;
        }

        Directory.CreateDirectory(destinationRoot);
        string destRoot = Path.Combine(destinationRoot, GetNonCollidingFolderName(destinationRoot, batchName));
        Directory.CreateDirectory(destRoot);

        var batch = new BatchState(destRoot, batchName, totalBytes, fileCount);
        lock (BatchLock) { ActiveBatchState = batch; }
        lock (ConsoleLock) Console.WriteLine($"Receiving {batchName}...");
    }

    static void HandleSingleFileOrBatchMember(NetworkStream netStream, string senderAddress, string destinationRoot)
    {
        int nameLen = ReadInt32(netStream);
        if (nameLen <= 0 || nameLen > 4096) throw new IOException("Bad filename length");
        byte[] nameBytes = ReadExact(netStream, nameLen);
        string rawName = Encoding.UTF8.GetString(nameBytes);
        long fileSize = ReadInt64(netStream);
        if (fileSize < 0) throw new IOException("Bad file size");

        BatchState? batch;
        lock (BatchLock) { batch = ActiveBatchState; }

        if (batch != null)
        {
            netStream.WriteByte(1);
            netStream.Flush();
            try
            {
                string relPath = SanitizeRelativePath(rawName);
                string fullSavePath = Path.Combine(batch.DestRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
                string? fileDir = Path.GetDirectoryName(fullSavePath);
                if (!string.IsNullOrEmpty(fileDir)) Directory.CreateDirectory(fileDir);

                ReceiveFileBytes(netStream, fullSavePath, fileSize, n =>
                {
                    long total = Interlocked.Add(ref batch.ReceivedBytes, n);
                    lock (batch)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - batch.LastReportMs >= 250)
                        {
                            double deltaSec = Math.Max(nowMs - batch.LastReportMs, 1) / 1000.0;
                            double mbPerSec = ((total - batch.LastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                            string eta = FormatEta(mbPerSec, total, batch.TotalBytes);
                            double pct = batch.TotalBytes > 0 ? (total * 100.0) / batch.TotalBytes : 100.0;
                            lock (ConsoleLock) Console.Write($"\r{pct,5:F1}%   {mbPerSec,6:F1} MB/s   ETA {eta,-10}");
                            batch.LastReportMs = nowMs;
                            batch.LastReportBytes = total;
                        }
                    }
                });
                lock (ConsoleLock) Console.WriteLine($"\r  received: {relPath}                    ");
            }
            finally
            {
                if (Interlocked.Decrement(ref batch.RemainingFiles) <= 0)
                {
                    lock (BatchLock) { if (ActiveBatchState == batch) ActiveBatchState = null; }
                    double elapsed = Math.Max(Environment.TickCount64 - batch.StartTimeMs, 1) / 1000.0;
                    double avgMbPerSec = (batch.TotalBytes / (1024.0 * 1024.0)) / elapsed;
                    lock (ConsoleLock)
                        Console.WriteLine($"\rSaved {batch.Name} to {batch.DestRoot}  \u2014  {avgMbPerSec:F1} MB/s average                ");
                }
            }
            return;
        }

        // Standalone single file — read the sender's identity first.
        var senderIdentity = ReadIdentityBlock(netStream);
        string fileName = SanitizeFileName(rawName);
        bool approved;
        lock (ConsoleLock)
        {
            string fromWho = senderIdentity != null ? $"{senderIdentity.Value.Item2} ({senderAddress})" : senderAddress;
            Console.WriteLine($"\nIncoming: {fileName} ({fileSize / (1024.0 * 1024.0):F1} MB) from {fromWho}");
            Console.Write("Accept? [y/N]: ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            approved = answer == "y" || answer == "yes";
        }

        netStream.WriteByte((byte)(approved ? 1 : 0));
        if (approved) WriteIdentityBlock(netStream);
        netStream.Flush();
        if (!approved)
        {
            lock (ConsoleLock) Console.WriteLine("Rejected.");
            return;
        }

        Directory.CreateDirectory(destinationRoot);
        string savePath = GetNonCollidingPath(Path.Combine(destinationRoot, fileName));

        long received = 0;
        var sw = Stopwatch.StartNew();
        long lastReportMs = 0;
        long lastReportBytes = 0;

        ReceiveFileBytes(netStream, savePath, fileSize, n =>
        {
            received += n;
            long nowMs = sw.ElapsedMilliseconds;
            if (nowMs - lastReportMs >= 250)
            {
                double deltaSec = Math.Max(nowMs - lastReportMs, 1) / 1000.0;
                double mbPerSec = ((received - lastReportBytes) / (1024.0 * 1024.0)) / deltaSec;
                double pct = (received * 100.0) / fileSize;
                string eta = FormatEta(mbPerSec, received, fileSize);
                lock (ConsoleLock) Console.Write($"\r{pct,5:F1}%   {mbPerSec,6:F1} MB/s   ETA {eta,-10}");
                lastReportMs = nowMs;
                lastReportBytes = received;
            }
        });

        sw.Stop();
        double avgMbPerSec2 = (fileSize / (1024.0 * 1024.0)) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"\rSaved to {savePath}  \u2014  {avgMbPerSec2:F1} MB/s average ({sw.Elapsed.TotalSeconds:F1}s)                ");
    }

    static void ReceiveFileBytes(Stream stream, string savePath, long fileSize, Action<int> onChunk)
    {
        byte[] buffer = new byte[BufferSize];
        long received = 0;
        using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        while (received < fileSize)
        {
            int toRead = (int)Math.Min(buffer.Length, fileSize - received);
            int n = stream.Read(buffer, 0, toRead);
            if (n == 0) throw new IOException("Connection closed before transfer finished");
            fileStream.Write(buffer, 0, n);
            received += n;
            onChunk(n);
        }
    }

    // ================= DISCOVERY =================
    // Lightweight LAN discovery over UDP, separate from the TCP transfer protocol.
    // Requires both devices to already be on the same WiFi/hotspot network — this
    // does not set up that network connection itself.

    const int DiscoveryPort = 52526;
    const string DiscoveryRequestMagic = "NEARBYBOOST_DISCOVER";
    const string DiscoveryResponseMagic = "NEARBYBOOST_HERE";

    static void RunDiscoveryResponder(int tcpPort, string deviceId, string deviceName, CancellationToken token)
    {
        UdpClient? socket = null;
        try
        {
            socket = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
            socket.Client.ReceiveTimeout = 1000;

            while (!token.IsCancellationRequested)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = socket.Receive(ref remote); }
                catch (SocketException) { continue; }

                string text = Encoding.UTF8.GetString(data);
                if (text == DiscoveryRequestMagic)
                {
                    byte[] response = Encoding.UTF8.GetBytes($"{DiscoveryResponseMagic}|{deviceId}|{deviceName}|{tcpPort}");
                    socket.Send(response, response.Length, remote);
                }
            }
        }
        catch { /* socket closed or port busy — responder simply stops */ }
        finally { socket?.Close(); }
    }

    static List<DiscoveredDevice> ScanForDevices(int timeoutMs = 2000)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        try
        {
            using var socket = new UdpClient { EnableBroadcast = true };
            socket.Client.ReceiveTimeout = 300;

            byte[] requestBytes = Encoding.UTF8.GetBytes(DiscoveryRequestMagic);
            var broadcastAddresses = GetBroadcastAddresses();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                foreach (var addr in broadcastAddresses)
                {
                    try { socket.Send(requestBytes, requestBytes.Length, new IPEndPoint(addr, DiscoveryPort)); }
                    catch { /* try the next address */ }
                }

                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = socket.Receive(ref remote);
                    string text = Encoding.UTF8.GetString(data);
                    var parts = text.Split('|');
                    if (parts.Length == 4 && parts[0] == DiscoveryResponseMagic)
                    {
                        string ip = remote.Address.ToString();
                        int port = int.TryParse(parts[3], out int p) ? p : 52525;
                        found[parts[1]] = new DiscoveredDevice(parts[1], parts[2], ip, port);
                    }
                }
                catch (SocketException) { /* no reply this tick */ }
            }
        }
        catch { /* return whatever was found before the failure */ }
        return found.Values.ToList();
    }

    static List<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;
                    byte[] ipBytes = ua.Address.GetAddressBytes();
                    byte[] maskBytes = ua.IPv4Mask.GetAddressBytes();
                    byte[] broadcastBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
                    result.Add(new IPAddress(broadcastBytes));
                }
            }
        }
        catch { /* ignore */ }
        if (result.Count == 0) result.Add(IPAddress.Broadcast);
        return result;
    }

    // ================= SHARED HELPERS =================

    static void WriteInt32(Stream s, int value)
    {
        int network = IPAddress.HostToNetworkOrder(value);
        s.Write(BitConverter.GetBytes(network), 0, 4);
    }

    static void WriteInt64(Stream s, long value)
    {
        long network = IPAddress.HostToNetworkOrder(value);
        s.Write(BitConverter.GetBytes(network), 0, 8);
    }

    static int ReadInt32(Stream s)
    {
        byte[] buf = ReadExact(s, 4);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buf, 0));
    }

    static long ReadInt64(Stream s)
    {
        byte[] buf = ReadExact(s, 8);
        return IPAddress.NetworkToHostOrder(BitConverter.ToInt64(buf, 0));
    }

    static byte[] ReadExact(Stream s, int count)
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

    static string GetNonCollidingPath(string path)
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

    static string GetNonCollidingFolderName(string parentDir, string name)
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

    static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "received_file" : name;
    }

    static string SanitizeRelativePath(string path)
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

    static string GetOrCreateDeviceId()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NearbyBoost", "identity.txt");
        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(existing)) return existing;
            }
            string newId = Guid.NewGuid().ToString("N");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, newId);
            return newId;
        }
        catch
        {
            return Guid.NewGuid().ToString("N"); // not persisted this run, but still valid for one session
        }
    }

    /// <summary>Writes this device's identity: [int32 idLen][id][int32 nameLen][name].</summary>
    static void WriteIdentityBlock(Stream s)
    {
        byte[] idBytes = Encoding.UTF8.GetBytes(GetOrCreateDeviceId());
        byte[] nameBytes = Encoding.UTF8.GetBytes(Environment.MachineName);
        WriteInt32(s, idBytes.Length);
        s.Write(idBytes, 0, idBytes.Length);
        WriteInt32(s, nameBytes.Length);
        s.Write(nameBytes, 0, nameBytes.Length);
    }

    /// <summary>Reads a peer identity block. Returns null on any framing problem rather than throwing.</summary>
    static (string, string)? ReadIdentityBlock(Stream s)
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

    static string? GetLocalIPv4()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .ToList();

            foreach (var ni in interfaces)
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    static string FormatEta(double mbPerSec, long bytesDone, long totalBytes)
    {
        if (mbPerSec <= 0.05) return "calc...";
        long remaining = Math.Max(totalBytes - bytesDone, 0);
        double seconds = (remaining / (1024.0 * 1024.0)) / mbPerSec;
        int s = (int)Math.Max(seconds, 0);
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m{s % 60}s";
        return $"{s / 3600}h{(s % 3600) / 60}m";
    }
}
