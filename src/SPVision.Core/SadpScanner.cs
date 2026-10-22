using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SPVision.Core;

/// <summary>Result of one scan: the devices found and, when something got in the way, a short reason (else null).</summary>
public sealed record SadpScanResult(IReadOnlyList<DiscoveredDevice> Devices, int Interfaces, string? Problem);

/// <summary>
/// Hikvision SADP discovery: sends ONE inquiry probe (<see cref="SadpProbe.Inquiry"/>) on every active IPv4 interface
/// to 239.255.255.250:37020 and collects &lt;ProbeMatch&gt; replies for a short while.
/// <para>
/// STRICTLY READ-ONLY: the inquiry is the only SADP message this class (and SPVision) sends. Never add activation,
/// network parameter update/modify, password reset or any other SADP type here — devices are configured with
/// Hikvision's own tools. Replies are parsed defensively (<see cref="SadpReply.Parse"/>) and de-duplicated by MAC.
/// </para>
/// Replies arrive on UDP 37020 (devices answer to the group's port). Windows Firewall may drop them without an inbound
/// rule (the installer adds one for SPVision.exe); then the list stays empty and the user types the address instead.
/// </summary>
public static class SadpScanner
{
    /// <summary>How long replies are collected (the real recorder answers within ~100 ms; some other devices are slower).</summary>
    public static readonly TimeSpan DefaultListen = TimeSpan.FromSeconds(3);

    /// <summary>A socket that keeps failing is given up instead of spinning.</summary>
    const int MaxReceiveErrors = 16;

    /// <summary>Cancellable; never blocks the caller's thread for the listening window (call it via Task.Run from UI).</summary>
    public static async Task<SadpScanResult> ScanAsync(TimeSpan listen, CancellationToken ct = default)
    {
        var locals = LocalIPv4Addresses();
        if (locals.Count == 0)
        {
            return new SadpScanResult([], 0, "Etkin bir ağ bağlantısı yok.");
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        string? problem = null;
        try
        {
            // Shared with Hikvision's SADP tool if it is open (both get the multicast replies).
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, SadpProbe.Port));
        }
        catch (SocketException)
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // only unicast replies can arrive now
            problem = "SADP portu (UDP 37020) başka bir uygulamada; bazı cihazlar görünmeyebilir.";
        }

        var group = IPAddress.Parse(SadpProbe.MulticastAddress);
        foreach (var local in locals)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local));
            }
            catch (SocketException)
            {
            }
        }
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
        }
        catch (SocketException)
        {
        }

        // The one and only message: an inquiry (read-only). Same probe (same Uuid) on every interface.
        var probe = SadpProbe.InquiryBytes(Guid.NewGuid());
        var target = new IPEndPoint(group, SadpProbe.Port);
        var sent = 0;
        foreach (var local in locals)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                await socket.SendToAsync(probe, SocketFlags.None, target, ct).ConfigureAwait(false);
                sent++;
            }
            catch (SocketException)
            {
            }
        }
        if (sent == 0)
        {
            return new SadpScanResult([], locals.Count, "Tarama isteği gönderilemedi.");
        }

        var found = new DiscoveredDeviceSet();
        var buffer = new byte[64 * 1024]; // a whole datagram; anything over SadpReply.MaxDatagramBytes is ignored
        var errors = 0;
        using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            window.CancelAfter(listen);
            while (!window.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), window.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (++errors >= MaxReceiveErrors)
                    {
                        break;
                    }
                    continue;
                }
                if (received.ReceivedBytes is > 0 and <= SadpReply.MaxDatagramBytes &&
                    SadpReply.Parse(buffer.AsSpan(0, received.ReceivedBytes)) is { } device)
                {
                    found.Add(device);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return new SadpScanResult(found.Items, locals.Count, problem);
    }

    /// <summary>IPv4 addresses of interfaces that are up and can multicast (no loopback, no tunnels).</summary>
    static List<IPAddress> LocalIPv4Addresses()
    {
        var result = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                        !result.Contains(address))
                    {
                        result.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result;
    }
}
