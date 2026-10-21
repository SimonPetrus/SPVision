using System.Globalization;
using System.Xml.Linq;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Ağ (phase 2). Read: ISAPI/System/Network/interfaces (IPv4 part of each NIC),
// ISAPI/System/Network/Bond, ISAPI/Security/adminAccesses (ports; read only: the same document holds the TLS
// settings, and adminAccess is never written). Write: ISAPI/System/Network/interfaces/{id}/ipAddress as a risky
// operation (RiskyWrites.NetworkAddress). Unit tested.

/// <summary>IPv4 part of an ISAPI IPAddress element. Text values as the device sent them ("" when missing).</summary>
public sealed record NetworkAddress(bool Dhcp, string Ip, string Mask, string Gateway, string Dns1, string Dns2)
{
    public const string AddressingPath = "addressingType";
    public const string IpPath = "ipAddress";
    public const string MaskPath = "subnetMask";
    public const string GatewayPath = "DefaultGateway/ipAddress";
    public const string Dns1Path = "PrimaryDNS/ipAddress";
    public const string Dns2Path = "SecondaryDNS/ipAddress";

    /// <summary><paramref name="address"/> = the IPAddress element; null when it has no IPv4 address.</summary>
    public static NetworkAddress? Parse(XElement? address)
    {
        if (address is null || IsapiXml.Get(address, IpPath) is not { } ip)
        {
            return null;
        }
        return new NetworkAddress(
            string.Equals(IsapiXml.Get(address, AddressingPath), "dynamic", StringComparison.OrdinalIgnoreCase),
            ip,
            IsapiXml.Get(address, MaskPath) ?? "",
            IsapiXml.Get(address, GatewayPath) ?? "",
            IsapiXml.Get(address, Dns1Path) ?? "",
            IsapiXml.Get(address, Dns2Path) ?? "");
    }

    public static NetworkAddress? ParseDocument(XDocument doc) =>
        doc.Root?.Name.LocalName == "IPAddress" ? Parse(doc.Root) : null;

    public static string ModeText(bool dhcp) => dhcp ? "DHCP (otomatik)" : "Elle (sabit)";
}

public sealed record NetworkInterfaceInfo(int Id, NetworkAddress? Address, bool DefaultConnection)
{
    public static List<NetworkInterfaceInfo> ParseList(XDocument doc) =>
        doc.Root!.Elements().Where(e => e.Name.LocalName == "NetworkInterface")
            .Select(e => new NetworkInterfaceInfo(
                IsapiXml.GetInt(e, "id") ?? 0,
                NetworkAddress.Parse(IsapiXml.Child(e, "IPAddress")),
                IsapiXml.GetBool(e, "defaultConnection") == true))
            .Where(i => i.Id > 0)
            .ToList();

    /// <summary>
    /// The interface SPVision reaches the recorder through: the one whose address is the device's host, else the
    /// recorder's default connection, else the first.
    /// </summary>
    public static NetworkInterfaceInfo? ForHost(IReadOnlyList<NetworkInterfaceInfo> interfaces, string host) =>
        interfaces.FirstOrDefault(i => i.Address is { } a && string.Equals(a.Ip, host.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? interfaces.FirstOrDefault(i => i.DefaultConnection)
        ?? interfaces.FirstOrDefault();
}

/// <summary>Network bonding: when on, the NICs share the bond's address and the per-interface address is not used.</summary>
public sealed record BondInfo(bool Enabled)
{
    public static BondInfo Parse(XDocument doc) =>
        new(doc.Root!.Descendants().Where(e => e.Name.LocalName == "Bond").Any(b => IsapiXml.GetBool(b, "enabled") == true));
}

/// <summary>Ports of ISAPI/Security/adminAccesses (only protocol + port are read; nothing else of that document).</summary>
public sealed record PortSettings(int? Http, int? Https, int? Rtsp, int? Server)
{
    public static PortSettings Parse(XDocument doc)
    {
        int? Port(string protocol) => doc.Root!.Elements()
            .Where(e => e.Name.LocalName == "AdminAccessProtocol" &&
                        string.Equals(IsapiXml.Get(e, "protocol"), protocol, StringComparison.OrdinalIgnoreCase))
            .Select(e => IsapiXml.GetInt(e, "portNo")).FirstOrDefault(p => p is > 0);
        return new PortSettings(Port("HTTP"), Port("HTTPS"), Port("RTSP"), Port("DEV_MANAGE"));
    }

    public static string Text(int? port) => port?.ToString(CultureInfo.InvariantCulture) ?? "—";
}

/// <summary>Dotted IPv4 helpers (no host names, no IPv6).</summary>
public static class Ipv4
{
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        var parts = (text ?? "").Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0') ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var b) || b > 255)
            {
                return false;
            }
            value = (value << 8) | (uint)b;
        }
        return true;
    }

