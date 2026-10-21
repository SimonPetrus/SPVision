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

    /// <summary>
    /// HTTP / HTTPS / RTSP / server (SDK) port numbers in ISAPI/Security/adminAccesses: the document exactly as just read,
    /// with only those portNo values changed (<see cref="IsapiWritePolicy.CheckAdminPortsBody"/>).
    /// </summary>
    AdminPorts,

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
        // address and login, and its password cannot be read back, so a rename could drop the camera. Renames use the
        // SDK picture configuration instead: SdkWritePolicy. See §9.)
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
        (RiskyOperation.AdminPorts, "PUT", @"^ISAPI/Security/adminAccesses$"),
    ];

    /// <summary>
    /// The one place a <see cref="ForbiddenKeywords"/> entry is lifted, for one exact (operation, method, path) triple and
    /// one keyword only: the port numbers of adminAccesses. The body may then differ from the document just read only in
    /// <c>portNo</c> of HTTP / HTTPS / RTSP / DEV_MANAGE (<see cref="CheckAdminPortsBody"/>); any other adminAccess path,
    /// method or operation stays forbidden.
    /// </summary>
    public static readonly IReadOnlyList<(RiskyOperation Operation, string Method, string Path, string Keyword)> KeywordExemptions =
    [
        (RiskyOperation.AdminPorts, "PUT", IsapiPaths.AdminAccesses, "adminAccess"),
    ];

    /// <summary>Protocols whose port SPVision may change (the others, e.g. WebSocket, IOT, SDK_OVER_TLS, are never touched).</summary>
    public static readonly IReadOnlyList<string> EditablePortProtocols = ["HTTP", "HTTPS", "RTSP", "DEV_MANAGE"];

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
        var exempt = KeywordExemptions
            .Where(x => x.Operation == operation && string.Equals(x.Method, method, StringComparison.Ordinal) && string.Equals(x.Path, path, StringComparison.Ordinal))
            .Select(x => x.Keyword).FirstOrDefault();
        if (CheckCommon(path, exempt) is { } common)
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

    static string? CheckCommon(string? path, string? exemptKeyword = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Yazma adresi boş.";
        }
        foreach (var keyword in ForbiddenKeywords)
        {
            if (string.Equals(keyword, exemptKeyword, StringComparison.Ordinal))
            {
                continue; // only for the exact KeywordExemptions triple
            }
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
    public static void EnsureRiskyBodyAllowed(RiskyOperation operation, XDocument? body) => EnsureRiskyBodyAllowed(operation, body, null);

    /// <summary>
    /// As above; <paramref name="original"/> is the document just read from the device, required for
    /// <see cref="RiskyOperation.AdminPorts"/> (the body may differ from it only in the allowed port numbers).
    /// </summary>
    public static void EnsureRiskyBodyAllowed(RiskyOperation operation, XDocument? body, XDocument? original)
    {
        switch (operation)
        {
            case RiskyOperation.AdminPorts:
                if (CheckAdminPortsBody(original, body) is { } reason)
                {
                    throw new IsapiWriteDeniedException(reason);
                }
                return;
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

    /// <summary>Elements a port body may never carry, even unchanged.</summary>
    static readonly string[] PortBodyDeniedElements = ["password", "secret", "privateKey", "verificationCode", "encrypt", "EZVIZ", "platform", "cloud"];

    /// <summary>
    /// Strict structural diff of an adminAccesses body against the document just read: same elements in the same order,
    /// same names and namespaces, same attributes, same text everywhere, except <c>portNo</c> of an existing
    /// <c>AdminAccessProtocol</c> whose <c>protocol</c> is one of <see cref="EditablePortProtocols"/>; a changed port is a
    /// plain number 1–65535 and is not used by any other entry. At least one port must change. Null when allowed.
    /// </summary>
    public static string? CheckAdminPortsBody(XDocument? original, XDocument? body)
    {
        if (original?.Root is not { } before)
        {
            return "Cihazdan okunan port belgesi olmadan port değişikliği gönderilmez.";
        }
        if (body?.Root is not { } after)
        {
            return "Gönderilecek belge boş.";
        }
        if (before.Name.LocalName != "AdminAccessProtocolList" || before.Elements().Any(e => e.Name.LocalName != "AdminAccessProtocol"))
        {
            return "Port listesi belgesi beklenirdi.";
        }
        foreach (var element in after.DescendantsAndSelf())
        {
            var name = element.Name.LocalName;
            if (PortBodyDeniedElements.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                return $"Gönderilecek belgede değiştirilemeyen bir alan var ({name}).";
            }
        }
        var changed = new List<XElement>();
        if (Diff(before, after, changed) is { } difference)
        {
            return difference;
        }
        if (changed.Count == 0)
        {
            return "Port değişmemiş; gönderilmedi.";
        }
        var ports = after.Elements().Select(e => e.Elements().Where(c => c.Name.LocalName == "portNo").Select(c => c.Value).FirstOrDefault()).ToList();
        foreach (var port in changed)
        {
            if (ports.Count(p => p == port.Value) > 1)
            {
                return $"Port {port.Value} başka bir hizmette de kullanılıyor; gönderilmedi.";
            }
        }
        return null;
    }

    /// <summary>Null when <paramref name="b"/> equals <paramref name="a"/> apart from allowed port changes (collected).</summary>
    static string? Diff(XElement a, XElement b, List<XElement> changed)
    {
        if (a.Name != b.Name)
        {
            return $"Belgenin yapısı değişmiş ({a.Name.LocalName} → {b.Name.LocalName}); gönderilmedi.";
        }
        var attributesA = a.Attributes().Select(x => (x.Name, x.Value)).ToList();
        var attributesB = b.Attributes().Select(x => (x.Name, x.Value)).ToList();
        if (!attributesA.SequenceEqual(attributesB))
        {
            return $"{a.Name.LocalName} öğesinin nitelikleri değişmiş; gönderilmedi.";
        }
        var nodesA = a.Nodes().ToList();
        var nodesB = b.Nodes().ToList();
        if (nodesA.Count != nodesB.Count)
        {
            return $"{a.Name.LocalName} öğesine öğe eklenmiş veya çıkarılmış; gönderilmedi.";
        }
        for (var i = 0; i < nodesA.Count; i++)
        {
            switch (nodesA[i], nodesB[i])
            {
                case (XElement x, XElement y):
                    if (Diff(x, y, changed) is { } inner)
                    {
                        return inner;
                    }
                    break;
                case (XText x, XText y) when x.NodeType == y.NodeType:
                    if (!string.Equals(x.Value, y.Value, StringComparison.Ordinal))
                    {
                        if (!IsEditablePort(a) || nodesA.Count != 1)
                        {
                            return $"{a.Name.LocalName} değeri değişmiş; yalnızca HTTP, HTTPS, RTSP ve sunucu portu değiştirilebilir.";
                        }
                        if (!IsPortNumber(y.Value))
                        {
                            return $"Port değeri geçersiz ({y.Value}); 1–65535 arası bir sayı olmalı.";
                        }
                        changed.Add(b);
                    }
                    break;
                default:
                    if (!XNode.DeepEquals(nodesA[i], nodesB[i]))
                    {
                        return $"{a.Name.LocalName} öğesinin içeriği değişmiş; gönderilmedi.";
                    }
                    break;
            }
        }
        return null;
    }

    /// <summary>portNo directly under an AdminAccessProtocol entry (direct child of the list) of an editable protocol.</summary>
    static bool IsEditablePort(XElement element)
    {
        if (element.Name.LocalName != "portNo" || element.Parent is not { } entry || entry.Name.LocalName != "AdminAccessProtocol" ||
            entry.Parent is not { Parent: null } || entry.Elements().Count(e => e.Name.LocalName == "portNo") != 1)
        {
            return false;
        }
        var protocols = entry.Elements().Where(e => e.Name.LocalName == "protocol").ToList();
        if (protocols.Count != 1 || protocols[0].HasElements)
        {
            return false;
        }
        var protocol = protocols[0].Value;
        // The protocol must be editable and appear in exactly one entry of the list (no ambiguity).
        return EditablePortProtocols.Contains(protocol, StringComparer.Ordinal) &&
               entry.Parent!.Elements().Count(e => e.Elements().Any(c => c.Name.LocalName == "protocol" && c.Value == protocol)) == 1;
    }

    static bool IsPortNumber(string text) =>
        text.Length is >= 1 and <= 5 && text.All(char.IsAsciiDigit) && text[0] != '0' &&
        int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 65535;

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
