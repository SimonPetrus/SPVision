using System.Globalization;

namespace SPVision.Core;

/// <summary>
/// "HTTPS kullan" in the device editor: the HTTP port field follows the checkbox between the standard ports
/// (80 ↔ 443); a custom port is left alone. Also decides when a failed HTTPS attempt may offer HTTP instead.
/// </summary>
public static class HttpsPort
{
    public const int Http = 80;
    public const int Https = 443;

    /// <summary>Port field text after HTTPS was switched on or off: "80" → "443", "443" → "80"; anything else unchanged.</summary>
    public static string Follow(string portText, bool useHttps)
    {
        if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return portText;
        }
        return useHttps && port == Http ? Https.ToString(CultureInfo.InvariantCulture)
            : !useHttps && port == Https ? Http.ToString(CultureInfo.InvariantCulture)
            : portText;
    }

    /// <summary>
    /// Whether a failed attempt may offer HTTP: only when HTTPS was on and the device gave no HTTP answer at all
    /// (TCP refused/reset, TLS handshake failed, timed out). A device that answered, with any status (401 etc.) or
    /// earlier in the same attempt (answered), never does.
    /// </summary>
    public static bool OfferHttpFallback(bool useHttps, bool answered, Exception error) =>
        useHttps && !answered && error is IsapiException { ConnectionFailed: true };
}
