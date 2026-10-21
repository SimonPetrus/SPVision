using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Bakım (phase 3): what the tab reads (recorder and camera firmware, the recorder's online
// upgrade check), the firmware file the user picks, and the exact maintenance entries of the write policy. Reading is
// plain GET; the three risky operations (recorder firmware, camera firmware, factory reset) go through
// MaintenanceWrites → RiskyConfirmDialog (typed word) → ConfirmedRiskyWrite, see RiskyWrites.Maintenance.cs.

/// <summary>Endpoints of the Bakım tab. Everything here is a read except <see cref="FactoryReset"/> (risky, exact).</summary>
public static class MaintenancePaths
{
    public const string DeviceInfo = "ISAPI/System/deviceInfo";
    public const string Channels = "ISAPI/ContentMgmt/InputProxy/channels";
    public const string ChannelStatus = "ISAPI/ContentMgmt/InputProxy/channels/status";

    /// <summary>XML_OnlineUpgradeCap: 200 = the recorder supports the online check.</summary>
    public const string OnlineUpgradeCapabilities = "ISAPI/System/onlineUpgrade/capabilities";

    /// <summary>XML_OnlineUpgradeServer: <c>connectStatus</c> = the recorder reaches Hikvision's upgrade server.</summary>
    public const string OnlineUpgradeServer = "ISAPI/System/onlineUpgrade/server";

    /// <summary>
    /// XML_OnlineUpgradeVersion (GET; without the parameter the recorder answers 400 badParameters). check=true asks the
    /// recorder to look on Hikvision's server now, check=false returns its last result. Neither downloads or installs.
    /// </summary>
    public static string OnlineUpgradeVersion(bool askServer) =>
        askServer ? "ISAPI/System/onlineUpgrade/version?check=true" : "ISAPI/System/onlineUpgrade/version?check=false";

    /// <summary>PUT, no body (SDK General Developer Guide E.177). The only two query variants SPVision ever sends.</summary>
    public static string FactoryReset(FactoryResetMode mode) => mode switch
    {
        FactoryResetMode.Basic => "ISAPI/System/factoryReset?mode=basic",
        FactoryResetMode.Full => "ISAPI/System/factoryReset?mode=full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Hikvision's firmware download page (opened in the browser; SPVision never downloads firmware itself).</summary>
    public const string HikvisionFirmwarePage = "https://www.hikvision.com/en/support/download/firmware/";
}

/// <summary>
/// The exact maintenance entries of <see cref="IsapiWritePolicy.CheckRisky"/>: operation + method + target, compared
/// ordinally as whole strings (no pattern, so no case, slash, escape or extra-parameter variant passes). Firmware goes
/// through the HCNetSDK (documented upgrade APIs), so its "method" is <see cref="SdkMethod"/> and its target names the
/// SDK call; <see cref="IsapiClient.SendRiskyAsync"/> refuses that method.
/// </summary>
public static class MaintenancePolicy
{
    public const string SdkMethod = "SDK";

    /// <summary>NET_DVR_Upgrade_V50 with ENUM_UPGRADE_DVR (SDK General Developer Guide §4.2, §12.7.8).</summary>
    public const string RecorderFirmwareCall = "NET_DVR_Upgrade_V50/ENUM_UPGRADE_DVR";

    /// <summary>NET_DVR_StartRemoteConfig with NET_DVR_UPGRADE_IPC (6174) and NET_DVR_UPGRADE_IPC_PARAM (official ClientDemo DlgUpgradeIPC).</summary>
    public const string CameraFirmwareCall = "NET_DVR_StartRemoteConfig/NET_DVR_UPGRADE_IPC";

    /// <summary>Typed confirmation of a firmware upgrade (Turkish upper-casing, like <see cref="RiskyWrites.FormatWord"/>).</summary>
    public const string UpdateWord = "GÜNCELLE";

    /// <summary>Typed confirmation of a factory reset.</summary>
    public const string ResetWord = "SIFIRLA";

    /// <summary>Upper bound of one camera batch (NET_DVR_UPGRADE_IPC_PARAM holds 64 channels; SPVision sends one at a time).</summary>
    public const int MaxCameras = 64;

    public static readonly IReadOnlyList<(RiskyOperation Operation, string Method, string Target)> Entries =
    [
        (RiskyOperation.RecorderFirmware, SdkMethod, RecorderFirmwareCall),
        (RiskyOperation.CameraFirmware, SdkMethod, CameraFirmwareCall),
        (RiskyOperation.FactoryReset, "PUT", "ISAPI/System/factoryReset?mode=basic"),
        (RiskyOperation.FactoryReset, "PUT", "ISAPI/System/factoryReset?mode=full"),
    ];

    public static bool Handles(RiskyOperation operation) =>
        operation is RiskyOperation.RecorderFirmware or RiskyOperation.CameraFirmware or RiskyOperation.FactoryReset;

    /// <summary>Null when <paramref name="target"/> is exactly an entry of <paramref name="operation"/> with that method.</summary>
    public static string? Check(RiskyOperation operation, string? method, string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return "Yazma adresi boş.";
        }
        return Entries.Any(e => e.Operation == operation && string.Equals(e.Method, method, StringComparison.Ordinal)
                                && string.Equals(e.Target, target, StringComparison.Ordinal))
            ? null
            : "Bu işlem izin verilen yazma listesinde değil.";
    }
}

public enum FactoryResetMode
{
    /// <summary>mode=basic.</summary>
    Basic,

    /// <summary>mode=full.</summary>
    Full,
}

/// <summary>What each reset mode does, worded after the SDK General Developer Guide (E.177 /ISAPI/System/factoryReset?mode=).</summary>
public static class FactoryResetText
{
    public static string Name(FactoryResetMode mode) => mode == FactoryResetMode.Basic ? "Temel sıfırlama" : "Tam sıfırlama";

    /// <summary>Help line of the row.</summary>
    public static string Help(FactoryResetMode mode) => mode == FactoryResetMode.Basic
        ? "Ağ ayarları korunur, diğer tüm ayarlar varsayılana döner (Hikvision: mode=basic). Belge kullanıcı hesaplarının korunacağını söylemez."
        : "Ağ ayarları, kullanıcılar ve yönetici şifresi dahil tüm ayarlar varsayılana döner (mode=full); cihaz yeniden etkinleştirilmelidir.";

    /// <summary>"Korunur / Sıfırlanır" lines of the confirmation.</summary>
    public static IReadOnlyList<string> Lines(FactoryResetMode mode) => mode == FactoryResetMode.Basic
        ?
        [
            "Mod: temel (mode=basic)",
            "Korunur: ağ ayarları (IP adresi, maske, ağ geçidi, portlar)",
            "Sıfırlanır: diğer tüm ayarlar — kamera kanalları, kayıt programı, olaylar, akış şifreleme, Hik-Connect",
            "Kullanıcılar: Hikvision belgesi korunacağını söylemez; yönetici şifresi sıfırlanırsa cihaz yeniden etkinleştirme ister",
        ]
        :
        [
            "Mod: tam (mode=full)",
            "Korunur: hiçbir ayar",
            "Sıfırlanır: ağ ayarları, kullanıcılar ve yönetici şifresi dahil tüm ayarlar; akış şifreleme, Hik-Connect",
            "Sonra: cihaz etkin olmayan duruma geçer, IP adresi fabrika değerine döner; yeniden etkinleştirmek gerekir",
        ];
}

/// <summary>"V5.5.800 build 210628" → version + build date (yyMMdd).</summary>
public sealed record FirmwareVersion(string Version, DateOnly? BuildDate)
{
    static readonly Regex Build = new(@"build\s*(\d{6})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"V4.61.025 · 05.09.2022" (date only when known).</summary>
    public string Text => BuildDate is { } date
        ? Version + " · " + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        : Version;

    /// <summary>Version + build date ("V5.5.800 20210628"): a rebuild of the same version number still compares as new.</summary>
    public string Key => BuildDate is { } date ? Version + " " + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : Version;

    /// <summary>Null for an empty version. <paramref name="releasedDate"/> is deviceInfo's separate "build 220905".</summary>
    public static FirmwareVersion? Parse(string? version, string? releasedDate = null)
    {
        var text = version?.Trim() ?? "";
        if (text.Length == 0)
        {
            return null;
        }
        var date = ParseBuild(releasedDate) ?? ParseBuild(text);
        var at = text.IndexOf(" build", StringComparison.OrdinalIgnoreCase);
        return new FirmwareVersion(at > 0 ? text[..at].Trim() : text, date);
    }

    static DateOnly? ParseBuild(string? text)
    {
        if (text is null || Build.Match(text) is not { Success: true } match)
        {
            return null;
        }
        var digits = match.Groups[1].Value;
        var year = 2000 + int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(digits.Substring(2, 2), CultureInfo.InvariantCulture);
        var day = int.Parse(digits[4..], CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }
}

/// <summary>The recorder's model and firmware (ISAPI/System/deviceInfo). Serial, MAC and device id are not kept.</summary>
public sealed record RecorderFirmware(string? Model, FirmwareVersion? Firmware, string? DeviceType)
{
    public static RecorderFirmware Parse(XDocument doc)
    {
        var root = doc.Root!;
        return new RecorderFirmware(
            Text(root, "model"),
            FirmwareVersion.Parse(Text(root, "firmwareVersion"), Text(root, "firmwareReleasedDate")),
            Text(root, "deviceType"));
    }

    internal static string? Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
}

/// <summary>
/// One IP camera of the recorder as the Bakım table shows it: channel, name, model, firmware, online, protocol (from
/// ISAPI/ContentMgmt/InputProxy/channels + /status). The connection record's address, user name and serial are not kept.
/// </summary>
public sealed record CameraFirmware(int Channel, string Name, string? Model, FirmwareVersion? Firmware, bool? Online, string? Protocol)
{
    /// <summary>The recorder upgrades only cameras it reaches over the Hikvision protocol, and only online ones.</summary>
    public bool Upgradable => Online == true && (Protocol is null || string.Equals(Protocol, "HIKVISION", StringComparison.OrdinalIgnoreCase));

    public string Label => Name.Length > 0 ? Name : string.Create(CultureInfo.InvariantCulture, $"Kamera {Channel}");

    public static List<CameraFirmware> ParseList(XDocument channels, XDocument? status)
    {
        var online = new Dictionary<int, bool>();
        if (status?.Root is { } statusRoot)
        {
            foreach (var item in statusRoot.Descendants().Where(e => e.Name.LocalName == "InputProxyChannelStatus"))
            {
                if (int.TryParse(RecorderFirmware.Text(item, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    online[id] = string.Equals(RecorderFirmware.Text(item, "online"), "true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        var list = new List<CameraFirmware>();
        foreach (var channel in channels.Root!.Descendants().Where(e => e.Name.LocalName == "InputProxyChannel"))
        {
            if (!int.TryParse(RecorderFirmware.Text(channel, "id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                continue;
            }
            var source = channel.Elements().FirstOrDefault(e => e.Name.LocalName == "sourceInputPortDescriptor") ?? channel;
            list.Add(new CameraFirmware(
                id,
                RecorderFirmware.Text(channel, "name") ?? "",
                RecorderFirmware.Text(source, "model"),
                FirmwareVersion.Parse(RecorderFirmware.Text(source, "firmwareVersion")),
                online.TryGetValue(id, out var isOnline) ? isOnline : null,
                RecorderFirmware.Text(source, "proxyProtocol")));
        }
        list.Sort((a, b) => a.Channel.CompareTo(b.Channel));
        return list;
    }

    /// <summary>Distinct models of <paramref name="cameras"/> (unknown models count as one "bilinmiyor").</summary>
    public static IReadOnlyList<string> Models(IEnumerable<CameraFirmware> cameras) =>
        cameras.Select(c => c.Model ?? "model bilinmiyor").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
}

public enum OnlineCheckState
{
    Unsupported,
    ServerUnreachable,
    UpToDate,
    NewVersion,
    Failed,
}

/// <summary>Result of "Denetle". Never followed by a download or an install: the user fetches the file from Hikvision.</summary>
public sealed record OnlineCheckResult(OnlineCheckState State, string? NewVersion = null, string? ChangeLog = null, string? Error = null)
{
    public string Badge => State switch
    {
        OnlineCheckState.UpToDate => "Güncel",
        OnlineCheckState.NewVersion => "Güncelleme var",
        OnlineCheckState.Unsupported => "Desteklenmiyor",
        OnlineCheckState.ServerUnreachable => "Sunucuya ulaşılamadı",
        _ => "Denetlenemedi",
    };

    public string Text => State switch
    {
        OnlineCheckState.UpToDate => "Hikvision sunucusunda bu cihaz için daha yeni bir yazılım yok.",
        OnlineCheckState.NewVersion => $"Yeni sürüm var: {NewVersion ?? "sürüm adı bildirilmedi"}. SPVision dosyayı indirmez: Hikvision sitesinden bu modele ait dosyayı indirip “Dosyadan yükle” ile seçin.",
        OnlineCheckState.Unsupported => "Bu cihaz çevrimiçi denetimi desteklemiyor — Hikvision sitesinden dosya indirin.",
        OnlineCheckState.ServerUnreachable => "Kayıt cihazı Hikvision güncelleme sunucusuna bağlanamıyor (cihazın internet erişimi yok ya da kapalı) — Hikvision sitesinden dosya indirin.",
        _ => $"Denetlenemedi: {Error ?? "cihaz yanıt vermedi"}.",
    };

    /// <summary>The Hikvision download page is offered for every state but "Güncel".</summary>
    public bool OffersDownloadPage => State != OnlineCheckState.UpToDate;
}

/// <summary>"Denetle": capabilities → server status → version (check=true). Reads only.</summary>
public static class OnlineUpgrade
{
    public static async Task<OnlineCheckResult> CheckAsync(IConfigTransport transport, CancellationToken ct = default)
    {
        try
        {
            var caps = await transport.GetConfigAsync(MaintenancePaths.OnlineUpgradeCapabilities, ct);
            if (caps.Root?.Name.LocalName != "OnlineUpgradeCap")
            {
                return new OnlineCheckResult(OnlineCheckState.Unsupported);
            }
        }
        catch (IsapiException e) when (Unsupported(e.Status))
        {
            return new OnlineCheckResult(OnlineCheckState.Unsupported);
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException)
        {
            return new OnlineCheckResult(OnlineCheckState.Failed, Error: e.Message);
        }

        try
        {
            if (ParseServerConnected(await transport.GetConfigAsync(MaintenancePaths.OnlineUpgradeServer, ct)) == false)
            {
                return new OnlineCheckResult(OnlineCheckState.ServerUnreachable);
            }
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException)
        {
            // No server status: the version query below tells.
        }

        try
        {
            return ParseVersion(await transport.GetConfigAsync(MaintenancePaths.OnlineUpgradeVersion(askServer: true), ct));
        }
        catch (IsapiException e) when (Unsupported(e.Status))
        {
            return new OnlineCheckResult(OnlineCheckState.Unsupported);
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException)
        {
            return new OnlineCheckResult(OnlineCheckState.Failed, Error: e.Message);
        }
    }

    static bool Unsupported(HttpStatusCode? status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;

    public static bool? ParseServerConnected(XDocument doc) =>
        RecorderFirmware.Text(doc.Root!, "connectStatus") is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : null;

    public static OnlineCheckResult ParseVersion(XDocument doc)
    {
        var root = doc.Root!;
        if (root.Name.LocalName == "ResponseStatus")
        {
            var status = IsapiResponseStatus.Parse(doc);
            return status is { SubStatusCode: "notSupport" }
                ? new OnlineCheckResult(OnlineCheckState.Unsupported)
                : new OnlineCheckResult(OnlineCheckState.Failed, Error: status?.Describe() ?? "beklenmeyen yanıt");
        }
        if (root.Name.LocalName != "OnlineUpgradeVersion")
        {
            return new OnlineCheckResult(OnlineCheckState.Failed, Error: "beklenmeyen yanıt");
        }
        var available = string.Equals(RecorderFirmware.Text(root, "newVersionAvailable"), "true", StringComparison.OrdinalIgnoreCase);
        return available
            ? new OnlineCheckResult(OnlineCheckState.NewVersion, RecorderFirmware.Text(root, "newVersion"), RecorderFirmware.Text(root, "changeLog"))
            : new OnlineCheckResult(OnlineCheckState.UpToDate);
    }
}

/// <summary>A firmware file picked or confirmed for an upgrade is not usable; the message is for the user.</summary>
public sealed class FirmwareFileException(string message) : Exception(message);

/// <summary>
/// A firmware file on this PC: name, size, last write time and SHA-256 (computed by streaming, never loaded whole).
/// The confirmation shows them and binds to them: <see cref="VerifyUnchangedAsync"/> refuses a file that changed
/// between the confirmation and the upgrade.
/// </summary>
public sealed class FirmwareFile
{
    /// <summary>Hikvision recorder and camera firmware ships as "digicap.dav".</summary>
    public static readonly IReadOnlyList<string> Extensions = [".dav", ".digicap"];

    public const long MinBytes = 1L << 20;   // 1 MiB: smaller is no firmware image
    public const long MaxBytes = 512L << 20; // 512 MiB: recorder images are well below

    FirmwareFile(string path, long length, DateTime lastWriteUtc, string sha256)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Length = length;
        LastWriteUtc = lastWriteUtc;
        Sha256 = sha256;
    }

    public string Path { get; }
    public string Name { get; }
    public long Length { get; }
    public DateTime LastWriteUtc { get; }

    /// <summary>Lower-case hex.</summary>
    public string Sha256 { get; }

    public string SizeText => Size(Length);

    /// <summary>"63,2 MB" (MiB, Turkish decimal comma).</summary>
    public static string Size(long bytes) =>
        (bytes / 1048576.0).ToString("0.0", CultureInfo.GetCultureInfo("tr-TR")) + " MB";

    /// <summary>Null when a file with this name and size may be offered; otherwise why not (Turkish).</summary>
    public static string? Validate(string fileName, long length)
    {
        var extension = System.IO.Path.GetExtension(fileName);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return $"“{fileName}” bir Hikvision yazılım dosyası değil (beklenen uzantı: {string.Join(", ", Extensions)}).";
        }
        if (length < MinBytes)
        {
            return $"“{fileName}” yazılım dosyası olamayacak kadar küçük ({Size(length)}).";
        }
        if (length > MaxBytes)
        {
            return $"“{fileName}” beklenenden çok büyük ({Size(length)}; en çok {Size(MaxBytes)}).";
        }
        return null;
    }

    /// <summary>Checks name and size, then hashes the file by streaming it. Throws <see cref="FirmwareFileException"/>.</summary>
    public static async Task<FirmwareFile> OpenAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FirmwareFileException($"“{info.Name}” bulunamadı.");
        }
        if (Validate(info.Name, info.Length) is { } reason)
        {
            throw new FirmwareFileException(reason);
        }
        var (length, written) = (info.Length, info.LastWriteTimeUtc);
        var hash = await HashAsync(info.FullName, ct);
        info.Refresh();
        if (info.Length != length || info.LastWriteTimeUtc != written)
        {
            throw new FirmwareFileException($"“{info.Name}” okunurken değişti; dosyanın inmesi bitince yeniden seçin.");
        }
        return new FirmwareFile(info.FullName, length, written, hash);
    }

    /// <summary>The file is still the one that was confirmed (size, last write time and SHA-256).</summary>
    public async Task VerifyUnchangedAsync(CancellationToken ct = default)
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length != Length || info.LastWriteTimeUtc != LastWriteUtc ||
            !string.Equals(await HashAsync(Path, ct), Sha256, StringComparison.Ordinal))
        {
            throw new FirmwareFileException($"“{Name}” onaydan sonra değişti ya da silindi; güncelleme başlatılmadı.");
        }
    }

    static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FirmwareFileException($"“{System.IO.Path.GetFileName(path)}” okunamadı: {e.Message}");
        }
    }
}

/// <summary>"Web arayüzünü aç": the recorder's own web UI with the scheme and port SPVision uses for ISAPI.</summary>
public static class WebUi
{
    /// <summary>https://host/ (443 omitted), https://host:8443/, http://host/… IPv6 hosts get brackets. Throws UriFormatException for a bad host.</summary>
    public static string Address(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var host = device.Host.Trim();
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]";
        }
        return new UriBuilder(device.UseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, device.HttpPort, "/").Uri.AbsoluteUri;
    }
}
