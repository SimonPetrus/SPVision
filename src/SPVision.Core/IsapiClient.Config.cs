using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>
/// Settings › Cihaz ayarları: whole-document GET and the only two ISAPI write paths of the app, both guarded by
/// <see cref="IsapiWritePolicy"/>: ordinary settings (PUT, <see cref="PutConfigAsync"/>, used through
/// <see cref="ConfigWriter"/>: read-modify-write, verify) and risky operations (<see cref="SendRiskyAsync"/>, only with a
/// <see cref="ConfirmedRiskyWrite"/>).
/// </summary>
public sealed partial class IsapiClient : IConfigTransport, IRiskyTransport
{
    public Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default) => GetXmlAsync(path, ct);

    /// <summary>
    /// Ordinary write. The path is checked against the allowlist (and the body against the deny list) before any
    /// network I/O. Returns the device's ResponseStatus; an HTTP error with a ResponseStatus body is returned as that
    /// status, not thrown.
    /// </summary>
    public async Task<IsapiResponseStatus> PutConfigAsync(string path, XDocument body, CancellationToken ct = default)
    {
        IsapiWritePolicy.EnsureAllowed(path);
        IsapiWritePolicy.EnsureBodyAllowed(body);

        var xml = body.Declaration is null ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + body.ToString(SaveOptions.DisableFormatting)
            : body.Declaration + body.ToString(SaveOptions.DisableFormatting);
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(xml, Encoding.UTF8, new MediaTypeHeaderValue("application/xml")),
        };
        return await SendForStatusAsync(request, ct);
    }

    /// <summary>
    /// Risky write (reboot, disk format, network address, users): sends exactly the confirmed request, once. The token
    /// is consumed first (a second use or an expired one throws), then method, path and body are checked against the
    /// policy again, and a body with a password needs HTTPS — all before any network I/O.
    /// </summary>
    public async Task<IsapiResponseStatus> SendRiskyAsync(ConfirmedRiskyWrite confirmed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        var risky = confirmed.Consume();
        IsapiWritePolicy.EnsureRiskyAllowed(risky.Operation, risky.Method, risky.Path);
        if (risky.Operation is RiskyOperation.AddUser or RiskyOperation.ChangePassword &&
            !string.Equals(_http.BaseAddress?.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // The body carries passwords in clear text (Digest protects only the login): never over plain HTTP.
            throw new IsapiWriteDeniedException("Şifre içeren işlemler yalnızca HTTPS bağlantısıyla gönderilir (Ayarlar › Cihaz yönetimi › HTTPS kullan).");
        }
        IsapiWritePolicy.EnsureRiskyBodyAllowed(risky.Operation, risky.Body is null ? null : IsapiXml.Parse(risky.Body),
            risky.Original is null ? null : IsapiXml.Parse(risky.Original));

        using var request = new HttpRequestMessage(new HttpMethod(risky.Method), risky.Path);
        if (risky.Body is not null)
        {
            request.Content = new StringContent(risky.Body, Encoding.UTF8, new MediaTypeHeaderValue("application/xml"));
        }
        return await SendForStatusAsync(request, ct);
    }

    /// <summary>A redirect throws in <see cref="SendRawAsync"/>, before its body could pass for a ResponseStatus; it is never followed.</summary>
    async Task<IsapiResponseStatus> SendForStatusAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await SendRawAsync(request, ct);
        var text = await ReadTextAsync(response, ct);
        XDocument? doc = null;
        try
        {
            doc = text.Length > 0 ? IsapiXml.Parse(text) : null;
        }
        catch (System.Xml.XmlException)
        {
        }
        if (IsapiResponseStatus.Parse(doc) is { } status)
        {
            return status;
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new IsapiException(UnauthorizedMessage(response), response.StatusCode);
        }
        // A 2xx without a ResponseStatus body is not a confirmed success.
        return IsapiResponseStatus.FromHttp(response.StatusCode);
    }
}
