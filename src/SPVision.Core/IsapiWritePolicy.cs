using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SPVision.Core;

/// <summary>A write that the policy refuses; thrown before any network I/O.</summary>
public sealed class IsapiWriteDeniedException(string message) : Exception(message);

/// <summary>Writes that can cut the connection, lock users out or destroy recordings (Settings › Cihaz ayarları, phases 2 and 3).</summary>
public enum RiskyOperation
{
    Reboot,
    FormatDisk,
    NetworkAddress,
    AddUser,
    ChangePassword,
    DeleteUser,

    // Phase 3 (Bakım): exact entries in MaintenancePolicy.
    FactoryReset,
    RecorderFirmware,
    CameraFirmware,
}

/// <summary>
/// The only ISAPI paths SPVision may write to. Two routes, both checked here before any network I/O:
/// <list type="bullet">
/// <item>Ordinary settings (<see cref="Check"/>): PUT through <see cref="IsapiClient.PutConfigAsync"/> /
/// <see cref="ConfigWriter"/>, exact paths of <see cref="AllowedPatterns"/>.</item>
/// <item>Risky operations (<see cref="CheckRisky"/>): reboot, disk format, the network address and user management.
/// Only <see cref="IsapiClient.SendRiskyAsync"/> sends them, and only with a <see cref="ConfirmedRiskyWrite"/> that the
/// separate confirmation dialog produced (<see cref="ConfirmedRiskyWrite.Confirm"/>). Exact method + path per operation.</item>
/// </list>
/// <see cref="ForbiddenKeywords"/> (stream encryption, Hik-Connect/EZVIZ/cloud, certificates/TLS, admin access ports,
/// login lock, factory reset, firmware, activation…) are refused on every route, whatever a pattern says. The only
/// exceptions are the exact whole-string entries of <see cref="MaintenancePolicy"/> (phase 3: factory reset
/// mode=basic|full, recorder and camera firmware through the SDK), reachable only by their own risky operation.
/// <see cref="GuardedKeywords"/> (Security, Users, Network, reboot, format) are refused on the ordinary route; the risky
/// route reaches them only through its exact entries.
/// </summary>
public static class IsapiWritePolicy
{
    /// <summary>Never written by SPVision. Matched case-insensitively and culture-independently (ordinal: no Turkish "ı/İ" gap).</summary>
    public static readonly IReadOnlyList<string> ForbiddenKeywords =
    [
        "encrypt", "EZVIZ", "HikConnect", "Hik-Connect", "platform", "cloud",
        "factoryReset", "upgrade", "firmware", "activate", "configurationData",
        "adminAccess", "certificate", "SRTP", "TLS", "illegalLogin", "loginLock", "securityQuestion", "ONVIF", "shutdown",
    ];

    /// <summary>Never on the ordinary route; the risky route allows only its exact <see cref="RiskyPatterns"/>.</summary>
    public static readonly IReadOnlyList<string> GuardedKeywords = ["Security", "Users", "Network", "reboot", "format"];

    /// <summary>Ordinary endpoints, exact case, whole path. {n} = 1-3 digit channel, {t} = channel·100 + stream.</summary>
    public static readonly IReadOnlyList<string> AllowedPatterns =
    [
        // Phase 1. (The IP channel record ContentMgmt/InputProxy/channels/{n} is NOT here: it carries the camera's
        // address and login, and its password cannot be read back, so a rename could drop the camera. See §9.)
        @"^ISAPI/System/deviceInfo$",                                // device name
        @"^ISAPI/System/time$",                                      // "Bilgisayarla eşitle"
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/overlays$",         // OSD date/time + name
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/motionDetection$",  // motion on/off + sensitivity
        @"^ISAPI/Image/channels/[1-9][0-9]{0,2}$",                   // brightness / contrast / saturation
        @"^ISAPI/ContentMgmt/StreamingProxy/channels/[1-9][0-9]{0,2}0[12]$", // main / sub stream
        @"^ISAPI/ContentMgmt/record/tracks/[1-9][0-9]{0,2}01$",      // recording schedule + pre/post record
        // Phase 2: Olaylar.
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/videoLoss$",        // video loss on/off
        @"^ISAPI/ContentMgmt/InputProxy/channels/[1-9][0-9]{0,2}/video/tamperDetection$",  // tampering on/off
        @"^ISAPI/Event/triggers/(videoloss|tamper)-[1-9][0-9]{0,2}$",                      // their linkage
        @"^ISAPI/Event/triggers/(diskfull|diskerror|nicbroken|ipconflict|illaccess|recordingfailure)$", // exception linkage
    ];

