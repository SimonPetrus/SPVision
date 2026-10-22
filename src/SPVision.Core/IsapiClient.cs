using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>An ISAPI request failed. <see cref="CertificateChangedException"/> is the HTTPS certificate pin refusing a device.</summary>
public class IsapiException(string message, HttpStatusCode? status = null, bool connectionFailed = false) : Exception(message)
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
    /// <summary>Largest response read at all (snapshot JPEGs); a bigger answer fails instead of filling memory.</summary>
    internal const int MaxResponseLength = 16 * 1024 * 1024;

    readonly HttpClient _http;

    /// <summary>HTTPS only: the device whose certificate pin is checked, and who decides on a changed certificate.</summary>
    readonly Device? _pinned;
    readonly CertificateTrust? _trust;

    /// <summary>Certificate seen while the device had no pin yet (stored after the first answer).</summary>
    CertificatePin? _firstUse;

    /// <summary>Certificate refused by <see cref="ValidateCertificate"/> (not the pinned key).</summary>
    CertificatePin? _mismatch;

    public IsapiClient(Device device, TimeSpan? timeout = null) : this(device, timeout, CertificateTrust.Shared)
    {
    }

    /// <summary>Tests: a private <see cref="CertificateTrust"/> instead of the app's.</summary>
    internal IsapiClient(Device device, TimeSpan? timeout, CertificateTrust trust)
    {
        var scheme = device.UseHttps ? "https" : "http";
        var baseUri = new Uri($"{scheme}://{device.Host}:{device.HttpPort}/");
        var credential = new NetworkCredential(device.UserName, device.Password);
        var handler = new SocketsHttpHandler
        {
            // Digest only: a Basic entry hands the password in clear text to whoever answers 401 Basic (a man in the
            // middle on the LAN). Pre-authentication is Basic-only in .NET, so it stays off.
            Credentials = new CredentialCache { { baseUri, "Digest", credential } },
            PreAuthenticate = false,
            // A 3xx would replay the method, body and credentials to another path, past IsapiWritePolicy.
            AllowAutoRedirect = false,
        };
        if (device.UseHttps)
        {
            // Recorders ship self-signed certificates (no CA, no host name to check), so the certificate is pinned
            // instead: trust on first use, then only the same public key (CertificateTrust, §8).
            _pinned = device;
            _trust = trust;
            handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        }
        _http = CreateHttp(handler, baseUri, timeout ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>Tests: requests go to <paramref name="handler"/> instead of the network.</summary>
    internal IsapiClient(Uri baseUri, HttpMessageHandler handler) =>
        _http = CreateHttp(handler, baseUri, TimeSpan.FromSeconds(10));

    static HttpClient CreateHttp(HttpMessageHandler handler, Uri baseUri, TimeSpan timeout) =>
        new(handler) { BaseAddress = baseUri, Timeout = timeout, MaxResponseContentBufferSize = MaxResponseLength };

    /// <summary>The device has answered at least one request (any status) through this client.</summary>
    public bool Answered { get; private set; }

    /// <summary>
    /// HTTPS: a request failed because the device's certificate is not the pinned one and was not trusted
    /// (<see cref="CertificateChangedException"/>). Lets pages that turn read errors into "okunamadı" say why.
    /// </summary>
    public bool CertificateRefused { get; private set; }

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
        return await response.Content.ReadAsByteArrayAsync(ct); // buffered by the client, at most MaxResponseLength
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

    /// <summary>
    /// Days of <paramref name="month"/> the camera recorded on: the read-only month query the recorder's own web
    /// UI uses for its calendar (POST with search semantics, like ContentMgmt/search; never a write, never through
    /// <see cref="IsapiWritePolicy"/>). One request per camera and month. Null when the recorder does not support
    /// it (<see cref="MonthQueryUnsupported"/> status, or a body that is not a day list): ask day by day instead.
    /// Other failures (timeout, busy, 401…) throw <see cref="IsapiException"/>.
    /// </summary>
    public async Task<IReadOnlySet<int>?> GetRecordedDaysAsync(int channel, DateTime month, CancellationToken ct = default)
    {
        var path = $"ISAPI/ContentMgmt/record/tracks/{StreamUrls.TrackId(channel, StreamQuality.Main)}/dailyDistribution";
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(BuildDailyDistributionRequest(month), Encoding.UTF8, new MediaTypeHeaderValue("application/xml")),
        };
        string text;
        try
        {
            using var response = await SendAsync(request, ct);
            text = await ReadTextAsync(response, ct);
        }
        catch (IsapiException e) when (MonthQueryUnsupported(e.Status))
        {
            return null;
        }
        try
        {
            return ParseDailyDistribution(IsapiXml.Parse(text));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The month query is not there on this recorder (or not for this user): fall back to day searches.</summary>
    internal static bool MonthQueryUnsupported(HttpStatusCode? status) =>
        status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;

    internal static string BuildDailyDistributionRequest(DateTime month)
    {
        var doc = new XElement("trackDailyParam",
            new XElement("year", month.Year),
            new XElement("monthOfYear", month.Month));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc;
    }

    /// <summary>
    /// <c>trackDailyDistribution/dayList/day</c> (namespace or not): day numbers whose <c>record</c> is "true"
    /// (<c>dayOfMonth</c>, else <c>id</c>). Null when the document is no day list (e.g. a ResponseStatus refusal).
    /// </summary>
    internal static IReadOnlySet<int>? ParseDailyDistribution(XDocument doc)
    {
        var root = doc.Root!;
        var dayList = root.Name.LocalName == "dayList" ? root : Elements(root, "dayList").FirstOrDefault();
        if (root.Name.LocalName == "ResponseStatus" || dayList is null)
        {
            return null;
        }
        var days = new HashSet<int>();
        foreach (var day in dayList.Elements().Where(e => e.Name.LocalName == "day"))
        {
            var number = Int(day, "dayOfMonth") is > 0 and var n ? n : Int(day, "id");
            if (number is >= 1 and <= 31 && string.Equals(Value(day, "record"), "true", StringComparison.OrdinalIgnoreCase))
            {
                days.Add(number);
            }
        }
        return days;
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

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var response = await SendRawAsync(request, ct, completion);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            var message = status == HttpStatusCode.Unauthorized
                ? UnauthorizedMessage(response)
                : $"Cihaz {(int)status} {status} döndürdü ({request.RequestUri}).";
            response.Dispose();
            throw new IsapiException(message, status);
        }
        return response;
    }

    /// <summary>
    /// TLS handshake of an HTTPS device: the certificate must carry the pinned public key. Without a pin it is accepted
    /// and remembered (stored after the device answers). A refused certificate fails the handshake, so not a byte of
    /// HTTP — no Digest answer either — is sent. Chain and name errors are expected (self-signed) and not used.
    /// </summary>
    bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null || _pinned is null)
        {
            return false;
        }
        var presented = CertificatePin.From(certificate, DateTimeOffset.Now);
        if (_pinned.Certificate is not { } pin)
        {
            Volatile.Write(ref _firstUse, presented);
            return true;
        }
        if (pin.SameKey(presented))
        {
            return true;
        }
        Volatile.Write(ref _mismatch, presented);
        return false;
    }

    /// <summary>
    /// One request, body buffered (at most <see cref="MaxResponseLength"/>; only the alert stream asks for the headers alone,
    /// <see cref="OpenAlertStreamAsync"/>, and caps its parts itself). Transport failures, an over-long answer
    /// and any redirect (never followed: see the handler) throw <see cref="IsapiException"/>; other statuses are
    /// the caller's. HTTPS: a certificate that is not the pinned one throws <see cref="CertificateChangedException"/>
    /// unless the user trusts it (<see cref="CertificateTrust.ResolveMismatchAsync"/>, one question for all requests),
    /// in which case the request is sent once more.
    /// </summary>
    async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (_pinned is null || _trust is null)
        {
            return await SendOnceAsync(request, ct, completion);
        }
        using var retry = await CloneAsync(request, ct); // a sent HttpRequestMessage cannot be sent again
        try
        {
            return await SendOnceAsync(request, ct, completion);
        }
        catch (CertificateChangedException e)
        {
            if (!await _trust.ResolveMismatchAsync(_pinned, e.Presented).WaitAsync(ct))
            {
                CertificateRefused = true;
                throw;
            }
            try
            {
                return await SendOnceAsync(retry, ct, completion);
            }
            catch (CertificateChangedException)
            {
                CertificateRefused = true; // yet another key: no second question for the same request
                throw;
            }
        }
    }

    static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is { } content)
        {
            var body = new ByteArrayContent(await content.ReadAsByteArrayAsync(ct));
            foreach (var header in content.Headers)
            {
                body.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            copy.Content = body;
        }
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }

    async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption completion)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            Answered = true;
            throw new IsapiException(TooLargeMessage);
        }
        catch (HttpRequestException e) when (_pinned is not null && e.HttpRequestError == HttpRequestError.SecureConnectionError &&
                                             Volatile.Read(ref _mismatch) is { } presented &&
                                             !(_pinned.Certificate?.SameKey(presented) ?? false))
        {
            // The handshake refused the certificate: nothing was sent. Not a connection failure (no "HTTP ile dene").
            throw new CertificateChangedException(_pinned, _pinned.Certificate, presented);
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
        if (_pinned is not null && _trust is not null && Volatile.Read(ref _firstUse) is { } first && !_trust.PinFirstUse(_pinned, first) &&
            _pinned.Certificate is { } pin && !pin.SameKey(first))
        {
            // Another request pinned a different key while this one ran unpinned: do not use this answer.
            response.Dispose();
            throw new CertificateChangedException(_pinned, pin, first);
        }
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new IsapiException($"Cihaz isteği başka bir adrese yönlendirdi ({(int)status} {status}); yönlendirme izlenmez.", status);
        }
        return response;
    }

    const string TooLargeMessage = "Cihazın yanıtı beklenenden çok büyük; okunmadı.";

    /// <summary>401 text. A device that offers only Basic gets no password (Digest only, see the handler): say so.</summary>
    static string UnauthorizedMessage(HttpResponseMessage response)
    {
        var schemes = response.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList();
        return schemes.Count > 0 && !schemes.Any(s => s.Equals("Digest", StringComparison.OrdinalIgnoreCase))
            ? "Cihaz yalnızca şifreyi açık gönderen Basic kimlik doğrulamasını istedi; şifre gönderilmedi (Digest gerekir)."
            : "Kullanıcı adı veya şifre hatalı.";
    }

    /// <summary>Body text of an XML answer, at most <see cref="IsapiXml.MaxLength"/> characters.</summary>
    static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > IsapiXml.MaxLength)
        {
            throw new IsapiException(TooLargeMessage);
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > IsapiXml.MaxLength ? throw new IsapiException(TooLargeMessage) : text;
    }

    static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await ReadTextAsync(response, ct);
        try
        {
            return IsapiXml.Parse(text);
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
