using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

public sealed class IsapiException(string message, HttpStatusCode? status = null, bool connectionFailed = false) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>No HTTP answer at all: TCP refused/reset, TLS handshake failed or timed out (not a 401 or other status).</summary>
    public bool ConnectionFailed { get; } = connectionFailed;
}

/// <summary>
/// Minimal Hikvision ISAPI client (HTTP Digest). Replaces the HCNetSDK calls the old client made:
/// Login_V40/DEVICECFG_V40 -> deviceInfo, IPPARACFG_V40/PICCFG_V40 -> channel lists, FindFile_V50 -> search.
/// </summary>
public sealed partial class IsapiClient : IDisposable
{
    readonly HttpClient _http;

    public IsapiClient(Device device, TimeSpan? timeout = null)
    {
        var scheme = device.UseHttps ? "https" : "http";
        var baseUri = new Uri($"{scheme}://{device.Host}:{device.HttpPort}/");
        var credential = new NetworkCredential(device.UserName, device.Password);
        var handler = new SocketsHttpHandler
        {
            Credentials = new CredentialCache { { baseUri, "Digest", credential }, { baseUri, "Basic", credential } },
            PreAuthenticate = true,
        };
        if (device.UseHttps)
        {
            // Recorders ship self-signed certificates; they are only reached on the local network.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>Tests: requests go to <paramref name="handler"/> instead of the network.</summary>
    internal IsapiClient(Uri baseUri, HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The device has answered at least one request (any status) through this client.</summary>
    public bool Answered { get; private set; }

    public async Task<DeviceDetails> GetDeviceInfoAsync(CancellationToken ct = default) =>
        ParseDeviceInfo(await GetXmlAsync("ISAPI/System/deviceInfo", ct));

    /// <summary>
    /// Lists cameras. NVRs expose IP cameras under InputProxy; DVRs and standalone cameras use Video/inputs.
    /// </summary>
    public async Task<List<Camera>> GetCamerasAsync(CancellationToken ct = default)
    {
        try
        {
            var cameras = ParseInputProxyChannels(await GetXmlAsync("ISAPI/ContentMgmt/InputProxy/channels", ct));
            if (cameras.Count > 0)
            {
                try
                {
                    ApplyInputProxyStatus(cameras, await GetXmlAsync("ISAPI/ContentMgmt/InputProxy/channels/status", ct));
                }
                catch (IsapiException)
                {
                    // Status is optional; leave cameras marked online.
                }
                return cameras;
            }
        }
        catch (IsapiException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
        }
        return ParseVideoInputs(await GetXmlAsync("ISAPI/System/Video/inputs/channels", ct));
    }

    /// <summary>
    /// Hik-Connect platform settings. When stream encryption is on, the verification code is the stream key;
    /// admin accounts can read it, so the user does not have to type it.
    /// </summary>
    public async Task<StreamEncryptionInfo> GetStreamEncryptionAsync(CancellationToken ct = default)
    {
        try
        {
            return ParseStreamEncryption(await GetXmlAsync("ISAPI/System/Network/EZVIZ", ct));
        }
        catch (IsapiException e) when (e.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
        {
            return new StreamEncryptionInfo(false, null);
        }
    }

    /// <summary>Full-resolution JPEG of the main stream, served by the recorder.</summary>
    public async Task<byte[]> GetSnapshotAsync(int channel, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"ISAPI/Streaming/channels/{StreamUrls.TrackId(channel, StreamQuality.Main)}/picture"), ct);
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new IsapiException("Cihaz görüntü yerine farklı bir yanıt döndürdü.");
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Finds recorded segments for one camera, following "MORE" pages. Recorders may return fewer results per
    /// page than asked (the web UI asks for 40), so the next page starts after the results actually received.
    /// </summary>
    public async Task<List<RecordingSegment>> SearchRecordingsAsync(int channel, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var result = new List<RecordingSegment>();
        var searchId = Guid.NewGuid().ToString().ToUpperInvariant();
        const int pageSize = 100;
        const int maxPages = 100; // a day has far fewer segments; guards against a recorder that never stops saying MORE
        var position = 0;
        for (var page = 0; page < maxPages; page++)
        {
            var body = BuildSearchRequest(searchId, StreamUrls.TrackId(channel, StreamQuality.Main), from, to, position, pageSize);
            var (segments, more, received) = ParseSearchPage(await PostXmlAsync("ISAPI/ContentMgmt/search", body, ct));
            result.AddRange(segments);
            position += received;
            if (!more || received == 0)
            {
                break;
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>
    /// Whether the camera has any recording in the range: the same read-only search as
    /// <see cref="SearchRecordingsAsync"/>, asking for a single result (calendar day marks).
    /// </summary>
    public async Task<bool> HasRecordingsAsync(int channel, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var body = BuildSearchRequest(Guid.NewGuid().ToString().ToUpperInvariant(), StreamUrls.TrackId(channel, StreamQuality.Main), from, to, 0, 1);
        var (segments, _) = ParseSearchResult(await PostXmlAsync("ISAPI/ContentMgmt/search", body, ct));
        return segments.Count > 0;
    }

    async Task<XDocument> GetXmlAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await ReadXmlAsync(response, ct);
    }

    async Task<XDocument> PostXmlAsync(string path, string xml, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(xml, Encoding.UTF8, new MediaTypeHeaderValue("application/xml")),
        };
        using var response = await SendAsync(request, ct);
        return await ReadXmlAsync(response, ct);
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            // A name that does not resolve is not a TCP/TLS problem; HTTP would fail the same way.
            throw new IsapiException($"Cihaza bağlanılamadı: {e.Message}",
                connectionFailed: e.StatusCode is null && e.HttpRequestError != HttpRequestError.NameResolutionError);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IsapiException("Cihaz zaman aşımına uğradı.", connectionFailed: true);
        }
        Answered = true;
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new IsapiException(status == HttpStatusCode.Unauthorized
                ? "Kullanıcı adı veya şifre hatalı."
                : $"Cihaz {(int)status} {status} döndürdü ({request.RequestUri}).", status);
        }
        return response;
    }

    static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException e)
        {
            throw new IsapiException($"Cihazdan geçersiz XML geldi: {e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();

    // ---- XML helpers (namespace-agnostic: firmware uses hikvision.com and isapi.org namespaces) ----

    internal static DeviceDetails ParseDeviceInfo(XDocument doc)
    {
        var root = doc.Root!;
        return new DeviceDetails(
            Value(root, "deviceName") ?? "",
            Value(root, "model"),
            Value(root, "serialNumber"),
            Value(root, "firmwareVersion"),
            Value(root, "deviceType"));
    }

    internal static StreamEncryptionInfo ParseStreamEncryption(XDocument doc)
    {
        var root = doc.Root!;
        // Firmware spells it "streamEncrypteEnabled"; accept the corrected spelling too.
        var flag = Value(root, "streamEncrypteEnabled") ?? Value(root, "streamEncryptEnabled");
        var code = Value(root, "verificationCode");
        return new StreamEncryptionInfo(
            string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrEmpty(code) ? null : code);
    }

    internal static List<Camera> ParseInputProxyChannels(XDocument doc) =>
        Elements(doc.Root!, "InputProxyChannel")
            .Select(e => new Camera { Channel = Int(e, "id"), Name = Value(e, "name") ?? $"Kamera {Int(e, "id")}" })
            .Where(c => c.Channel > 0)
            .ToList();

    internal static void ApplyInputProxyStatus(List<Camera> cameras, XDocument doc)
    {
        var online = Elements(doc.Root!, "InputProxyChannelStatus")
            .ToDictionary(e => Int(e, "id"), e => string.Equals(Value(e, "online"), "true", StringComparison.OrdinalIgnoreCase));
        foreach (var camera in cameras)
        {
            if (online.TryGetValue(camera.Channel, out var isOnline))
            {
                camera.Online = isOnline;
            }
        }
    }

    internal static List<Camera> ParseVideoInputs(XDocument doc) =>
        Elements(doc.Root!, "VideoInputChannel")
            .Where(e => !string.Equals(Value(e, "videoInputEnabled"), "false", StringComparison.OrdinalIgnoreCase))
            .Select(e => new Camera { Channel = Int(e, "id"), Name = Value(e, "name") ?? $"Kamera {Int(e, "id")}" })
            .Where(c => c.Channel > 0)
            .ToList();

    internal static string BuildSearchRequest(string searchId, int trackId, DateTime from, DateTime to, int position, int maxResults)
    {
        var doc = new XElement("CMSearchDescription",
            new XElement("searchID", searchId),
            new XElement("trackList", new XElement("trackID", trackId)),
            new XElement("timeSpanList", new XElement("timeSpan",
                new XElement("startTime", FormatTime(from)),
                new XElement("endTime", FormatTime(to)))),
            new XElement("maxResults", maxResults),
            new XElement("searchResultPostion", position), // sic: ISAPI spelling
            new XElement("metadataList", new XElement("metadataDescriptor", "//recordType.meta.std-cgi.com")));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc;
    }

    internal static (List<RecordingSegment> Segments, bool More) ParseSearchResult(XDocument doc)
    {
        var (segments, more, _) = ParseSearchPage(doc);
        return (segments, more);
    }

    /// <summary>
    /// One result page: the valid segments, the MORE flag and how many match items the page held. A refusal the
    /// recorder sends with HTTP 200 (a <c>ResponseStatus</c> document, or <c>responseStatus</c> false without
    /// "NO MATCHES") throws: it must not read as "no recordings" (the calendar would keep that day without a dot).
    /// </summary>
    static (List<RecordingSegment> Segments, bool More, int Received) ParseSearchPage(XDocument doc)
    {
        var root = doc.Root!;
        var status = Value(root, "responseStatusStrg") ?? "";
        if (root.Name.LocalName == "ResponseStatus" ||
            (string.Equals(Value(root, "responseStatus"), "false", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(status, "NO MATCHES", StringComparison.OrdinalIgnoreCase)))
        {
            var reason = Value(root, "statusString") ?? Value(root, "subStatusCode") ?? (status.Length > 0 ? status : null);
            throw new IsapiException(reason is null ? "Cihaz aramayı reddetti." : $"Cihaz aramayı reddetti ({reason}).");
        }
        var items = Elements(root, "searchMatchItem").ToList();
        var segments = items
            .Select(item =>
            {
                var span = Elements(item, "timeSpan").FirstOrDefault() ?? item;
                return new RecordingSegment(
                    ParseTime(Value(span, "startTime")),
                    ParseTime(Value(span, "endTime")),
                    Value(item, "playbackURI") ?? "");
            })
            .Where(s => s.Start != default && s.End > s.Start)
            .ToList();
        return (segments, string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase), items.Count);
    }

    /// <summary>Recorders treat the trailing "Z" as their own local time, so no UTC conversion is done.</summary>
    internal static string FormatTime(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Device wall-clock time: a "Z" or "+hh:mm" / "-hh:mm" suffix is dropped (not converted). An unreadable
    /// value gives <c>default</c>, so that one bad item drops out instead of failing the whole search.
    /// </summary>
    internal static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return default;
        }
        var trimmed = s.Trim().TrimEnd('Z', 'z');
        // The offset sign comes after "yyyy-MM-ddTHH:mm:ss" (19 characters); the date's own '-' come before it.
        var offset = trimmed.Length > 19 ? trimmed.IndexOfAny(['+', '-'], 19) : -1;
        if (offset > 0)
        {
            trimmed = trimmed[..offset];
        }
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : default;
    }

    static IEnumerable<XElement> Elements(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName);

    static string? Value(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    static int Int(XElement parent, string localName) =>
        int.TryParse(parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value, out var v) ? v : 0;
}
