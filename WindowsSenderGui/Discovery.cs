using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NearbyBoostSenderGui;

public record DiscoveredDevice(string DeviceId, string Name, string Ip, int Port);

/// <summary>
/// Lightweight LAN discovery over UDP, separate from the TCP transfer protocol.
/// A device in Receive mode runs <see cref="RunResponder"/>; a device about to send
/// calls <see cref="Scan"/> to find it. Requires both devices to already be on the same
/// WiFi/hotspot network — this does not set up that network connection itself.
/// </summary>
public static class Discovery
{
    public const int DiscoveryPort = 52526;
    private const string RequestMagic = "NEARBYBOOST_DISCOVER";
    private const string ResponseMagic = "NEARBYBOOST_HERE";

    /// <summary>Blocking — call from a background thread. Answers discovery broadcasts until cancelled.</summary>
    public static void RunResponder(int tcpPort, string deviceId, string deviceName, CancellationToken token)
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
                try
                {
                    data = socket.Receive(ref remote);
                }
                catch (SocketException)
                {
                    continue; // timed out, loop and re-check cancellation
                }

                string text = Encoding.UTF8.GetString(data);
                if (text == RequestMagic)
                {
                    byte[] response = Encoding.UTF8.GetBytes($"{ResponseMagic}|{deviceId}|{deviceName}|{tcpPort}");
                    socket.Send(response, response.Length, remote);
                }
            }
        }
        catch { /* socket closed or port busy — responder simply stops */ }
        finally { socket?.Close(); }
    }

    /// <summary>Blocking — call from a background thread. Broadcasts a discovery request and collects replies for timeoutMs.</summary>
    public static List<DiscoveredDevice> Scan(int timeoutMs = 2000)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        try
        {
            using var socket = new UdpClient { EnableBroadcast = true };
            socket.Client.ReceiveTimeout = 300;

            byte[] requestBytes = Encoding.UTF8.GetBytes(RequestMagic);
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
                    if (parts.Length == 4 && parts[0] == ResponseMagic)
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

    private static List<IPAddress> GetBroadcastAddresses()
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
}
