using System.Text;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>"Çevrim içi cihazlar": SADP inquiry probe, defensive reply parser, recorder classifier, de-duplication, form fill.</summary>
public class DiscoveryTests
{
    // Shape of a real DS-8632NI-I8 reply (V4.61), identifying values replaced.
    const string RecorderReply = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ProbeMatch>
        <Uuid>6A1C2F10-0000-4000-8000-000000000001</Uuid>
        <Types>inquiry</Types>
        <Encrypt>true</Encrypt>
        <Salt>abc</Salt>
        <DeviceType>42514</DeviceType>
        <DeviceDescription>DS-8632NI-I8</DeviceDescription>
        <DeviceSN>DS-8632NI-I80000000000AAAA0000000</DeviceSN>
        <CommandPort>8000</CommandPort>
        <HttpPort>80</HttpPort>
        <SDKOverTLSPort>8443</SDKOverTLSPort>
        <MAC>c0-56-e3-00-00-01</MAC>
        <IPv4Address>192.168.1.2</IPv4Address>
        <IPv4SubnetMask>255.255.255.0</IPv4SubnetMask>
        <DHCP>false</DHCP>
        <AnalogChannelNum>0</AnalogChannelNum>
        <DigitalChannelNum>32</DigitalChannelNum>
        <SoftwareVersion>V4.61.025build 220905</SoftwareVersion>
        <Activated>true</Activated>
        </ProbeMatch>
        """;

    static string Reply(string ip = "192.168.1.64", string model = "DS-2CD2043G2-I", string mac = "44-19-b6-00-00-02",
        string activated = "true", string http = "80", string? https = null, int digital = 1, string types = "inquiry") =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <ProbeMatch><Uuid>X</Uuid><Types>{types}</Types><DeviceDescription>{model}</DeviceDescription>
        <DeviceSN>{model}SN1</DeviceSN><CommandPort>8000</CommandPort><HttpPort>{http}</HttpPort>
        {(https is null ? "" : $"<HttpsPort>{https}</HttpsPort>")}<MAC>{mac}</MAC><IPv4Address>{ip}</IPv4Address>
        <AnalogChannelNum>0</AnalogChannelNum><DigitalChannelNum>{digital}</DigitalChannelNum>
        <SoftwareVersion>V5.7.3</SoftwareVersion><Activated>{activated}</Activated></ProbeMatch>
        """;

    static DiscoveredDevice? Parse(string xml) => SadpReply.Parse(Encoding.UTF8.GetBytes(xml));

    // ---- Probe: inquiry only ----

    [Fact]
    public void ProbeIsAnInquiryAndNothingElse()
    {
        var uuid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var xml = SadpProbe.Inquiry(uuid);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("Probe", root.Name.LocalName);
        Assert.Equal(["Uuid", "Types"], root.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("0F8FAD5B-D9CB-469F-A165-70867728950E", root.Element("Uuid")!.Value);
        Assert.Equal("inquiry", root.Element("Types")!.Value);
        Assert.Equal(Encoding.UTF8.GetBytes(xml), SadpProbe.InquiryBytes(uuid));
    }

    [Fact]
    public void ProbeTargetIsTheSadpGroup()
    {
        Assert.Equal("239.255.255.250", SadpProbe.MulticastAddress);
        Assert.Equal(37020, SadpProbe.Port);
        Assert.Equal("inquiry", SadpProbe.InquiryType);
    }

    [Fact]
    public void ScannerSendsNoOtherSadpType()
    {
        // Read-only rule: the probe builder is the only SADP message in the code; no write type exists anywhere in Core.
        var source = typeof(SadpProbe).Assembly;
        var publicMethods = typeof(SadpProbe).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name).Distinct().OrderBy(n => n);
        Assert.Equal(["Inquiry", "InquiryBytes"], publicMethods);
        Assert.NotNull(source.GetType("SPVision.Core.SadpScanner"));
    }

    // ---- Parser ----

    [Fact]
    public void ParsesARealRecorderReply()
    {
        var d = Parse(RecorderReply)!;
        Assert.Equal("192.168.1.2", d.Address);
        Assert.Equal("DS-8632NI-I8", d.Model);
        Assert.Equal("42514", d.DeviceTypeCode);
        Assert.Equal(80, d.HttpPort);
        Assert.Null(d.HttpsPort);
        Assert.Equal(8000, d.CommandPort);
        Assert.Equal("V4.61.025build 220905", d.SoftwareVersion);
        Assert.True(d.Activated);
        Assert.True(d.IsActivated);
        Assert.Equal("c056e3000001", d.Mac);
        Assert.Equal(32, d.DigitalChannels);
        Assert.Equal(0, d.AnalogChannels);
        Assert.True(d.IsRecorder);
        Assert.Equal("mac:c056e3000001", d.Key);
    }

