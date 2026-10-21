using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SPVision.Core;

// Bakım › Kamera yazılımları › "Denetle" (reads only, never downloads or installs). It re-reads the camera table
// (ISAPI GET InputProxy/channels + /status) and says per camera whether a newer firmware is known.
//
// DS-8632NI-I8 V4.61.025 offers no per-camera online version query (GET-only probe, CLAUDE_REHBERI §9 "3. aşama"):
// onlineUpgrade/capabilities advertises isSupportUpgradeChannels, but onlineUpgrade/version ignores every channel
// parameter (channelID=1, 33 and 999 all return the recorder's own answer) and every channel-scoped onlineUpgrade path
// (InputProxy/channels/{n}/onlineUpgrade/*, onlineUpgrade/channels/*, …) is 403 notSupport. So what SPVision can say:
// offline; a newer version of the same model already runs on another camera of this recorder; or — when an online
// source per model is given (none on this recorder) — up to date / newer available. Anything else is "Denetlenemedi".

/// <summary>Orders Hikvision firmware versions: "V5.5.800" &lt; "V5.7.12" &lt; "V5.7.20" &lt; "V5.8.4".</summary>
public static class FirmwareVersionOrder
{
    static readonly Regex Numbers = new(@"^[Vv]?(\d{1,9}(?:\.\d{1,9})*)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Dotted numbers compared numerically (a missing part counts as 0), then the build date. Null when either version
    /// is not a dotted number ("unknown", not older or newer). Equal numbers with an unknown build date compare equal.
    /// </summary>
    public static int? Compare(FirmwareVersion? a, FirmwareVersion? b)
    {
        if (Parts(a) is not { } left || Parts(b) is not { } right)
        {
            return null;
        }
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length ? left[i] : 0;
            var y = i < right.Length ? right[i] : 0;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }
        return a!.BuildDate is { } da && b!.BuildDate is { } db ? da.CompareTo(db) switch { < 0 => -1, > 0 => 1, _ => 0 } : 0;
    }

    /// <summary><paramref name="candidate"/> is known to be newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(FirmwareVersion? candidate, FirmwareVersion? current) => Compare(candidate, current) > 0;

    static long[]? Parts(FirmwareVersion? version)
    {
        if (version is null || Numbers.Match(version.Version.Trim()) is not { Success: true } match)
        {
            return null;
        }
        return match.Groups[1].Value.Split('.').Select(p => long.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();
    }
}

public enum CameraCheckState
{
    /// <summary>"—": Denetle has not run since the table was read.</summary>
    NotChecked,
    UpToDate,
    NewVersion,

    /// <summary>"Denetlenemedi": online, but no source tells whether a newer firmware exists.</summary>
    Unchecked,
    Offline,
}

public enum NewerSource
{
    /// <summary>An online source (Hikvision's server through the recorder) named the newer version.</summary>
    Online,

