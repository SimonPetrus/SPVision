using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// ISAPI client hardening: Digest only (no Basic downgrade), no redirects, bounded responses, no DTDs, and no
/// credentials in error text. Real-handler cases talk to <see cref="LoopbackServer"/> on 127.0.0.1 only.
/// </summary>
public class IsapiSecurityTests
{
    const string Password = "s3cret-Pw";
    const string DeviceInfoXml = "<DeviceInfo><deviceName>NVR</deviceName><model>DS-8632NI-I8</model></DeviceInfo>";

    static Device Loopback(LoopbackServer server) =>
        new() { Host = "127.0.0.1", HttpPort = server.Port, UserName = "admin", Password = Password };

    // ---- 1. Digest only ----

    [Fact]
    public async Task BasicOnlyChallengeGetsNoPassword()
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Answer(401, "Unauthorized", "", "WWW-Authenticate: Basic realm=\"nvr\""));
        using var client = new IsapiClient(Loopback(server));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetDeviceInfoAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.False(error.ConnectionFailed); // the device answered: no "HTTP ile dene" question
        Assert.Contains("Basic", error.Message);
        Assert.DoesNotContain(Password, error.Message);
        var request = Assert.Single(server.Requests); // no retry with credentials
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task BasicOnlyChallengeOnWriteGetsNoPassword()
    {
        using var server = new LoopbackServer(_ => LoopbackServer.Answer(401, "Unauthorized", "", "WWW-Authenticate: Basic realm=\"nvr\""));
        using var client = new IsapiClient(Loopback(server));

        var error = await Assert.ThrowsAsync<IsapiException>(() =>
            client.PutConfigAsync(IsapiPaths.Motion(1), XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>")));

        Assert.Equal(HttpStatusCode.Unauthorized, error.Status);
        Assert.All(server.Requests, r => Assert.False(r.Headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task DigestChallengeIsAnsweredWithDigest()
    {
        // Control for the test above: the credentials are there, only never as Basic.
        using var server = new LoopbackServer(r => r.Headers.ContainsKey("Authorization")
            ? LoopbackServer.Answer(200, "OK", DeviceInfoXml, "Content-Type: application/xml")
            : LoopbackServer.Answer(401, "Unauthorized", "",
                "WWW-Authenticate: Digest realm=\"nvr\", nonce=\"6b0d1d2e\", qop=\"auth\"",
                "WWW-Authenticate: Basic realm=\"nvr\""));
        using var client = new IsapiClient(Loopback(server));

        Assert.Equal("NVR", (await client.GetDeviceInfoAsync()).Name);

        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests[0].Headers.ContainsKey("Authorization")); // no pre-authentication
        Assert.StartsWith("Digest ", server.Requests[1].Headers["Authorization"]);
        Assert.DoesNotContain(Password, server.Requests[1].Headers["Authorization"]);
    }

    // ---- 3. Redirects ----

    [Fact]
    public async Task RedirectedWriteIsNotFollowed()
    {
        // The 307 body even claims success: it must not count, and the PUT must not be replayed elsewhere.
        using var server = new LoopbackServer(_ => LoopbackServer.Answer(307, "Temporary Redirect",
            "<ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString></ResponseStatus>",
            "Location: /ISAPI/Security/users/1"));
        using var client = new IsapiClient(Loopback(server));

        var error = await Assert.ThrowsAsync<IsapiException>(() =>
            client.PutConfigAsync(IsapiPaths.Motion(1), XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>")));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.Status);
        Assert.Contains("yönlendir", error.Message);
        var request = Assert.Single(server.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/" + IsapiPaths.Motion(1), request.Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RedirectIsAFailure(HttpStatusCode status)
    {
        var handler = new Fixed(() => new HttpResponseMessage(status)
        {
            Headers = { Location = new Uri("http://elsewhere.test/ISAPI/Security/users") },
            Content = new StringContent("<ResponseStatus><statusCode>1</statusCode></ResponseStatus>"),
        });
        using var client = new IsapiClient(new Uri("http://recorder.test/"), handler);

        var put = await Assert.ThrowsAsync<IsapiException>(() =>
            client.PutConfigAsync(IsapiPaths.Motion(1), XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>")));
        var get = await Assert.ThrowsAsync<IsapiException>(() => client.GetDeviceInfoAsync());

        Assert.Equal(status, put.Status);
        Assert.Equal(status, get.Status);
        Assert.Equal(2, handler.Calls);
    }

    // ---- 4. Response size caps ----

    [Fact]
    public async Task OverLongXmlIsRefused()
    {
        var huge = "<DeviceInfo><deviceName>" + new string('a', IsapiXml.MaxLength) + "</deviceName></DeviceInfo>";
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new Fixed(() => Ok(huge)));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetDeviceInfoAsync());

        Assert.Contains("çok büyük", error.Message);
    }

    [Fact]
    public async Task OverLongXmlWithoutLengthIsRefused()
    {
        // Chunked (no Content-Length): the decoded text is checked as well.
        var huge = "<DeviceInfo><deviceName>" + new string('a', IsapiXml.MaxLength) + "</deviceName></DeviceInfo>";
        using var client = new IsapiClient(new Uri("http://recorder.test/"),
            new Fixed(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLength(Encoding.UTF8.GetBytes(huge)) }));

        await Assert.ThrowsAsync<IsapiException>(() => client.GetDeviceInfoAsync());
    }

    [Fact]
    public async Task OverLongSnapshotIsRefused()
    {
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new Fixed(() => Jpeg(new byte[IsapiClient.MaxResponseLength + 1])));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetSnapshotAsync(1));

        Assert.Contains("çok büyük", error.Message);
        Assert.False(error.ConnectionFailed);
    }

    [Fact]
    public async Task OverLongStreamedSnapshotIsRefused()
    {
        using var client = new IsapiClient(new Uri("http://recorder.test/"),
            new Fixed(() => Jpeg(new byte[IsapiClient.MaxResponseLength + 1], knownLength: false)));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetSnapshotAsync(1));

        Assert.Contains("çok büyük", error.Message);
    }

    [Fact]
    public async Task NormalSnapshotIsRead()
    {
        var jpeg = new byte[2 * 1024 * 1024];
        jpeg[0] = 0xFF;
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new Fixed(() => Jpeg(jpeg, knownLength: false)));

        Assert.Equal(jpeg.Length, (await client.GetSnapshotAsync(1)).Length);
    }

    // ---- 5. XML parsing ----

    const string EntityBomb = """
        <?xml version="1.0"?>
        <!DOCTYPE DeviceInfo [
          <!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
          <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
          <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
        ]>
        <DeviceInfo><deviceName>&c;</deviceName></DeviceInfo>
        """;

    const string ExternalEntity = """
        <?xml version="1.0"?>
        <!DOCTYPE DeviceInfo [ <!ENTITY x SYSTEM "file:///C:/Windows/win.ini"> ]>
        <DeviceInfo><deviceName>&x;</deviceName></DeviceInfo>
        """;

    [Theory]
    [InlineData(EntityBomb)]
    [InlineData(ExternalEntity)]
    public void DtdIsRefused(string xml)
    {
        Assert.Throws<System.Xml.XmlException>(() => IsapiXml.Parse(xml));
    }

    [Theory]
    [InlineData(EntityBomb)]
    [InlineData(ExternalEntity)]
    public async Task DtdFromDeviceIsInvalidXml(string xml)
    {
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new Fixed(() => Ok(xml)));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetDeviceInfoAsync());

        Assert.Contains("geçersiz XML", error.Message);
    }

    [Fact]
    public void SafeParserReadsLikeXDocumentParse()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <MotionDetection version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <enabled>true</enabled>
              <MotionDetectionLayout>
                <sensitivityLevel> 60 </sensitivityLevel>
                <!-- comment -->
              </MotionDetectionLayout>
              <name>Kapı &amp; bahçe</name>
            </MotionDetection>
            """;

        var safe = IsapiXml.Parse(xml);
        var plain = XDocument.Parse(xml);

        Assert.Equal(plain.ToString(SaveOptions.DisableFormatting), safe.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(plain.Declaration?.ToString(), safe.Declaration?.ToString());
    }

    // ---- 7. Credentials in error text ----

    [Fact]
    public void RedactsCredentialsInAnyUrl()
    {
        var device = new Device { Host = "192.168.1.2", UserName = "admin", Password = "p@ss:w/rd!" };
        var url = StreamUrls.Playback(device, 3, new DateTime(2026, 10, 21, 0, 0, 0), new DateTime(2026, 10, 21, 23, 59, 59));
        var error = $"Opening '{url}' failed: Invalid data found when processing input";

        var redacted = StreamUrls.RedactCredentials(error);

        Assert.DoesNotContain(Uri.EscapeDataString(device.Password), redacted);
        Assert.DoesNotContain("admin", redacted);
        Assert.Contains("rtsp://***@192.168.1.2:554/Streaming/tracks/301?starttime=20261021T000000Z", redacted);
        Assert.EndsWith("failed: Invalid data found when processing input", redacted);
    }

    [Theory]
    [InlineData("rtsp://admin:pa@ss@10.0.0.5:554/Streaming/Channels/101 timeout", "rtsp://***@10.0.0.5:554/Streaming/Channels/101 timeout")]
    [InlineData("a RTSP://u:p@h/x and http://v:q@h2:80/y", "a RTSP://***@h/x and http://***@h2:80/y")]
    [InlineData("rtsp://10.0.0.5:554/Streaming/Channels/101 (no credentials)", "rtsp://10.0.0.5:554/Streaming/Channels/101 (no credentials)")]
    [InlineData("mail admin@example.com", "mail admin@example.com")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void RedactsOnlyUserInfo(string? text, string expected)
    {
        Assert.Equal(expected, StreamUrls.RedactCredentials(text));
    }

    // ---- helpers ----

    static HttpResponseMessage Ok(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml) };

    static HttpResponseMessage Jpeg(byte[] data, bool knownLength = true)
    {
        HttpContent content = knownLength ? new ByteArrayContent(data) : new UnknownLength(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Answers every request with a new copy of the same response (no network).</summary>
    sealed class Fixed(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    /// <summary>A body without Content-Length (like a chunked answer): only reading it shows how long it is.</summary>
    sealed class UnknownLength(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(data).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 (never the network) so that the real <see cref="SocketsHttpHandler"/>
/// settings of <see cref="IsapiClient"/> are exercised. Records each request and answers it with a raw response.
/// </summary>
sealed class LoopbackServer : IDisposable
{
    public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body);

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Func<Request, string> _answer;
    readonly List<Request> _requests = [];

    public LoopbackServer(Func<Request, string> answer)
    {
        _answer = answer;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<Request> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static string Answer(int status, string reason, string body, params string[] headers) =>
        $"HTTP/1.1 {status} {reason}\r\n" + string.Concat(headers.Select(h => h + "\r\n")) +
        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                // Latin-1: one char per byte, so Content-Length counts work on the reader.
                using var reader = new StreamReader(stream, Encoding.Latin1, false, 4096, leaveOpen: true);
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    var parts = line.Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
                    {
                        var colon = header.IndexOf(':');
                        headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
                    }
                    var body = "";
                    if (headers.TryGetValue("Content-Length", out var length) && int.Parse(length) is > 0 and var n)
                    {
                        var buffer = new char[n];
                        await reader.ReadBlockAsync(buffer, _stop.Token);
                        body = new string(buffer);
                    }
                    var request = new Request(parts[0], parts[1], headers, body);
                    lock (_requests)
                    {
                        _requests.Add(request);
                    }
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(_answer(request)), _stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
