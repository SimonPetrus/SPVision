using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

public sealed class IsapiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Minimal Hikvision ISAPI client (HTTP Digest). Replaces the HCNetSDK calls the old client made:
/// Login_V40/DEVICECFG_V40 -> deviceInfo, IPPARACFG_V40/PICCFG_V40 -> channel lists, FindFile_V50 -> search.
/// </summary>
public sealed class IsapiClient : IDisposable
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

    /// <summary>Finds recorded segments for one camera, following "MORE" pages.</summary>
    public async Task<List<RecordingSegment>> SearchRecordingsAsync(int channel, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var result = new List<RecordingSegment>();
        var searchId = Guid.NewGuid().ToString().ToUpperInvariant();
        const int pageSize = 100;
        for (var position = 0; ; position += pageSize)
        {
            var body = BuildSearchRequest(searchId, StreamUrls.TrackId(channel, StreamQuality.Main), from, to, position, pageSize);
            var (segments, more) = ParseSearchResult(await PostXmlAsync("ISAPI/ContentMgmt/search", body, ct));
            result.AddRange(segments);
            if (!more || segments.Count == 0)
            {
                break;
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
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
            throw new IsapiException($"Cihaza bağlanılamadı: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IsapiException("Cihaz zaman aşımına uğradı.");
        }
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
        var root = doc.Root!;
        var status = Value(root, "responseStatusStrg") ?? "";
        var segments = Elements(root, "searchMatchItem")
            .Select(item =>
            {
                var span = Elements(item, "timeSpan").FirstOrDefault() ?? item;
                return new RecordingSegment(
                    ParseTime(Value(span, "startTime")),
                    ParseTime(Value(span, "endTime")),
                    Value(item, "playbackURI") ?? "");
            })
            .Where(s => s.End > s.Start)
            .ToList();
        return (segments, string.Equals(status, "MORE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Recorders treat the trailing "Z" as their own local time, so no UTC conversion is done.</summary>
    internal static string FormatTime(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static DateTime ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return default;
        }
        var trimmed = s.TrimEnd('Z', 'z');
        var plus = trimmed.IndexOfAny(['+'], 10);
        if (plus > 0)
        {
            trimmed = trimmed[..plus];
        }
        return DateTime.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    static IEnumerable<XElement> Elements(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName);

    static string? Value(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    static int Int(XElement parent, string localName) =>
        int.TryParse(parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value, out var v) ? v : 0;
}