    /// <summary>Another camera of the same model on this recorder already runs the newer version.</summary>
    OtherCamera,
}

/// <summary>One camera's line of the YAZILIM column.</summary>
public sealed record CameraCheckResult(int Channel, CameraCheckState State, FirmwareVersion? Newer = null, NewerSource? Source = null,
    int? SourceChannel = null)
{
    public string Badge => State switch
    {
        CameraCheckState.UpToDate => "Güncel",
        CameraCheckState.NewVersion => "Güncelleme var · " + (Newer?.Version ?? "?"),
        CameraCheckState.Unchecked => "Denetlenemedi",
        CameraCheckState.Offline => "Çevrimdışı",
        _ => "—",
    };

    /// <summary>Tooltip of the cell.</summary>
    public string Detail => State switch
    {
        CameraCheckState.UpToDate => "Hikvision sunucusunda bu model için daha yeni bir yazılım yok.",
        CameraCheckState.NewVersion when Source == NewerSource.OtherCamera => string.Create(CultureInfo.InvariantCulture,
            $"Aynı modeldeki kanal {SourceChannel} kamerasında {Newer?.Text} yüklü. Hikvision sitesinde daha yenisi de olabilir. SPVision dosyayı indirmez."),
        CameraCheckState.NewVersion => $"Hikvision sunucusunda yeni sürüm var: {Newer?.Text}. SPVision dosyayı indirmez: Hikvision sitesinden indirip “Dosyadan yükle” ile seçin.",
        CameraCheckState.Unchecked => "Denetlenemedi — Hikvision sitesinden kontrol edin: bu kayıt cihazı kameralar için çevrimiçi sürüm denetimi sunmuyor.",
        CameraCheckState.Offline => "Kamera çevrimdışı; sürümü denetlenmedi.",
        _ => "Henüz denetlenmedi; “Denetle”ye basın.",
    };
}

public enum CameraCheckOverall
{
    NotChecked,
    UpToDate,
    Updates,
    Unchecked,
}

/// <summary>Result of one "Denetle": when, and per camera (null when the camera list could not be read).</summary>
public sealed record CameraCheck(DateTimeOffset At, IReadOnlyList<CameraCheckResult>? Results, string? Error = null)
{
    public CameraCheckResult? For(int channel) => Results?.FirstOrDefault(r => r.Channel == channel);
}

public static class CameraFirmwareCheck
{
    public const string NotCheckedText = "Henüz denetlenmedi · “Denetle” kameraları yeniden okur ve daha yeni yazılım olup olmadığına bakar";

    /// <summary>
    /// Per camera, in table order. <paramref name="latestByModel"/>: the newest firmware an online source reports per
    /// model (model names compared ignoring case); null or a missing model = no online source for it.
    /// </summary>
    public static IReadOnlyList<CameraCheckResult> Evaluate(IReadOnlyList<CameraFirmware> cameras,
        IReadOnlyDictionary<string, FirmwareVersion>? latestByModel = null)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        var online = latestByModel is null
            ? new Dictionary<string, FirmwareVersion>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FirmwareVersion>(latestByModel, StringComparer.OrdinalIgnoreCase);

        // Newest installed version per model (offline cameras count: their version is what the recorder last saw).
        var newest = new Dictionary<string, CameraFirmware>(StringComparer.OrdinalIgnoreCase);
        foreach (var camera in cameras)
        {
            if (camera.Model is { } model && camera.Firmware is not null &&
                (!newest.TryGetValue(model, out var best) || FirmwareVersionOrder.IsNewer(camera.Firmware, best.Firmware)))
            {
                newest[model] = camera;
            }
        }

        var results = new List<CameraCheckResult>(cameras.Count);
        foreach (var camera in cameras)
        {
            results.Add(Evaluate(camera, online, newest));
        }
        return results;
    }

    static CameraCheckResult Evaluate(CameraFirmware camera, Dictionary<string, FirmwareVersion> online, Dictionary<string, CameraFirmware> newest)
    {
        if (camera.Online == false)
        {
            return new CameraCheckResult(camera.Channel, CameraCheckState.Offline);
        }
        if (camera.Model is not { } model || camera.Firmware is not { } current)
        {
            return new CameraCheckResult(camera.Channel, CameraCheckState.Unchecked);
        }
        var latest = online.GetValueOrDefault(model);
        var other = newest.GetValueOrDefault(model);
        var otherNewer = other is not null && other.Channel != camera.Channel && FirmwareVersionOrder.IsNewer(other.Firmware, current);
        if (latest is not null && FirmwareVersionOrder.IsNewer(latest, current) &&
            (!otherNewer || FirmwareVersionOrder.Compare(latest, other!.Firmware) >= 0))
        {
            return new CameraCheckResult(camera.Channel, CameraCheckState.NewVersion, latest, NewerSource.Online);
        }
        if (otherNewer)
        {
            return new CameraCheckResult(camera.Channel, CameraCheckState.NewVersion, other!.Firmware, NewerSource.OtherCamera, other.Channel);
        }
        if (latest is not null && FirmwareVersionOrder.Compare(latest, current) is not null)
        {
            return new CameraCheckResult(camera.Channel, CameraCheckState.UpToDate);
        }
        return new CameraCheckResult(camera.Channel, CameraCheckState.Unchecked);
    }