    /// <summary>A contiguous mask from /8 to /30.</summary>
    public static bool IsMask(string? text)
    {
        if (!TryParse(text, out var mask))
        {
            return false;
        }
        var inverted = ~mask;
        var contiguous = (inverted & (inverted + 1)) == 0;
        var bits = 32 - System.Numerics.BitOperations.PopCount(inverted);
        return contiguous && bits is >= 8 and <= 30;
    }

    /// <summary>A unicast host address (not 0.x, 127.x, multicast or reserved).</summary>
    public static bool IsHost(string? text) =>
        TryParse(text, out var ip) && (ip >> 24) is not (0 or 127) && (ip >> 24) < 224;

    public static bool SameSubnet(string a, string b, string mask) =>
        TryParse(a, out var x) && TryParse(b, out var y) && TryParse(mask, out var m) && (x & m) == (y & m);

    /// <summary>The network or broadcast address of its subnet (not usable by a device).</summary>
    public static bool IsNetworkOrBroadcast(string ip, string mask) =>
        TryParse(ip, out var x) && TryParse(mask, out var m) && ((x & ~m) == 0 || (x & ~m) == ~m);
}

/// <summary>Ağ tab: validation and "before → after" edits of the IPAddress document.</summary>
public static class NetworkPlan
{
    /// <summary>Null when <paramref name="after"/> may be sent; otherwise what is wrong (Turkish). Unchanged fields are not judged.</summary>
    public static string? Validate(NetworkAddress before, NetworkAddress after)
    {
        if (after.Dhcp)
        {
            return null;
        }
        var addressChanged = before.Dhcp || after.Ip != before.Ip || after.Mask != before.Mask || after.Gateway != before.Gateway;
        if (addressChanged)
        {
            if (!Ipv4.IsHost(after.Ip))
            {
                return "IP adresi geçerli değil (ör. 192.168.1.2).";
            }
            if (!Ipv4.IsMask(after.Mask))
            {
                return "Alt ağ maskesi geçerli değil (ör. 255.255.255.0).";
            }
            if (Ipv4.IsNetworkOrBroadcast(after.Ip, after.Mask))
            {
                return "Bu IP adresi alt ağın ağ veya yayın adresidir; cihaza verilemez.";
            }
            if (!Ipv4.IsHost(after.Gateway))
            {
                return "Ağ geçidi geçerli değil (ör. 192.168.1.1).";
            }
            if (after.Gateway == after.Ip)
            {
                return "Ağ geçidi cihazın kendi adresi olamaz.";
            }
            if (!Ipv4.SameSubnet(after.Ip, after.Gateway, after.Mask))
            {
                return "Ağ geçidi, IP adresi ve maskeyle aynı alt ağda olmalı.";
            }
        }
        foreach (var (label, value, old) in new[] { ("Birincil DNS", after.Dns1, before.Dns1), ("İkincil DNS", after.Dns2, before.Dns2) })
        {
            if (value != old && !(Ipv4.IsHost(value) || value == "0.0.0.0"))
            {
                return $"{label} geçerli değil (ör. 8.8.8.8; yoksa 0.0.0.0).";
            }
        }
        return null;
    }

    public static List<ConfigEdit> Edits(NetworkAddress before, NetworkAddress after)
    {
        var edits = new List<ConfigEdit>();
        if (after.Dhcp != before.Dhcp)
        {
            edits.Add(new SetValueEdit("Adres alma", NetworkAddress.AddressingPath, after.Dhcp ? "dynamic" : "static",
                NetworkAddress.ModeText(before.Dhcp), NetworkAddress.ModeText(after.Dhcp)));
        }
        if (after.Dhcp)
        {
            return edits; // the DHCP server decides the rest
        }
        void Add(string label, string path, string old, string value)
        {
            if (value != old)
            {
                edits.Add(new SetValueEdit(label, path, value, old, value));
            }
        }
        Add("IP adresi", NetworkAddress.IpPath, before.Ip, after.Ip);
        Add("Alt ağ maskesi", NetworkAddress.MaskPath, before.Mask, after.Mask);
        Add("Ağ geçidi", NetworkAddress.GatewayPath, before.Gateway, after.Gateway);
        Add("Birincil DNS", NetworkAddress.Dns1Path, before.Dns1, after.Dns1);
        Add("İkincil DNS", NetworkAddress.Dns2Path, before.Dns2, after.Dns2);
        return edits;
    }

    /// <summary>
    /// The address SPVision should use after a successful change, when it reached the recorder by the old static
    /// address; null when nothing to offer (DHCP: the new address is not known here).
    /// </summary>
    public static string? NewHost(string deviceHost, NetworkAddress before, NetworkAddress after) =>
        !after.Dhcp && after.Ip != before.Ip && string.Equals(deviceHost.Trim(), before.Ip, StringComparison.OrdinalIgnoreCase)
            ? after.Ip
            : null;
}
