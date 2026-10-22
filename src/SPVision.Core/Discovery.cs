using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SPVision.Core;

// "Çevrim içi cihazlar" (Ayarlar › Cihaz ekle and the first-run screen): Hikvision SADP discovery on the local network.
//
// STRICTLY READ-ONLY. SPVision sends exactly one kind of SADP message: the "inquiry" probe built by SadpProbe.Inquiry,
// which only asks devices to describe themselves. No other SADP type is ever built or sent: no activation, no network
// parameter update/modify, no password reset, no "restore"/"exchange code" or anything else. A device that is not
// activated is only labelled "Aktif değil"; the user activates it with Hikvision's own tool or web page.

/// <summary>The SADP inquiry probe: the only SADP message SPVision ever sends (multicast 239.255.255.250:37020).</summary>
public static class SadpProbe
{
    public const string MulticastAddress = "239.255.255.250";
    public const int Port = 37020;

    /// <summary>The only SADP message type SPVision builds. There is deliberately no way to build any other.</summary>
    public const string InquiryType = "inquiry";

    /// <summary>
    /// <c>&lt;Probe&gt;&lt;Uuid&gt;{GUID, upper case}&lt;/Uuid&gt;&lt;Types&gt;inquiry&lt;/Types&gt;&lt;/Probe&gt;</c>.
    /// The GUID is formatted by us (hex digits and hyphens only), so nothing needs escaping.
    /// </summary>
    public static string Inquiry(Guid uuid) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><Probe><Uuid>" + uuid.ToString("D").ToUpperInvariant() +
        "</Uuid><Types>" + InquiryType + "</Types></Probe>";

    public static byte[] InquiryBytes(Guid uuid) => Encoding.UTF8.GetBytes(Inquiry(uuid));
}

/// <summary>One Hikvision device that answered the SADP inquiry (its &lt;ProbeMatch&gt; reply).</summary>
public sealed record DiscoveredDevice
{
    /// <summary>IPv4 address the device reports (dotted, e.g. "192.168.1.2").</summary>
    public required string Address { get; init; }

    /// <summary>Model (DeviceDescription, e.g. "DS-8632NI-I8"); "" when the device did not say.</summary>
    public string Model { get; init; } = "";

    /// <summary>Hikvision's numeric DeviceType code (not used for classification; shown nowhere).</summary>
    public string? DeviceTypeCode { get; init; }

    public int? HttpPort { get; init; }

    /// <summary>Only some devices report it; most report HttpPort only.</summary>
    public int? HttpsPort { get; init; }

    /// <summary>HCNetSDK port (CommandPort, normally 8000).</summary>
    public int? CommandPort { get; init; }

    public string? SoftwareVersion { get; init; }

    /// <summary>Activated flag; null when the device does not report one (older firmware: nothing to activate).</summary>
    public bool? Activated { get; init; }

    /// <summary>MAC address normalized to 12 lower-case hex digits, or null.</summary>
    public string? Mac { get; init; }

    public string? SerialNumber { get; init; }
    public int? DigitalChannels { get; init; }
    public int? AnalogChannels { get; init; }

    /// <summary>False only when the device says Activated=false.</summary>
    public bool IsActivated => Activated != false;

    /// <summary>NVR / DVR rather than a camera or another device (<see cref="RecorderClassifier"/>).</summary>
    public bool IsRecorder => RecorderClassifier.IsRecorder(Model, DigitalChannels, AnalogChannels);

    /// <summary>De-duplication key: MAC, else serial number, else address.</summary>
    public string Key => Mac is { } mac ? "mac:" + mac
        : SerialNumber is { Length: > 0 } serial ? "sn:" + serial.ToUpperInvariant()
        : "ip:" + Address;
}

/// <summary>Defensive parser of SADP replies: anything unexpected is ignored (null), never thrown.</summary>
public static class SadpReply
{
    /// <summary>Largest datagram parsed; real replies are 1–2 KB (DS-8632NI-I8: 1855 bytes).</summary>
    public const int MaxDatagramBytes = 16 * 1024;

