using System.Globalization;

namespace SPVision.Core.Updates;

public enum UpdateKind
{
    None,

    /// <summary>Newer version: a banner offers it; the app stays usable ("Sonra").</summary>
    Optional,

    /// <summary>The app may not be used until it is updated (the user still has to press Güncelle; Çıkış quits).</summary>
    Mandatory,
}

/// <summary>
/// What to do with the current version: <see cref="Kind"/> and the manifest to install (<see cref="Target"/>);
/// <see cref="KeepMandatory"/> is the verified mandatory manifest to remember (null = forget it), so a mandatory update is
/// still enforced when a later check fails (offline, proxy down).
/// </summary>
public sealed record UpdateResolution(UpdateKind Kind, UpdateManifest? Target, UpdateManifest? KeepMandatory);

/// <summary>Pure decision rules of the updater (unit tested).</summary>
public static class UpdatePolicy
{
    /// <summary>The automatic check runs this long after startup (the camera wall connects first).</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    /// <summary>Automatic checks run at most this often (manual "Denetle" always runs).</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// None when <paramref name="manifest"/> is not newer than <paramref name="current"/>; Mandatory when it is newer and
    /// marked mandatory, or when <paramref name="current"/> is below its minimum supported version; otherwise Optional.
    /// </summary>
    public static UpdateKind Decide(AppVersion current, UpdateManifest manifest) =>
        manifest.Version <= current ? UpdateKind.None :
        manifest.Mandatory || current < manifest.MinSupportedVersion ? UpdateKind.Mandatory :
        UpdateKind.Optional;

    /// <summary>
    /// Combines a fresh check (<paramref name="fresh"/>, null when it failed) with the remembered mandatory manifest
    /// (<paramref name="remembered"/>, already signature-verified). Fail-open for availability: a failed check never blocks
    /// by itself — but a mandatory update that was verified earlier stays mandatory until a version at least as new as it is
    /// installed, even if a newer release is optional or the check fails. The newest known manifest is the one installed.
    /// </summary>
    public static UpdateResolution Resolve(AppVersion current, UpdateManifest? fresh, UpdateManifest? remembered)
    {
        var freshKind = fresh is null ? UpdateKind.None : Decide(current, fresh);
        if (freshKind == UpdateKind.Mandatory)
        {
            return new UpdateResolution(UpdateKind.Mandatory, fresh, fresh);
        }
        if (remembered is not null && Decide(current, remembered) == UpdateKind.Mandatory)
        {
            var target = fresh is not null && fresh.Version >= remembered.Version ? fresh : remembered;
            return new UpdateResolution(UpdateKind.Mandatory, target, remembered);
        }
        return new UpdateResolution(freshKind, freshKind == UpdateKind.None ? null : fresh, null);
    }

    /// <summary>
    /// True when no automatic check succeeded within <see cref="CheckInterval"/>. A last check "in the future" (clock moved
    /// back) counts as due, so a wrong clock cannot switch the checks off.
    /// </summary>
    public static bool IsCheckDue(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        lastCheckUtc is not { } last || last > nowUtc || nowUtc - last >= CheckInterval;
}

/// <summary>Address rules of the update proxy (tools/update-proxy, a Cloudflare Worker).</summary>
public static class UpdateEndpoint
{
    /// <summary>
    /// True for an absolute https URL without placeholder characters (the template value
    /// "https://spvision-update.&lt;hesap&gt;.workers.dev" is not configured), user info, query or fragment.
    /// </summary>
    public static bool IsConfigured(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) && baseUrl.IndexOfAny(['<', '>', ' ']) < 0 &&
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && IsHttps(uri) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static Uri Manifest(string baseUrl) => Combine(baseUrl, "manifest.json");

    public static Uri Signature(string baseUrl) => Combine(baseUrl, "manifest.json.sig");

    public static Uri Download(string baseUrl) => Combine(baseUrl, "download");

    /// <summary>Only https addresses are requested or followed (the proxy redirects /download to GitHub's CDN).</summary>
    public static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host);

    static Uri Combine(string baseUrl, string path) => new(baseUrl.TrimEnd('/') + "/" + path, UriKind.Absolute);
}

/// <summary>Turkish texts of the updater.</summary>
public static class UpdateText
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public const string KeyNotConfigured = "Güncelleme imza anahtarı ayarlanmamış";

    public const string ProxyNotConfigured = "Güncelleme sunucusu ayarlanmamış";

    public const string Cancelled = "Güncelleme iptal edildi";

    /// <summary>"İndiriliyor · %42 (41,2 / 98,0 MB)"; without a known total: "İndiriliyor · 41,2 MB".</summary>
    public static string Progress(long received, long total) =>
        total > 0
            ? string.Format(Turkish, "İndiriliyor · %{0} ({1} / {2})", Percent(received, total), Megabytes(received, false), Megabytes(total, true))
            : "İndiriliyor · " + Megabytes(received, true);

    /// <summary>0–100, clamped.</summary>
    public static int Percent(long received, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(received * 100 / total, 0, 100);

    /// <summary>"Son denetim 21.10.2026 14:05" (local time) or "Henüz denetlenmedi".</summary>
    public static string LastCheck(DateTimeOffset? lastCheckUtc) =>
        lastCheckUtc is { } last ? "Son denetim " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish) : "Henüz denetlenmedi";

    /// <summary>First non-empty line of the notes, for one-line places (banner tooltip, settings row).</summary>
    public static string FirstLine(string notes)
    {
        foreach (var line in notes.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('-', '*', '#', ' ').Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return "";
    }

    static string Megabytes(long bytes, bool unit) =>
        (bytes / 1048576.0).ToString("0.0", Turkish) + (unit ? " MB" : "");
}
