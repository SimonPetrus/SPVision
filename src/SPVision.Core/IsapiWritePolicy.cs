using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>A write that the policy refuses; thrown before any network I/O.</summary>
public sealed class IsapiWriteDeniedException(string message) : Exception(message);

/// <summary>
/// The only ISAPI paths SPVision may PUT to (Settings › Cihaz ayarları, phase 1). Every write goes through
/// <see cref="IsapiClient.PutConfigAsync"/>, which calls <see cref="EnsureAllowed"/> first. Security settings
/// (stream encryption, Hik-Connect/EZVIZ, users, network) and destructive operations (reboot, format, factory
/// reset, firmware upgrade, activation) are denied by keyword even if a pattern were ever widened by mistake.
/// Later phases extend <see cref="AllowedPatterns"/>; the deny list stays.
/// </summary>
public static class IsapiWritePolicy
{
    /// <summary>Matched case-insensitively and culture-independently (ordinal), so a Turkish "ı/İ" cannot slip past.</summary>
    static readonly string[] DeniedKeywords =
    [
        "encrypt", "EZVIZ", "HikConnect", "Hik-Connect", "platform", "Security", "Users", "Network",
        "factoryReset", "reboot", "format", "upgrade", "activate",
    ];

    /// <summary>Phase 1 endpoints, exact case, whole path. {n} = 1-3 digit channel, {t} = channel·100 + stream.</summary>
    public static readonly IReadOnlyList<string> AllowedPatterns =
    [
        @"^ISAPI/System/deviceInfo$",                                // device name
        @"^ISAPI/System/time$",                                      // "Bilgisayarla eşitle"
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}$",  // IP channel name
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/overlays$",         // OSD date/time + name
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/motionDetection$",  // motion on/off + sensitivity
        @"^ISAPI/Image/channels/[1-9][0-9]{0,2}$",                   // brightness / contrast / saturation
        @"^ISAPI/ContentMgmt/StreamingProxy/channels/[1-9][0-9]{0,2}0[12]$", // main / sub stream
        @"^ISAPI/ContentMgmt/record/tracks/[1-9][0-9]{0,2}01$",      // recording schedule + pre/post record
    ];

    static readonly Regex[] Allowed = AllowedPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToArray();

    /// <summary>Plain ASCII path characters only: no query, fragment, escapes, dots, back slashes or scheme.</summary>
    static readonly Regex PlainPath = new("^[A-Za-z0-9/]+$", RegexOptions.CultureInvariant);

    /// <summary>XML elements that must never be sent back to the device, whatever the path.</summary>
    static readonly string[] DeniedElements = ["encrypt", "password", "EZVIZ", "platform", "verificationCode"];

    /// <summary>Null when the path may be written; otherwise why not (Turkish, for the user).</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Yazma adresi boş.";
        }
        foreach (var keyword in DeniedKeywords)
        {
            if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return $"Bu ayar SPVision'dan değiştirilemez ({keyword}).";
            }
        }
        if (!PlainPath.IsMatch(path) || path.Contains("//", StringComparison.Ordinal) || path.StartsWith('/'))
        {
            return "Yazma adresi geçersiz karakter içeriyor.";
        }
        return Allowed.Any(r => r.IsMatch(path)) ? null : "Bu ayar izin verilen yazma listesinde değil.";
    }

    public static void EnsureAllowed(string? path)
    {
        if (Check(path) is { } reason)
        {
            throw new IsapiWriteDeniedException(reason);
        }
    }

    /// <summary>Refuses a body that carries a secret or a security element (defence in depth).</summary>
    public static void EnsureBodyAllowed(XDocument body)
    {
        var root = body.Root ?? throw new IsapiWriteDeniedException("Gönderilecek belge boş.");
        foreach (var element in root.DescendantsAndSelf())
        {
            var name = element.Name.LocalName;
            if (DeniedElements.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IsapiWriteDeniedException($"Gönderilecek belgede değiştirilemeyen bir alan var ({name}).");
            }
        }
    }
}