    public static CameraCheckOverall Overall(CameraCheck? check)
    {
        if (check?.Results is not { } results)
        {
            return check is null ? CameraCheckOverall.NotChecked : CameraCheckOverall.Unchecked;
        }
        if (results.Any(r => r.State == CameraCheckState.NewVersion))
        {
            return CameraCheckOverall.Updates;
        }
        return results.Any(r => r.State == CameraCheckState.UpToDate) && !results.Any(r => r.State == CameraCheckState.Unchecked)
            ? CameraCheckOverall.UpToDate
            : CameraCheckOverall.Unchecked;
    }

    /// <summary>Header badge of the card (mirrors the recorder card's badge).</summary>
    public static string OverallBadge(CameraCheckOverall overall) => overall switch
    {
        CameraCheckOverall.UpToDate => "Güncel",
        CameraCheckOverall.Updates => "Güncelleme var",
        CameraCheckOverall.Unchecked => "Denetlenemedi",
        _ => "Denetlenmedi",
    };

    /// <summary>
    /// "Son denetim 20:14 · 3 kamerada güncelleme var · 2 kamera çevrimdışı" / "Son denetim 20:14 · Tüm kameralar güncel".
    /// The date is added when the check was not today (<paramref name="now"/>).
    /// </summary>
    public static string Summary(CameraCheck? check, DateTimeOffset now)
    {
        if (check is null)
        {
            return NotCheckedText;
        }
        var at = check.At.Date == now.Date
            ? check.At.ToString("HH:mm", CultureInfo.InvariantCulture)
            : check.At.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var head = "Son denetim " + at;
        if (check.Results is not { } results)
        {
            return head + " · kamera listesi okunamadı" + (check.Error is { Length: > 0 } e ? ": " + e : "");
        }
        if (results.Count == 0)
        {
            return head + " · kayıt cihazında IP kamera yok";
        }
        int Count(CameraCheckState state) => results.Count(r => r.State == state);
        var (updates, upToDate, notChecked, offline) =
            (Count(CameraCheckState.NewVersion), Count(CameraCheckState.UpToDate), Count(CameraCheckState.Unchecked), Count(CameraCheckState.Offline));
        var parts = new List<string> { head };
        if (updates > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{updates} kamerada güncelleme var"));
        }
        else if (upToDate > 0 && notChecked == 0)
        {
            parts.Add(offline > 0 ? "Tüm çevrimiçi kameralar güncel" : "Tüm kameralar güncel");
        }
        if (notChecked > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{notChecked} kamerada sürüm denetlenemedi"));
        }
        if (offline > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{offline} kamera çevrimdışı"));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Models whose cameras could not be checked online (for the per-model Hikvision links), sorted.</summary>
    public static IReadOnlyList<string> ModelsToLookUp(IReadOnlyList<CameraFirmware> cameras, IReadOnlyList<CameraCheckResult> results)
    {
        var byChannel = results.ToDictionary(r => r.Channel);
        return cameras
            .Where(c => c.Model is not null && byChannel.TryGetValue(c.Channel, out var r) &&
                        (r.State == CameraCheckState.Unchecked || r is { State: CameraCheckState.NewVersion, Source: NewerSource.OtherCamera }))
            .Select(c => c.Model!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}

public static class CameraFirmwareReader
{
    /// <summary>
    /// The Bakım camera table: GET InputProxy/channels, then /status (when that fails the online state is unknown).
    /// Reads only; used by the tab's first load and by "Denetle".
    /// </summary>
    public static Task<Read<List<CameraFirmware>>> ReadAsync(IConfigTransport transport, CancellationToken ct = default) =>
        RecorderReader.TryCallAsync(async () =>
        {
            var channels = await transport.GetConfigAsync(MaintenancePaths.Channels, ct);
            XDocument? status = null;
            try
            {
                status = await transport.GetConfigAsync(MaintenancePaths.ChannelStatus, ct);
            }
            catch (IsapiException)
            {
                // Online state unknown: the table says "—" and no camera can be selected.
            }
            return CameraFirmware.ParseList(channels, status);
        });
}
