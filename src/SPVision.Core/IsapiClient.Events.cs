namespace SPVision.Core;

/// <summary>The open alert stream of one recorder: the body to read and its multipart boundary (null: none given).</summary>
public sealed class AlertStreamConnection(HttpResponseMessage response, Stream body, string? boundary, string? contentType) : IDisposable
{
    public Stream Body { get; } = body;

    public string? Boundary { get; } = boundary;

    public string? ContentType { get; } = contentType;

    public void Dispose()
    {
        Body.Dispose();
        response.Dispose();
    }
}

public sealed partial class IsapiClient
{
    /// <summary>The recorder's event notifications: a long-lived, read-only subscription (GET; nothing is configured on the device).</summary>
    public const string AlertStreamPath = "ISAPI/Event/notification/alertStream";

    /// <summary>
    /// Opens <c>GET ISAPI/Event/notification/alertStream</c> through the same rules as every request (Digest only, no
    /// redirects, HTTPS certificate pin). Only the headers are awaited (within the client's timeout); the body stays open
    /// and is read by the caller with <see cref="AlertStreamParser"/>, which caps every part — the client's buffer limit does
    /// not apply to a stream. Throws <see cref="IsapiException"/> like the other requests (403/404 = not supported).
    /// </summary>
    public async Task<AlertStreamConnection> OpenAlertStreamAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, AlertStreamPath), ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            var type = response.Content.Headers.ContentType;
            var boundary = type?.Parameters
                .FirstOrDefault(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
            var body = await response.Content.ReadAsStreamAsync(ct);
            return new AlertStreamConnection(response, body, string.IsNullOrWhiteSpace(boundary) ? null : boundary, type?.MediaType);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
