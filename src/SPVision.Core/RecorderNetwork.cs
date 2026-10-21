using System.Globalization;
using System.Xml.Linq;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Ağ (phase 2). Read: ISAPI/System/Network/interfaces (IPv4 part of each NIC),
// ISAPI/System/Network/Bond, ISAPI/Security/adminAccesses (ports; the same document holds the TLS settings).
// Writes, both risky operations: ISAPI/System/Network/interfaces/{id}/ipAddress (RiskyWrites.NetworkAddress) and the
// four port numbers of ISAPI/Security/adminAccesses (RiskyWrites.AdminPorts: the document just read with only those
// portNo values changed — IsapiWritePolicy.CheckAdminPortsBody). Unit tested.

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

/// <summary>Ports of ISAPI/Security/adminAccesses (only protocol + port are parsed; also the Ağ tab's draft).</summary>
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

/// <summary>
/// Allowed range of one protocol's port (adminAccesses/capabilities portNo min/max) and its default (def/default), which
/// is accepted even outside the range: the DS-8632NI-I8 reports RTSP 1024–65535 although its default is 554.
/// </summary>
public sealed record PortLimit(int Min, int Max, int? Default = null);

/// <summary>One port change of the Ağ tab: protocol as the device names it (HTTP, HTTPS, RTSP, DEV_MANAGE).</summary>
public sealed record PortChange(string Protocol, int Old, int New)
{
    public string Label => PortPlan.Label(Protocol);
}

/// <summary>The ports SPVision itself uses for a device (ISAPI HTTP(S), RTSP for Flyleaf, HCNetSDK login).</summary>
public sealed record DevicePorts(int Http, int Rtsp, int Sdk);

/// <summary>
/// Ağ tab, ports (risky write <see cref="RiskyWrites.AdminPorts"/>): validation, warnings, the body (the document just
/// read with only portNo of HTTP / HTTPS / RTSP / DEV_MANAGE changed) and which of SPVision's own ports follow. Unit tested.
/// </summary>
public static class PortPlan
{
    public const string Http = "HTTP";
    public const string Https = "HTTPS";
    public const string Rtsp = "RTSP";
    public const string Server = "DEV_MANAGE";

    public static string Label(string protocol) => protocol switch
    {
        Server => "Sunucu (SDK)",
        _ => protocol,
    };

    /// <summary>portNo min/max per protocol from ISAPI/Security/adminAccesses/capabilities (missing = 1–65535).</summary>
    public static Dictionary<string, PortLimit> ParseLimits(XDocument? capabilities)
    {
        var limits = new Dictionary<string, PortLimit>(StringComparer.Ordinal);
        if (capabilities?.Root is not { } root)
        {
            return limits;
        }
        foreach (var entry in root.Elements().Where(e => e.Name.LocalName == "AdminAccessProtocol"))
        {
            if (IsapiXml.Get(entry, "protocol") is { Length: > 0 } protocol && !limits.ContainsKey(protocol))
            {
                var min = IsapiXml.IntAttribute(entry, "portNo", "min") ?? 1;
                var max = IsapiXml.IntAttribute(entry, "portNo", "max") ?? 65535;
                var def = IsapiXml.IntAttribute(entry, "portNo", "def") ?? IsapiXml.IntAttribute(entry, "portNo", "default");
                limits[protocol] = new PortLimit(Math.Max(1, min), Math.Min(65535, max), def);
            }
        }
        return limits;
    }

    /// <summary>The changed ports, in the order HTTP, HTTPS, RTSP, server; ports the device did not report are never changed.</summary>
    public static List<PortChange> Changes(PortSettings before, PortSettings after)
    {
        var changes = new List<PortChange>();
        void Add(string protocol, int? old, int? value)
        {
            if (old is { } o && value is { } v && v != o)
            {
                changes.Add(new PortChange(protocol, o, v));
            }
        }
        Add(Http, before.Http, after.Http);
        Add(Https, before.Https, after.Https);
        Add(Rtsp, before.Rtsp, after.Rtsp);
        Add(Server, before.Server, after.Server);
        return changes;
    }

    /// <summary>Text of a port box → port, or null when it is not a plain number 1–65535.</summary>
    public static int? ParsePort(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length is >= 1 and <= 5 && t.All(char.IsAsciiDigit) && t[0] != '0' &&
               int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port <= 65535
            ? port
            : null;
    }