    /// <summary>Longest text kept from one field (models, versions and serials are far shorter).</summary>
    public const int MaxFieldLength = 128;

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// A &lt;ProbeMatch&gt; answering an inquiry with a valid IPv4 address; null for everything else (too large, not UTF-8,
    /// not XML, DTD, another root such as our own &lt;Probe&gt;, another SADP type, missing or bad address).
    /// The XML goes through <see cref="IsapiXml.Parse"/>: DTD prohibited, no resolver, size limit.
    /// </summary>
    public static DiscoveredDevice? Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.IsEmpty || datagram.Length > MaxDatagramBytes)
        {
            return null;
        }
        string text;
        try
        {
            text = StrictUtf8.GetString(datagram);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        text = text.TrimStart('﻿').TrimEnd('\0', ' ', '\r', '\n', '\t');
        XElement? root;
        try
        {
            root = IsapiXml.Parse(text).Root;
        }
        catch (XmlException)
        {
            return null;
        }
        if (root is null || root.Name.LocalName != "ProbeMatch")
        {
            return null;
        }
        if (Field(root, "Types") is { } types && !types.Equals(SadpProbe.InquiryType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (Ipv4(Field(root, "IPv4Address")) is not { } address)
        {
            return null;
        }
        return new DiscoveredDevice
        {
            Address = address,
            Model = Field(root, "DeviceDescription") ?? "",
            DeviceTypeCode = Field(root, "DeviceType"),
            HttpPort = Port(Field(root, "HttpPort")),
            HttpsPort = Port(Field(root, "HttpsPort")),
            CommandPort = Port(Field(root, "CommandPort")),
            SoftwareVersion = Field(root, "SoftwareVersion"),
            Activated = Bool(Field(root, "Activated")),
            Mac = Mac(Field(root, "MAC")),
            SerialNumber = Field(root, "DeviceSN"),
            DigitalChannels = Count(Field(root, "DigitalChannelNum")),
            AnalogChannels = Count(Field(root, "AnalogChannelNum")),
        };
    }

    /// <summary>First child's text: control characters dropped, trimmed, at most <see cref="MaxFieldLength"/>; null if empty.</summary>
    static string? Field(XElement root, string name)
    {
        if (IsapiXml.Child(root, name) is not { } element)
        {
            return null;
        }
        var value = new string(element.Value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (value.Length > MaxFieldLength)
        {
            value = value[..MaxFieldLength].Trim();
        }
        return value.Length == 0 ? null : value;
    }

    static readonly Regex DottedQuad = new(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.CultureInvariant);

    /// <summary>Strict dotted IPv4 (no "1" → 0.0.0.1 shorthand); not 0.0.0.0, broadcast or multicast.</summary>
    internal static string? Ipv4(string? text)
    {
        if (text is null || !DottedQuad.IsMatch(text) || !IPAddress.TryParse(text, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        var b = ip.GetAddressBytes();
        if (b.All(x => x == 0) || b.All(x => x == 255) || b[0] is >= 224 and <= 239)
        {
            return null;
        }
        return ip.ToString();
    }

    static int? Port(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;

    static int? Count(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 4096 ? n : null;

    static bool? Bool(string? text) => text switch
    {
        { } s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        { } s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        _ => null,
    };

    /// <summary>"c0-56-e3-aa-bb-cc" / "C0:56:E3:AA:BB:CC" → "c056e3aabbcc"; null unless exactly 12 hex digits (not all zero).</summary>
    internal static string? Mac(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var hex = new string(text.Where(char.IsAsciiHexDigit).Select(char.ToLowerInvariant).ToArray());
        var separatorsOnly = text.All(c => char.IsAsciiHexDigit(c) || c is '-' or ':' or '.');
        return separatorsOnly && hex.Length == 12 && hex.Any(c => c != '0') ? hex : null;
    }
}

/// <summary>
/// Recorder (NVR / DVR) or not, for the "Sadece kayıt cihazları" filter. SADP has no field that says so directly
/// (DeviceType is an undocumented number), so the model decides, then the channel count:
/// <list type="number">
/// <item>Hikvision recorder series DS-7…, DS-8…, DS-9… (DS-7608NI-K2, DS-8632NI-I8, DS-96128NI-I24, iDS-7216HUHI-M2),
/// or "NVR" / "DVR" in the model (HiLook NVR-104MH-C, DVR-204Q-K1), or HiWatch HWN- / HWD- → recorder.</item>
/// <item>Camera series DS-2… (DS-2CD, DS-2DE, DS-2TD…), IPC-, HWI- → not a recorder.</item>
/// <item>Anything else (alarm panels, access control, unknown models): a recorder only with more than one channel.</item>
/// </list>
/// </summary>
public static class RecorderClassifier
{
    static readonly Regex RecorderSeries = new(@"^I?DS-[789]\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex CameraSeries = new(@"^(I?DS-2|IPC-|HWI-)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRecorder(string? model, int? digitalChannels, int? analogChannels)
    {
        var m = (model ?? "").Trim();
        if (RecorderSeries.IsMatch(m) ||
            m.Contains("NVR", StringComparison.OrdinalIgnoreCase) || m.Contains("DVR", StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith("HWN-", StringComparison.OrdinalIgnoreCase) || m.StartsWith("HWD-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (CameraSeries.IsMatch(m))
        {
            return false;
        }
        return (digitalChannels ?? 0) + (analogChannels ?? 0) > 1;
    }
}

/// <summary>Replies of one scan, de-duplicated by <see cref="DiscoveredDevice.Key"/> (devices answer more than once).</summary>
public sealed class DiscoveredDeviceSet
{
    /// <summary>A flood of fake replies cannot grow the list without bound.</summary>
    public const int MaxDevices = 512;

    readonly Dictionary<string, DiscoveredDevice> _byKey = new(StringComparer.Ordinal);

    public int Count => _byKey.Count;

    /// <summary>Adds or replaces (the later reply of the same device wins); true when the device is new.</summary>
    public bool Add(DiscoveredDevice device)
    {
        if (_byKey.ContainsKey(device.Key))
        {
            _byKey[device.Key] = device;
            return false;
        }
        if (_byKey.Count >= MaxDevices)
        {
            return false;
        }
        _byKey[device.Key] = device;
        return true;
    }

    /// <summary>In address order (192.168.1.2 before 192.168.1.10), then model.</summary>
    public IReadOnlyList<DiscoveredDevice> Items =>
        _byKey.Values.OrderBy(d => AddressOrder(d.Address)).ThenBy(d => d.Model, StringComparer.OrdinalIgnoreCase).ToList();

    static uint AddressOrder(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
            : uint.MaxValue;
}

/// <summary>Values a picked row writes into the add-device form.</summary>
public sealed record DiscoveryFill(string Host, int HttpPort, int RtspPort, int SdkPort, bool UseHttps, string Model);

public enum DiscoveryStatus
{
    /// <summary>Activated (or not reporting the flag) and not saved yet.</summary>
    Active,

    /// <summary>Its address is already a saved device.</summary>
    Added,

    /// <summary>Activated=false: needs Hikvision's tool or web page first (SPVision never activates).</summary>
    Inactive,
}

/// <summary>Rules and texts of the "Çevrim içi cihazlar" list.</summary>
public static class DiscoveryRules
{
    /// <summary>SADP does not report the RTSP port; Hikvision's default.</summary>
    public const int DefaultRtspPort = 554;

    public const int DefaultSdkPort = 8000;

    /// <summary>
    /// Form values of a picked device. HTTPS rule: "HTTPS kullan" is ticked when the device reports an HTTPS port
    /// (that port is used) or its HTTP port is 443; otherwise HTTP with the reported port (80 when missing). Most
    /// firmware (DS-8632NI-I8 V4.61 included) reports only HttpPort, so they start on HTTP; the user can tick HTTPS
    /// (the port follows 80 → 443, <see cref="HttpsPort.Follow"/>).
    /// </summary>
    public static DiscoveryFill Fill(DiscoveredDevice device)
    {
        var https = device.HttpsPort is not null || device.HttpPort == HttpsPort.Https;
        var port = device.HttpsPort ?? device.HttpPort ?? (https ? HttpsPort.Https : HttpsPort.Http);
        return new DiscoveryFill(device.Address, port, DefaultRtspPort, device.CommandPort ?? DefaultSdkPort, https, device.Model);
    }

    /// <summary>Inactive first (a saved device that was reset needs activating), then Eklendi, else Aktif.</summary>
    public static DiscoveryStatus Status(DiscoveredDevice device, IEnumerable<string> savedHosts)
    {
        if (!device.IsActivated)
        {
            return DiscoveryStatus.Inactive;
        }
        return savedHosts.Any(h => string.Equals(h?.Trim(), device.Address, StringComparison.OrdinalIgnoreCase))
            ? DiscoveryStatus.Added
            : DiscoveryStatus.Active;
    }

    public static string Badge(DiscoveryStatus status) => status switch
    {
        DiscoveryStatus.Added => "Eklendi",
        DiscoveryStatus.Inactive => "Aktif değil",
        _ => "Aktif",
    };

    /// <summary>Rows shown: all, or only recorders when "Sadece kayıt cihazları" is on.</summary>
    public static IReadOnlyList<DiscoveredDevice> Visible(IEnumerable<DiscoveredDevice> devices, bool onlyRecorders) =>
        devices.Where(d => !onlyRecorders || d.IsRecorder).ToList();

    /// <summary>PORT (HTTP / SDK) column: "HTTP 80 / 8000", "HTTPS 443 / 8000".</summary>
    public static string Ports(DiscoveredDevice device)
    {
        var fill = Fill(device);
        var sdk = device.CommandPort?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return string.Create(CultureInfo.InvariantCulture, $"{(fill.UseHttps ? "HTTPS" : "HTTP")} {fill.HttpPort} / {sdk}");
    }

    static readonly Regex GluedBuild = new(@"(?<=\S)build", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>YAZILIM column: "V4.61.025build 220905" → "V4.61.025 build 220905"; "—" when unknown.</summary>
    public static string Software(DiscoveredDevice device) =>
        device.SoftwareVersion is { Length: > 0 } version ? GluedBuild.Replace(version, " build", 1) : "—";

    /// <summary>MODEL column; "Hikvision cihazı" when the device gave no model.</summary>
    public static string Model(DiscoveredDevice device) => device.Model.Length > 0 ? device.Model : "Hikvision cihazı";

    /// <summary>"· taranıyor" / "· 1 cihaz bulundu" / "· cihaz bulunamadı" after ÇEVRİM İÇİ CİHAZLAR.</summary>
    public static string CountText(bool scanning, int visible) =>
        scanning ? "taranıyor"
        : visible == 0 ? "cihaz bulunamadı"
        : string.Create(CultureInfo.InvariantCulture, $"{visible} cihaz bulundu");

    public const string Scanning = "Yerel ağ taranıyor…";
    public const string NotFound = "Cihaz bulunamadı — IP adresini elle girebilirsiniz";
    public const string NoRecorder = "Kayıt cihazı bulunamadı — diğer cihazlar için “Sadece kayıt cihazları”nı kapatın";
    public const string InactiveHint =
        "Bu cihaz etkinleştirilmemiş. Önce Hikvision'ın SADP aracı ya da cihazın web sayfasıyla bir yönetici şifresi belirleyerek " +
        "etkinleştirin; SPVision cihazları etkinleştirmez.";
    public const string AlreadyAdded = "Bu cihaz zaten ekli; düzenlemek için listedeki satırına tıklayın.";
}
