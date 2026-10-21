using System.Net;
using System.Text;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Ağ › Portlar: the single risky adminAccesses entry (exact PUT path, token only), the strict structural body rule
/// (only portNo of HTTP / HTTPS / RTSP / DEV_MANAGE may differ from the document just read), PortPlan validation,
/// warnings and which SPVision ports follow. Document shape as read from the DS-8632NI-I8. No network.
/// </summary>
public class AdminPortsTests
{
    const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    static XDocument Doc() => IsapiXml.Parse($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <AdminAccessProtocolList version="2.0" xmlns="{Ns}">
          <AdminAccessProtocol><id>1</id><enabled>true</enabled><protocol>HTTP</protocol><portNo>80</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>2</id><enabled>true</enabled><protocol>RTSP</protocol><portNo>554</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>3</id><enabled>true</enabled><protocol>HTTPS</protocol><redirectToHttps>false</redirectToHttps><portNo>443</portNo><TLS1_1Enable>false</TLS1_1Enable><TLS1_2Enable>true</TLS1_2Enable></AdminAccessProtocol>
          <AdminAccessProtocol><id>4</id><enabled>true</enabled><protocol>DEV_MANAGE</protocol><portNo>8000</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>5</id><enabled>true</enabled><protocol>WebSocket</protocol><portNo>7681</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>6</id><enabled>true</enabled><protocol>IOT</protocol><portNo>30999</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>7</id><enabled>true</enabled><protocol>SDK_OVER_TLS</protocol><streamOverTls>false</streamOverTls><portNo>8443</portNo></AdminAccessProtocol>
        </AdminAccessProtocolList>
        """);

    static XDocument Capabilities() => IsapiXml.Parse($"""
        <AdminAccessProtocolList version="2.0" xmlns="{Ns}">
          <AdminAccessProtocol><id>1</id><protocol opt="HTTP">HTTP</protocol><portNo min="1" max="65535">80</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>2</id><protocol opt="RTSP">RTSP</protocol><portNo min="1024" max="65535" def="554">554</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>3</id><protocol opt="HTTPS">HTTPS</protocol><portNo min="1" max="65535">443</portNo></AdminAccessProtocol>
          <AdminAccessProtocol><id>4</id><protocol opt="DEV_MANAGE">DEV_MANAGE</protocol><portNo min="2000" max="65535" default="8000">8000</portNo></AdminAccessProtocol>
        </AdminAccessProtocolList>
        """);

    static XElement Entry(XDocument doc, string protocol) =>
        doc.Root!.Elements().Single(e => IsapiXml.Get(e, "protocol") == protocol);

    static XDocument With(Action<XDocument> change)
    {
        var doc = Doc();
        change(doc);
        return doc;
    }

    static void SetPort(XDocument doc, string protocol, string port) => IsapiXml.Set(Entry(doc, protocol), "portNo", port);

    // ================================================================ path: one exact risky entry

    [Fact]
    public void ExactPutIsTheOnlyAllowedPairForAdminAccesses()
    {
        Assert.Null(IsapiWritePolicy.CheckRisky(RiskyOperation.AdminPorts, "PUT", "ISAPI/Security/adminAccesses"));
        var matches = Enum.GetValues<RiskyOperation>()
            .SelectMany(o => new[] { "PUT", "POST", "DELETE", "GET", "PATCH", "put" }.Select(m => (o, m)))
            .Count(p => IsapiWritePolicy.CheckRisky(p.o, p.m, "ISAPI/Security/adminAccesses") is null);
        Assert.Equal(1, matches);
    }

    [Theory]
    [InlineData("ISAPI/Security/adminAccesses/")]
    [InlineData("/ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/Security/AdminAccesses")]
    [InlineData("ISAPI/security/adminAccesses")]
    [InlineData("ISAPI/SECURITY/ADMINACCESSES")]
    [InlineData("ISAPI/Security/adminaccesses")]
    [InlineData("ISAPI/Security/adminAccesses/1")]
    [InlineData("ISAPI/Security/adminAccesses/capabilities")]
    [InlineData("ISAPI/Security/adminAccess")]
    [InlineData("ISAPI/Security%2FadminAccesses")]
    [InlineData("ISAPI/Security/adminAccesses%3F")]
    [InlineData("ISAPI/Security/adminAccesses?format=json")]
    [InlineData("ISAPI/Security/adminAccesses#x")]
    [InlineData("ISAPI//Security/adminAccesses")]
    [InlineData("ISAPI/Security/adminAccesses/../users")]
    [InlineData("ISAPI\\Security\\adminAccesses")]
    [InlineData(" ISAPI/Security/adminAccesses")]
    [InlineData("http://192.0.2.1/ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/Security/adminAccesses/TLS")]
    public void DeniesEveryVariantOfThePath(string path)
    {
        foreach (var operation in Enum.GetValues<RiskyOperation>())
        {
            foreach (var method in new[] { "GET", "PUT", "POST", "DELETE" })
            {
                Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, path));
            }
        }
        Assert.NotNull(IsapiWritePolicy.Check(path));
    }

    [Theory]
    [InlineData("ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/Security/TLS")]
    [InlineData("ISAPI/Security/serverCertificate/certificate")]
    [InlineData("ISAPI/Security/users")]
    [InlineData("ISAPI/Security/illegalLoginLock")]
    public void TheOrdinaryRouteStillRefusesSecurityPaths(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureAllowed(path));
    }

    [Theory]
    [InlineData("ISAPI/Security/TLS")]
    [InlineData("ISAPI/Security/serverCertificate/certificate")]
    [InlineData("ISAPI/Security/SRTP")]
    [InlineData("ISAPI/Security/illegalLoginLock")]
    [InlineData("ISAPI/System/Network/EZVIZ")]
    [InlineData("ISAPI/Streaming/encryption")]
    public void TheExemptionDoesNotOpenOtherSecurityPaths(string path)
    {
        Assert.NotNull(IsapiWritePolicy.CheckRisky(RiskyOperation.AdminPorts, "PUT", path));
    }

    [Fact]
    public void ExemptionListIsExactlyOneEntry()
    {
        var entry = Assert.Single(IsapiWritePolicy.KeywordExemptions);
        Assert.Equal((RiskyOperation.AdminPorts, "PUT", "ISAPI/Security/adminAccesses", "adminAccess"), entry);
        Assert.Equal(["HTTP", "HTTPS", "RTSP", "DEV_MANAGE"], IsapiWritePolicy.EditablePortProtocols);
    }

    // ================================================================ body: strict structural diff

    [Theory]
    [InlineData("HTTP", "8080")]
    [InlineData("HTTPS", "8444")]
    [InlineData("RTSP", "10554")]
    [InlineData("DEV_MANAGE", "8001")]
    public void AllowsAPortOnlyChange(string protocol, string port)
    {
        var body = With(d => SetPort(d, protocol, port));
        Assert.Null(IsapiWritePolicy.CheckAdminPortsBody(Doc(), body));
        IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.AdminPorts, body, Doc());
    }

    [Fact]
    public void AllowsSwappingHttpAndHttpsPorts()
    {
        var body = With(d =>
        {
            SetPort(d, "HTTP", "443");
            SetPort(d, "HTTPS", "80");
        });
        Assert.Null(IsapiWritePolicy.CheckAdminPortsBody(Doc(), body));
    }

    public static TheoryData<string> RefusedBodyNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in RefusedBodies.Keys)
        {
            data.Add(name);
        }
        return data;
    }

    static readonly Dictionary<string, Action<XDocument>> RefusedBodies = new()
    {
        { "TLS 1.2 off", d => IsapiXml.Set(Entry(d, "HTTPS"), "TLS1_2Enable", "false") },
        { "TLS 1.1 on", d => IsapiXml.Set(Entry(d, "HTTPS"), "TLS1_1Enable", "true") },
        { "redirect to HTTPS", d => IsapiXml.Set(Entry(d, "HTTPS"), "redirectToHttps", "true") },
        { "HTTP disabled", d => IsapiXml.Set(Entry(d, "HTTP"), "enabled", "false") },
        { "SDK over TLS stream", d => IsapiXml.Set(Entry(d, "SDK_OVER_TLS"), "streamOverTls", "true") },
        { "WebSocket port", d => SetPort(d, "WebSocket", "7682") },
        { "IOT port", d => SetPort(d, "IOT", "31000") },
        { "SDK over TLS port", d => SetPort(d, "SDK_OVER_TLS", "8444") },
        { "protocol renamed", d => IsapiXml.Set(Entry(d, "HTTP"), "protocol", "HTTPS") },
        { "id changed", d => IsapiXml.Set(Entry(d, "HTTP"), "id", "9") },
        { "added protocol", d => d.Root!.Add(new XElement(XName.Get("AdminAccessProtocol", Ns),
            new XElement(XName.Get("id", Ns), "8"), new XElement(XName.Get("enabled", Ns), "true"),
            new XElement(XName.Get("protocol", Ns), "SSH"), new XElement(XName.Get("portNo", Ns), "22"))) },
        { "removed protocol", d => Entry(d, "IOT").Remove() },
        { "entries reordered", d =>
            {
                var first = Entry(d, "HTTP");
                first.Remove();
                d.Root!.Add(first);
            } },
        { "children reordered", d =>
            {
                var port = Entry(d, "HTTP").Elements().Single(e => e.Name.LocalName == "portNo");
                port.Remove();
                Entry(d, "HTTP").AddFirst(port);
            } },
        { "added element", d => Entry(d, "HTTPS").Add(new XElement(XName.Get("TLS1_3Enable", Ns), "true")) },
        { "added certificate", d => Entry(d, "HTTPS").Add(new XElement(XName.Get("certificateName", Ns), "x")) },
        { "version attribute", d => d.Root!.SetAttributeValue("version", "1.0") },
        { "new attribute", d => Entry(d, "HTTP").SetAttributeValue("x", "1") },
        { "port zero", d => SetPort(d, "HTTP", "0") },
        { "port too big", d => SetPort(d, "HTTP", "65536") },
        { "leading zero", d => SetPort(d, "HTTP", "08080") },
        { "spaces", d => SetPort(d, "HTTP", " 8080") },
        { "not a number", d => SetPort(d, "HTTP", "http") },
        { "negative", d => SetPort(d, "HTTP", "-1") },
        { "clash with RTSP", d => SetPort(d, "HTTP", "554") },
        { "clash with WebSocket", d => SetPort(d, "HTTPS", "7681") },
        { "clash with SDK over TLS", d => SetPort(d, "HTTPS", "8443") },
        { "no change", _ => { } },
        { "password element", d => Entry(d, "HTTP").Add(new XElement(XName.Get("password", Ns), "x")) },
    };

    [Theory]
    [MemberData(nameof(RefusedBodyNames))]
    public void RefusesEveryOtherDifference(string why)
    {
        var body = With(RefusedBodies[why]);
        Assert.True(IsapiWritePolicy.CheckAdminPortsBody(Doc(), body) is not null, why);
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.AdminPorts, body, Doc()));
    }

    [Fact]
    public void RefusesANamespaceChangeAndAForeignDocument()
    {
        var foreign = IsapiXml.Parse(Doc().ToString().Replace(Ns, "http://example.com/x", StringComparison.Ordinal));
        SetPort(foreign, "HTTP", "8080");
        Assert.NotNull(IsapiWritePolicy.CheckAdminPortsBody(Doc(), foreign));
        Assert.NotNull(IsapiWritePolicy.CheckAdminPortsBody(XDocument.Parse("<User><id>1</id></User>"), XDocument.Parse("<User><id>2</id></User>")));
    }

    [Fact]
    public void ABodyWithoutTheDocumentJustReadIsRefused()
    {
        var body = With(d => SetPort(d, "HTTP", "8080"));
        Assert.NotNull(IsapiWritePolicy.CheckAdminPortsBody(null, body));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.AdminPorts, body));
    }

    [Fact]
    public void DuplicateProtocolEntriesCannotBeChanged()
    {
        var original = With(d => d.Root!.Add(new XElement(Entry(d, "HTTP"))));
        var body = new XDocument(original);
        IsapiXml.Set(body.Root!.Elements().First(), "portNo", "8080");
        Assert.NotNull(IsapiWritePolicy.CheckAdminPortsBody(original, body));
    }

    // ================================================================ request + token

    static readonly PortChange HttpsTo8444 = new("HTTPS", 443, 8444);

    sealed class Answer(string? typed) : IRiskyConfirmation
    {
        public RiskyWriteRequest? Shown { get; private set; }

        public string? Ask(RiskyWriteRequest request)
        {
            Shown = request;
            return typed;
        }
    }

    [Fact]
    public void RequestListsOldAndNewPortsAndWarnsAboutOtherClients()
    {
        var request = RiskyWrites.AdminPorts(Doc(), [HttpsTo8444, new PortChange("DEV_MANAGE", 8000, 8001)], usedBySpvision: true);

        Assert.Equal(RiskyOperation.AdminPorts, request.Operation);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("ISAPI/Security/adminAccesses", request.Path);
        Assert.Equal(["Port · HTTPS: 443 → 8444", "Port · Sunucu (SDK): 8000 → 8001"], request.Changes);
        foreach (var client in new[] { "SPVision", "web arayüzü", "iVMS-4200", "telefon", "port yönlendirme" })
        {
            Assert.Contains(client, request.Warning, StringComparison.Ordinal);
        }
        Assert.True(request.IsUnchanged(Doc()));
        Assert.False(request.IsUnchanged(With(d => SetPort(d, "RTSP", "555"))));
    }

    [Fact]
    public void ABuilderCannotSmuggleOtherChanges()
    {
        Assert.Throws<IsapiWriteDeniedException>(() => RiskyWrites.AdminPorts(Doc(), [new PortChange("WebSocket", 7681, 7682)], false));
        Assert.Throws<IsapiWriteDeniedException>(() => RiskyWrites.AdminPorts(Doc(), [new PortChange("HTTP", 80, 554)], false));
        Assert.Throws<ConfigEditException>(() => RiskyWrites.AdminPorts(Doc(), [new PortChange("HTTP", 81, 8080)], false)); // device changed
        Assert.Throws<ConfigEditException>(() => RiskyWrites.AdminPorts(Doc(), [], false));
    }

    [Fact]
    public async Task TokenSendsExactlyTheReadDocumentWithTheNewPortOnce()
    {
        var handler = new Recorder();
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var token = ConfirmedRiskyWrite.Confirm(RiskyWrites.AdminPorts(Doc(), [HttpsTo8444], false), new Answer(""))!;

        var status = await client.SendRiskyAsync(token);

        Assert.True(status.IsOk);
        var (method, path, body) = Assert.Single(handler.Requests);
        Assert.Equal("PUT", method);
        Assert.Equal("/ISAPI/Security/adminAccesses", path);
        var expected = With(d => SetPort(d, "HTTPS", "8444"));
        Assert.True(XNode.DeepEquals(expected.Root, IsapiXml.Parse(body!).Root));

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void CancelGivesNoToken()
    {
        Assert.Null(ConfirmedRiskyWrite.Confirm(RiskyWrites.AdminPorts(Doc(), [HttpsTo8444], false), new Answer(null)));
    }

    [Fact]
    public async Task OrdinaryPutCannotReachAdminAccesses()
    {
        var handler = new Recorder();
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.PutConfigAsync("ISAPI/Security/adminAccesses", With(d => SetPort(d, "HTTP", "8080"))));
        Assert.Empty(handler.Requests);
    }

    // ================================================================ PortPlan

    [Fact]
    public void ParsesLimitsAndDefaults()
    {
        var limits = PortPlan.ParseLimits(Capabilities());
        Assert.Equal(new PortLimit(1024, 65535, 554), limits["RTSP"]);
        Assert.Equal(new PortLimit(2000, 65535, 8000), limits["DEV_MANAGE"]);
        Assert.Equal(new PortLimit(1, 65535), limits["HTTP"]);
        Assert.Empty(PortPlan.ParseLimits(null));
    }

    [Theory]
    [InlineData("8080", 8080)]
    [InlineData(" 8080 ", 8080)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("0", null)]
    [InlineData("65536", null)]
    [InlineData("08080", null)]
    [InlineData("80a", null)]
    [InlineData("", null)]
    [InlineData("-80", null)]
    [InlineData("８０", null)] // full-width digits
    public void ParsesPortBoxes(string text, int? port)
    {
        Assert.Equal(port, PortPlan.ParsePort(text));
    }

    [Fact]
    public void ChangesListOnlyDifferentPorts()
    {
        var before = new PortSettings(80, 443, 554, 8000);
        Assert.Empty(PortPlan.Changes(before, before));
        Assert.Equal([new PortChange("HTTPS", 443, 8444), new PortChange("DEV_MANAGE", 8000, 8001)],
            PortPlan.Changes(before, before with { Https = 8444, Server = 8001 }));
        Assert.Empty(PortPlan.Changes(before with { Rtsp = null }, before with { Rtsp = 600 })); // not reported: never changed
    }

    [Fact]
    public void ValidatesRangesUniquenessAndDefaults()
    {
        var limits = PortPlan.ParseLimits(Capabilities());
        Assert.Null(PortPlan.Validate(Doc(), [HttpsTo8444], limits));
        Assert.NotNull(PortPlan.Validate(Doc(), [new PortChange("DEV_MANAGE", 8000, 1999)], limits));  // device min 2000
        Assert.NotNull(PortPlan.Validate(Doc(), [new PortChange("RTSP", 554, 555)], limits));          // device min 1024
        Assert.Null(PortPlan.Validate(With(d => SetPort(d, "RTSP", "10554")), [new PortChange("RTSP", 10554, 554)], limits)); // undo to the default
        Assert.NotNull(PortPlan.Validate(Doc(), [new PortChange("HTTP", 80, 0)]));
        Assert.NotNull(PortPlan.Validate(Doc(), [new PortChange("HTTP", 80, 70000)]));
        Assert.Contains("RTSP", PortPlan.Validate(Doc(), [new PortChange("HTTP", 80, 554)])!, StringComparison.Ordinal);
        Assert.Contains("SDK_OVER_TLS", PortPlan.Validate(Doc(), [new PortChange("HTTPS", 443, 8443)])!, StringComparison.Ordinal);
        Assert.NotNull(PortPlan.Validate(Doc(), [new PortChange("HTTP", 80, 8080), new PortChange("HTTPS", 443, 8080)]));
        Assert.Null(PortPlan.Validate(Doc(), [new PortChange("HTTP", 80, 443), new PortChange("HTTPS", 443, 80)])); // swap
    }

    [Fact]
    public void WarnsAboutCommonConflictingPorts()
    {
        Assert.Contains("SSH", Assert.Single(PortPlan.Warnings([new PortChange("HTTP", 80, 22)])), StringComparison.Ordinal);
        Assert.Contains("Uzak Masaüstü", Assert.Single(PortPlan.Warnings([new PortChange("DEV_MANAGE", 8000, 3389)])), StringComparison.Ordinal);
        Assert.Contains("HTTPS", Assert.Single(PortPlan.Warnings([new PortChange("HTTP", 80, 443)])), StringComparison.Ordinal);
        Assert.Single(PortPlan.Warnings([new PortChange("HTTP", 80, 999)]));
        Assert.Empty(PortPlan.Warnings([new PortChange("HTTPS", 443, 8444), new PortChange("RTSP", 10554, 554)]));
        var request = RiskyWrites.AdminPorts(Doc(), [new PortChange("HTTP", 80, 22)], false);
        Assert.Contains("SSH", request.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyChangesOnlyPortNo()
    {
        var body = PortPlan.Apply(Doc(), [HttpsTo8444]);
        Assert.True(XNode.DeepEquals(With(d => SetPort(d, "HTTPS", "8444")).Root, body.Root));
        Assert.Equal("443", IsapiXml.Get(Entry(Doc(), "HTTPS"), "portNo")); // the read document is not modified
    }

    [Fact]
    public void SpvisionPortsFollowOnlyWhatItUses()
    {
        var used = new DevicePorts(443, 554, 8000);
        Assert.Equal(new DevicePorts(8444, 554, 8000), PortPlan.FollowDevice(true, used, [HttpsTo8444]));
        Assert.Null(PortPlan.FollowDevice(true, used, [new PortChange("HTTP", 80, 8080)]));              // SPVision uses HTTPS
        Assert.Equal(new DevicePorts(8080, 554, 8000), PortPlan.FollowDevice(false, used with { Http = 80 }, [new PortChange("HTTP", 80, 8080)]) );
        Assert.Equal(new DevicePorts(443, 10554, 8001),
            PortPlan.FollowDevice(true, used, [new PortChange("RTSP", 554, 10554), new PortChange("DEV_MANAGE", 8000, 8001)]));
        Assert.Null(PortPlan.FollowDevice(true, used with { Sdk = 9000 }, [new PortChange("DEV_MANAGE", 8000, 8001)])); // custom port
        Assert.Equal(["HTTPS portu: 443 → 8444"], PortPlan.DeviceChangeLines(true, used, new DevicePorts(8444, 554, 8000)));
    }

    // ---- in-memory handler ----

    sealed class Recorder : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString><subStatusCode>ok</subStatusCode></ResponseStatus>",
                    Encoding.UTF8, "application/xml"),
            };
        }
    }
}