    /// <summary>Risky endpoints: operation, HTTP method and exact path. The built-in admin (user 1) can never be deleted.</summary>
    public static readonly IReadOnlyList<(RiskyOperation Operation, string Method, string Pattern)> RiskyPatterns =
    [
        (RiskyOperation.Reboot, "PUT", @"^ISAPI/System/reboot$"),
        (RiskyOperation.FormatDisk, "PUT", @"^ISAPI/ContentMgmt/Storage/hdd/[1-9][0-9]?/format$"),
        (RiskyOperation.NetworkAddress, "PUT", @"^ISAPI/System/Network/interfaces/[1-9]/ipAddress$"),
        (RiskyOperation.AddUser, "POST", @"^ISAPI/Security/users$"),
        (RiskyOperation.ChangePassword, "PUT", @"^ISAPI/Security/users/[1-9][0-9]?$"),
        (RiskyOperation.DeleteUser, "DELETE", @"^ISAPI/Security/users/([2-9]|[1-9][0-9])$"),
    ];

    static readonly Regex[] Allowed = AllowedPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToArray();

    static readonly (RiskyOperation Operation, string Method, Regex Pattern)[] Risky =
        RiskyPatterns.Select(r => (r.Operation, r.Method, new Regex(r.Pattern, RegexOptions.CultureInvariant))).ToArray();

    /// <summary>Plain ASCII path characters only: no query, fragment, escapes, dots, back slashes or scheme.</summary>
    static readonly Regex PlainPath = new("^[A-Za-z0-9/-]+$", RegexOptions.CultureInvariant);

    /// <summary>XML elements that must never be sent on the ordinary route, whatever the path.</summary>
    static readonly string[] DeniedElements = ["encrypt", "password", "EZVIZ", "platform", "verificationCode", "sourceInputPortDescriptor"];

    /// <summary>Elements a risky body may never carry (passwords are allowed only in the user bodies, see below).</summary>
    static readonly string[] RiskyDeniedElements = ["encrypt", "EZVIZ", "platform", "verificationCode", "cloud", "certificate", "adminAccess"];

    /// <summary>The whole vocabulary of a user body: no permissions, bindings or enable flags are ever sent.</summary>
    static readonly string[] UserElements = ["User", "id", "userName", "password", "loginPassword", "userLevel"];

    /// <summary>Ordinary route (PUT). Null when the path may be written; otherwise why not (Turkish, for the user).</summary>
    public static string? Check(string? path)
    {
        if (CheckCommon(path) is { } common)
        {
            return common;
        }
        foreach (var keyword in GuardedKeywords)
        {
            if (path!.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return $"Bu ayar yalnızca ayrı onaylı işlemle değiştirilebilir ({keyword}).";
            }
        }
        return Allowed.Any(r => r.IsMatch(path!)) ? null : "Bu ayar izin verilen yazma listesinde değil.";
    }

    public static void EnsureAllowed(string? path)
    {
        if (Check(path) is { } reason)
        {
            throw new IsapiWriteDeniedException(reason);
        }
    }

    /// <summary>Risky route: the exact method + path of <paramref name="operation"/>. Null when allowed.</summary>
    public static string? CheckRisky(RiskyOperation operation, string? method, string? path)
    {
        if (MaintenancePolicy.Handles(operation))
        {
            return MaintenancePolicy.Check(operation, method, path); // exact whole strings only (phase 3)
        }
        if (CheckCommon(path) is { } common)
        {
            return common;
        }
        return Risky.Any(r => r.Operation == operation && string.Equals(r.Method, method, StringComparison.Ordinal) && r.Pattern.IsMatch(path!))
            ? null
            : "Bu işlem izin verilen yazma listesinde değil.";
    }

