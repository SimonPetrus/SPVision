using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>
/// Settings › Cihaz ayarları: whole-document GET and the single ISAPI write path of the app (PUT), guarded by
/// <see cref="IsapiWritePolicy"/>. Used through <see cref="ConfigWriter"/> (read-modify-write, verify).
/// </summary>
public sealed partial class IsapiClient : IConfigTransport
{
    public Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default) => GetXmlAsync(path, ct);

    /// <summary>
    /// The one method that writes to a device. The path is checked against the allowlist (and the body against the
    /// deny list) before any network I/O. Returns the device's ResponseStatus; an HTTP error with a ResponseStatus
    /// body is returned as that status, not thrown.
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
        // A redirect throws here, before its body could pass for a ResponseStatus; it is never followed.
        using (var response = await SendRawAsync(request, ct))
        {
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
}