    /// <summary>Null when the changes may be sent; otherwise what is wrong (Turkish). Uniqueness counts every entry (WebSocket, IOT…).</summary>
    public static string? Validate(XDocument current, IReadOnlyList<PortChange> changes, IReadOnlyDictionary<string, PortLimit>? limits = null)
    {
        foreach (var change in changes)
        {
            if (change.New is < 1 or > 65535)
            {
                return $"{change.Label} portu 1–65535 arasında olmalı.";
            }
            // The protocol's default (device "def" or the usual 80 / 443 / 554 / 8000) is always accepted: a change can be undone.
            var isDefault = change.New == limits?.GetValueOrDefault(change.Protocol)?.Default ||
                            (Defaults.TryGetValue(change.New, out var usualFor) && usualFor == change.Protocol);
            if (limits?.GetValueOrDefault(change.Protocol) is { } limit && (change.New < limit.Min || change.New > limit.Max) && !isDefault)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Cihaz {change.Label} portu için {limit.Min}–{limit.Max} arasını kabul ediyor.");
            }
        }
        var final = new List<(string Protocol, int Port)>();
        foreach (var entry in current.Root!.Elements().Where(e => e.Name.LocalName == "AdminAccessProtocol"))
        {
            var protocol = IsapiXml.Get(entry, "protocol") ?? "";
            if (IsapiXml.GetInt(entry, "portNo") is not { } port)
            {
                continue;
            }
            final.Add((protocol, changes.FirstOrDefault(c => c.Protocol == protocol)?.New ?? port));
        }
        foreach (var change in changes)
        {
            if (final.Count(f => f.Protocol == change.Protocol) != 1)
            {
                return $"{change.Label} cihazın port listesinde tek bir kez bulunmuyor; değiştirilemez.";
            }
            if (final.FirstOrDefault(f => f.Port == change.New && f.Protocol != change.Protocol) is { Protocol.Length: > 0 } clash)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{change.New} portunu cihazda {Label(clash.Protocol)} kullanıyor; her hizmetin portu farklı olmalı.");
            }
        }
        return null;
    }

    static readonly Dictionary<int, string> WellKnown = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "e-posta (SMTP)", [53] = "DNS", [67] = "DHCP", [68] = "DHCP",
        [69] = "TFTP", [110] = "e-posta (POP3)", [123] = "saat eşitleme (NTP)", [135] = "Windows RPC", [137] = "NetBIOS",
        [138] = "NetBIOS", [139] = "NetBIOS", [143] = "e-posta (IMAP)", [161] = "SNMP", [162] = "SNMP", [389] = "LDAP",
        [445] = "Windows dosya paylaşımı", [465] = "e-posta (SMTP)", [587] = "e-posta (SMTP)", [993] = "e-posta (IMAP)",
        [995] = "e-posta (POP3)", [1433] = "SQL Server", [1900] = "UPnP", [3306] = "MySQL", [3389] = "Uzak Masaüstü",
        [5353] = "mDNS", [5900] = "VNC", [8080] = "vekil sunucular ve başka web arayüzleri", [37020] = "Hikvision cihaz bulma (SADP)",
    };

    static readonly Dictionary<int, string> Defaults = new() { [80] = Http, [443] = Https, [554] = Rtsp, [8000] = Server };

    /// <summary>Allowed but worth a warning in the confirmation: a well-known service port or another protocol's usual port.</summary>
    public static List<string> Warnings(IReadOnlyList<PortChange> changes)
    {
        var warnings = new List<string>();
        foreach (var change in changes)
        {
            var port = change.New.ToString(CultureInfo.InvariantCulture);
            if (WellKnown.TryGetValue(change.New, out var service))
            {
                warnings.Add($"{change.Label} için seçilen {port} genellikle {service} için kullanılır; çakışma olabilir.");
            }
            else if (Defaults.TryGetValue(change.New, out var usual) && usual != change.Protocol)
            {
                warnings.Add($"{port} normalde {Label(usual)} portudur; programlar bu cihaza yanlış hizmetle bağlanmaya çalışabilir.");
            }
            else if (change.New < 1024 && !Defaults.ContainsKey(change.New))
            {
                warnings.Add($"{port} 1024'ten küçük, sistem hizmetlerine ayrılmış bir porttur; çakışma olabilir.");
            }
        }
        return warnings;
    }

    /// <summary>
    /// A copy of the document just read with only the changed portNo values replaced. Each protocol must appear once and
    /// still hold the old port (otherwise the device changed meanwhile). Throws <see cref="ConfigEditException"/>.
    /// </summary>
    public static XDocument Apply(XDocument current, IReadOnlyList<PortChange> changes)
    {
        var body = new XDocument(current);
        var root = body.Root ?? throw new ConfigEditException("Cihazdan boş belge geldi.");
        foreach (var change in changes)
        {
            var entries = root.Elements().Where(e => e.Name.LocalName == "AdminAccessProtocol" &&
                                                     string.Equals(IsapiXml.Get(e, "protocol"), change.Protocol, StringComparison.Ordinal)).ToList();
            if (entries.Count != 1 || IsapiXml.Child(entries[0], "portNo") is not { } port)
            {
                throw new ConfigEditException($"{change.Label} cihazın port listesinde tek bir kez bulunmuyor.");
            }
            if (IsapiXml.GetInt(entries[0], "portNo") != change.Old)
            {
                throw new ConfigEditException($"{change.Label} portu cihazda değişmiş; yeniden okuyun.");
            }
            port.Value = change.New.ToString(CultureInfo.InvariantCulture);
        }
        return body;
    }

    /// <summary>
    /// SPVision's own ports after the change, or null when none of them follows: the ISAPI port follows HTTPS (when the
    /// device uses HTTPS) or HTTP, RTSP follows RTSP and the SDK login port follows the server port — each only when
    /// SPVision used that protocol's old port.
    /// </summary>
    public static DevicePorts? FollowDevice(bool useHttps, DevicePorts current, IReadOnlyList<PortChange> changes)
    {
        int Follow(string protocol, int used) =>
            changes.FirstOrDefault(c => c.Protocol == protocol && c.Old == used)?.New ?? used;
        var next = new DevicePorts(Follow(useHttps ? Https : Http, current.Http), Follow(Rtsp, current.Rtsp), Follow(Server, current.Sdk));
        return next == current ? null : next;
    }

    /// <summary>"HTTPS 443 → 8443" lines of SPVision's own ports.</summary>
    public static List<string> DeviceChangeLines(bool useHttps, DevicePorts before, DevicePorts after)
    {
        var lines = new List<string>();
        void Add(string label, int a, int b)
        {
            if (a != b)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {a} → {b}"));
            }
        }
        Add(useHttps ? "HTTPS portu" : "HTTP portu", before.Http, after.Http);
        Add("RTSP portu", before.Rtsp, after.Rtsp);
        Add("Sunucu (SDK) portu", before.Sdk, after.Sdk);
        return lines;
    }
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