    public static void EnsureRiskyAllowed(RiskyOperation operation, string? method, string? path)
    {
        if (CheckRisky(operation, method, path) is { } reason)
        {
            throw new IsapiWriteDeniedException(reason);
        }
    }

    static string? CheckCommon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Yazma adresi boş.";
        }
        foreach (var keyword in ForbiddenKeywords)
        {
            if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return $"Bu ayar SPVision'dan değiştirilemez ({keyword}).";
            }
        }
        if (!PlainPath.IsMatch(path) || path.Contains("//", StringComparison.Ordinal) || path.StartsWith('/') || path.EndsWith('/'))
        {
            return "Yazma adresi geçersiz karakter içeriyor.";
        }
        return null;
    }

    /// <summary>Refuses a body that carries a secret or a security element (defence in depth).</summary>
    public static void EnsureBodyAllowed(XDocument body)
    {
        var root = body.Root ?? throw new IsapiWriteDeniedException("Gönderilecek belge boş.");
        DenyElements(root, DeniedElements);
    }

    /// <summary>
    /// The body of a risky operation: none for reboot / format / delete; an <c>IPAddress</c> document for the network
    /// address; a bare <c>User</c> (id, userName, password, loginPassword, userLevel only) for user changes, where a new
    /// user is never an administrator.
    /// </summary>
    public static void EnsureRiskyBodyAllowed(RiskyOperation operation, XDocument? body)
    {
        switch (operation)
        {
            case RiskyOperation.Reboot or RiskyOperation.FormatDisk or RiskyOperation.DeleteUser
                or RiskyOperation.FactoryReset or RiskyOperation.RecorderFirmware or RiskyOperation.CameraFirmware:
                // (Firmware has no XML body: the SDK reads the confirmed file, see FirmwareJob.)
                if (body is not null)
                {
                    throw new IsapiWriteDeniedException("Bu işlem gövde göndermez.");
                }
                return;
            case RiskyOperation.NetworkAddress:
            {
                var root = body?.Root ?? throw new IsapiWriteDeniedException("Gönderilecek belge boş.");
                if (root.Name.LocalName != "IPAddress")
                {
                    throw new IsapiWriteDeniedException("Ağ adresi belgesi beklenirdi.");
                }
                DenyElements(root, [.. RiskyDeniedElements, "password", "userName"]);
                return;
            }
            default:
            {
                var root = body?.Root ?? throw new IsapiWriteDeniedException("Gönderilecek belge boş.");
                if (root.Name.LocalName != "User" || root.Elements().Any(e => e.HasElements))
                {
                    throw new IsapiWriteDeniedException("Kullanıcı belgesi beklenirdi.");
                }
                foreach (var element in root.Elements())
                {
                    if (!UserElements.Contains(element.Name.LocalName, StringComparer.Ordinal))
                    {
                        throw new IsapiWriteDeniedException($"Gönderilecek belgede değiştirilemeyen bir alan var ({element.Name.LocalName}).");
                    }
                }
                DenyElements(root, RiskyDeniedElements);
                if (operation == RiskyOperation.AddUser &&
                    root.Elements().FirstOrDefault(e => e.Name.LocalName == "userLevel")?.Value is not ("Operator" or "Viewer"))
                {
                    throw new IsapiWriteDeniedException("Yeni kullanıcı yalnızca Operatör veya İzleyici olabilir.");
                }
                return;
            }
        }
    }

    static void DenyElements(XElement root, IEnumerable<string> denied)
    {
        var list = denied.ToList();
        foreach (var element in root.DescendantsAndSelf())
        {
            var name = element.Name.LocalName;
            if (list.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IsapiWriteDeniedException($"Gönderilecek belgede değiştirilemeyen bir alan var ({name}).");
            }
        }
    }
}