    [Fact]
    public void ParsesHttpsPortAndInactiveDevice()
    {
        var d = Parse(Reply(https: "443", activated: "false"))!;
        Assert.Equal(443, d.HttpsPort);
        Assert.False(d.Activated);
        Assert.False(d.IsActivated);
    }

    [Fact]
    public void MissingActivatedFlagCountsAsActive()
    {
        var d = Parse(RecorderReply.Replace("<Activated>true</Activated>", ""))!;
        Assert.Null(d.Activated);
        Assert.True(d.IsActivated);
    }

    [Fact]
    public void ToleratesBomTrailingNulAndNamespace()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(RecorderReply)).Concat(new byte[] { 0, 0 }).ToArray();
        Assert.Equal("192.168.1.2", SadpReply.Parse(bytes)!.Address);
        var withNs = RecorderReply.Replace("<ProbeMatch>", "<ProbeMatch xmlns=\"urn:x\">");
        Assert.Equal("DS-8632NI-I8", Parse(withNs)!.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<ProbeMatch><IPv4Address>192.168.1.2</IPv4Address>")] // truncated
    [InlineData("<Probe><Uuid>1</Uuid><Types>inquiry</Types></Probe>")] // our own probe echoed back
    [InlineData("<ProbeMatch><Types>update</Types><IPv4Address>192.168.1.2</IPv4Address></ProbeMatch>")] // other SADP type
    [InlineData("<ProbeMatch><Types>inquiry</Types></ProbeMatch>")] // no address
    public void IgnoresMalformedOrForeignPackets(string xml) => Assert.Null(Parse(xml));

    [Fact]
    public void RefusesDtdAndEntityExpansion()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE ProbeMatch [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <ProbeMatch><Types>inquiry</Types><DeviceDescription>&b;</DeviceDescription><IPv4Address>192.168.1.2</IPv4Address></ProbeMatch>
            """;
        Assert.Null(Parse(bomb));
        const string external = """
            <?xml version="1.0"?>
            <!DOCTYPE ProbeMatch [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]>
            <ProbeMatch><Types>inquiry</Types><DeviceDescription>&x;</DeviceDescription><IPv4Address>192.168.1.2</IPv4Address></ProbeMatch>
            """;
        Assert.Null(Parse(external));
    }

    [Fact]
    public void RefusesOversizedDatagramAndInvalidUtf8()
    {
        var big = RecorderReply.Replace("<Salt>abc</Salt>", "<Salt>" + new string('x', SadpReply.MaxDatagramBytes) + "</Salt>");
        Assert.Null(Parse(big));
        var bad = Encoding.UTF8.GetBytes(RecorderReply);
        bad[bad.Length / 2] = 0xFF;
        Assert.Null(SadpReply.Parse(bad));
        Assert.Null(SadpReply.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.300")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("239.255.255.250")]
    [InlineData("fe80::1")]
    [InlineData("192.168.1.2x")]
    public void RejectsBadAddresses(string ip)
    {
        Assert.Null(SadpReply.Ipv4(ip));
        Assert.Null(Parse(Reply(ip: ip)));
    }

    [Fact]
    public void AcceptsAPlainAddress() => Assert.Equal("10.1.2.3", SadpReply.Ipv4("10.1.2.3"));

    [Fact]
    public void BadPortsAndCountsBecomeNull()
    {
        var d = Parse(RecorderReply.Replace("<HttpPort>80</HttpPort>", "<HttpPort>99999</HttpPort>")
            .Replace("<CommandPort>8000</CommandPort>", "<CommandPort>-1</CommandPort>")
            .Replace("<DigitalChannelNum>32</DigitalChannelNum>", "<DigitalChannelNum>lots</DigitalChannelNum>"))!;
        Assert.Null(d.HttpPort);
        Assert.Null(d.CommandPort);
        Assert.Null(d.DigitalChannels);
    }

    [Fact]
    public void FieldsAreTrimmedStrippedAndCapped()
    {
        var d = Parse(RecorderReply.Replace("DS-8632NI-I8</DeviceDescription>", "&#x9;DS-8632&#x9;NI-I8  </DeviceDescription>")
            .Replace("<SoftwareVersion>V4.61.025build 220905</SoftwareVersion>",
                "<SoftwareVersion>" + new string('V', 500) + "</SoftwareVersion>"))!;
        Assert.Equal("DS-8632NI-I8", d.Model);
        Assert.Equal(SadpReply.MaxFieldLength, d.SoftwareVersion!.Length);
    }

    [Theory]
    [InlineData("c0-56-e3-aa-bb-cc", "c056e3aabbcc")]
    [InlineData("C0:56:E3:AA:BB:CC", "c056e3aabbcc")]
    [InlineData("c056.e3aa.bbcc", "c056e3aabbcc")]
    [InlineData("00-00-00-00-00-00", null)]
    [InlineData("c0-56-e3-aa-bb", null)]
    [InlineData("zz-56-e3-aa-bb-cc", null)]
    public void NormalizesMac(string text, string? expected) => Assert.Equal(expected, SadpReply.Mac(text));

    // ---- Classifier ----

    [Theory]
    [InlineData("DS-7608NI-K2", 0, 0, true)]
    [InlineData("DS-7104NI-Q1", 0, 0, true)]
    [InlineData("DS-7732NI-K4", 32, 0, true)]
    [InlineData("DS-8632NI-I8", 32, 0, true)]
    [InlineData("DS-96128NI-I24", 0, 0, true)]
    [InlineData("iDS-7216HUHI-M2/S", 0, 16, true)]
    [InlineData("DS-7208HQHI-K1", 0, 8, true)]
    [InlineData("NVR-104MH-C/4P", 0, 0, true)]
    [InlineData("DVR-204Q-K1", 0, 0, true)]
    [InlineData("HWN-4104MH-4P", 0, 0, true)]
    [InlineData("HWD-5104", 0, 0, true)]
    [InlineData("DS-2CD2043G2-I", 1, 0, false)]
    [InlineData("DS-2CD6D44G0-IHS", 4, 0, false)] // multi-sensor camera: still a camera
    [InlineData("DS-2DE4425IW-DE", 1, 0, false)]
    [InlineData("IPC-B140H", 1, 0, false)]
    [InlineData("HWI-B121H", 1, 0, false)]
    [InlineData("DS-PWA64-L-WE", 0, 0, false)] // alarm panel (seen on the real LAN)
    [InlineData("DS-K1A802AMF", 0, 0, false)] // access terminal (seen on the real LAN)
    [InlineData("Unknown-Box", 8, 0, true)] // unknown model with channels: a recorder
    [InlineData("", 1, 0, false)]
    [InlineData(null, 0, 4, true)]
    public void ClassifiesRecorders(string? model, int digital, int analog, bool expected) =>
        Assert.Equal(expected, RecorderClassifier.IsRecorder(model, digital, analog));

    // ---- De-duplication ----

    [Fact]
    public void DeduplicatesByMacAndKeepsTheLatestReply()
    {
        var set = new DiscoveredDeviceSet();
        Assert.True(set.Add(Parse(RecorderReply)!));
        Assert.False(set.Add(Parse(RecorderReply)!)); // devices answer twice
        var moved = Parse(RecorderReply.Replace("192.168.1.2<", "192.168.1.3<"))!; // same MAC, other interface/address
        Assert.False(set.Add(moved));
        Assert.Single(set.Items);
        Assert.Equal("192.168.1.3", set.Items[0].Address);
    }

    [Fact]
    public void FallsBackToSerialThenAddress()
    {
        var noMac = Parse(Reply(mac: "bad"))!;
        Assert.StartsWith("sn:", noMac.Key);
        var noSerial = Parse(Reply(mac: "bad").Replace("<DeviceSN>DS-2CD2043G2-ISN1</DeviceSN>", ""))!;
        Assert.Equal("ip:192.168.1.64", noSerial.Key);

        var set = new DiscoveredDeviceSet();
        Assert.True(set.Add(noSerial));
        Assert.False(set.Add(noSerial with { Model = "x" }));
        Assert.True(set.Add(Parse(Reply(ip: "192.168.1.64", mac: "44-19-b6-00-00-09"))!)); // address conflict, other device
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void ItemsAreInNumericAddressOrder()
    {
        var set = new DiscoveredDeviceSet();
        set.Add(Parse(Reply(ip: "192.168.1.10", mac: "00-00-00-00-00-0a"))!);
        set.Add(Parse(Reply(ip: "192.168.1.9", mac: "00-00-00-00-00-09"))!);
        set.Add(Parse(Reply(ip: "10.0.0.5", mac: "00-00-00-00-00-05"))!);
        Assert.Equal(["10.0.0.5", "192.168.1.9", "192.168.1.10"], set.Items.Select(d => d.Address));
    }

    [Fact]
    public void FloodIsCapped()
    {
        var set = new DiscoveredDeviceSet();
        for (var i = 0; i < DiscoveredDeviceSet.MaxDevices + 50; i++)
        {
            set.Add(new DiscoveredDevice { Address = $"10.0.{i / 250}.{i % 250 + 1}" });
        }
        Assert.Equal(DiscoveredDeviceSet.MaxDevices, set.Count);
    }

    // ---- Form fill, status, texts ----

    [Fact]
    public void RealRecorderFillsHttpWithReportedPorts()
    {
        var fill = DiscoveryRules.Fill(Parse(RecorderReply)!);
        Assert.Equal(new DiscoveryFill("192.168.1.2", 80, 554, 8000, false, "DS-8632NI-I8"), fill);
    }

    [Fact]
    public void HttpsWhenReportedOrOnPort443()
    {
        var reported = DiscoveryRules.Fill(Parse(Reply(http: "80", https: "8443"))!);
        Assert.True(reported.UseHttps);
        Assert.Equal(8443, reported.HttpPort);
        var on443 = DiscoveryRules.Fill(Parse(Reply(http: "443"))!);
        Assert.True(on443.UseHttps);
        Assert.Equal(443, on443.HttpPort);
        var missing = DiscoveryRules.Fill(new DiscoveredDevice { Address = "192.168.1.5" });
        Assert.Equal(new DiscoveryFill("192.168.1.5", 80, 554, 8000, false, ""), missing);
    }

    [Fact]
    public void StatusPrefersInactiveThenAdded()
    {
        var saved = new[] { "192.168.1.2", " 192.168.1.20 " };
        Assert.Equal(DiscoveryStatus.Added, DiscoveryRules.Status(Parse(RecorderReply)!, saved));
        Assert.Equal(DiscoveryStatus.Active, DiscoveryRules.Status(Parse(Reply())!, saved));
        Assert.Equal(DiscoveryStatus.Inactive, DiscoveryRules.Status(Parse(Reply(activated: "false"))!, saved));
        Assert.Equal(DiscoveryStatus.Inactive, DiscoveryRules.Status(Parse(Reply(ip: "192.168.1.20", activated: "false"))!, saved));
        Assert.Equal(DiscoveryStatus.Added, DiscoveryRules.Status(Parse(Reply(ip: "192.168.1.20"))!, saved));
        Assert.Equal("Eklendi", DiscoveryRules.Badge(DiscoveryStatus.Added));
        Assert.Equal("Aktif", DiscoveryRules.Badge(DiscoveryStatus.Active));
        Assert.Equal("Aktif değil", DiscoveryRules.Badge(DiscoveryStatus.Inactive));
    }

    [Fact]
    public void FilterKeepsOnlyRecorders()
    {
        var all = new[] { Parse(RecorderReply)!, Parse(Reply())!, Parse(Reply(ip: "192.168.1.98", model: "DS-K1A802AMF", digital: 0))! };
        Assert.Single(DiscoveryRules.Visible(all, onlyRecorders: true));
        Assert.Equal(3, DiscoveryRules.Visible(all, onlyRecorders: false).Count);
    }

    [Fact]
    public void ColumnTexts()
    {
        var recorder = Parse(RecorderReply)!;
        Assert.Equal("HTTP 80 / 8000", DiscoveryRules.Ports(recorder));
        Assert.Equal("HTTPS 443 / 8000", DiscoveryRules.Ports(Parse(Reply(https: "443"))!));
        Assert.Equal("V4.61.025 build 220905", DiscoveryRules.Software(recorder));
        Assert.Equal("V5.4.0 build 160530", DiscoveryRules.Software(recorder with { SoftwareVersion = "V5.4.0 build 160530" }));
        Assert.Equal("—", DiscoveryRules.Software(recorder with { SoftwareVersion = null }));
        Assert.Equal("Hikvision cihazı", DiscoveryRules.Model(recorder with { Model = "" }));
        Assert.Equal("taranıyor", DiscoveryRules.CountText(true, 3));
        Assert.Equal("1 cihaz bulundu", DiscoveryRules.CountText(false, 1));
        Assert.Equal("cihaz bulunamadı", DiscoveryRules.CountText(false, 0));
        Assert.Equal("Cihaz bulunamadı — IP adresini elle girebilirsiniz", DiscoveryRules.NotFound);
    }
}
