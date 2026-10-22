using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SPVision.Core;

// Pure logic of Settings › Cihaz ayarları: parsing the recorder's ISAPI documents into form values, Turkish
// display texts, and turning "before → after" form values into ConfigWrites (see ConfigWriter). Unit tested.

public enum ReadState
{
    Ok,
    Unsupported,
    Failed,
}

/// <summary>A value read from the device, or why it is missing ("Bu kamera desteklemiyor" / "okunamadı").</summary>
public sealed record Read<T>(ReadState State, T? Value, string? Error = null)
{
    public bool Ok => State == ReadState.Ok && Value is not null;

    public static Read<T> Success(T value) => new(ReadState.Ok, value);
}

public static class RecorderReader
{
    /// <summary>GET + parse; 403/404/405/501 mean the device does not offer the setting, other errors "okunamadı".</summary>
    public static Task<Read<T>> TryAsync<T>(IConfigTransport transport, string path, Func<XDocument, T?> parse,
        CancellationToken ct = default) where T : class =>
        TryCallAsync(async () => parse(await transport.GetConfigAsync(path, ct)));

    /// <summary>Same error mapping for any read call (e.g. <see cref="IsapiClient.GetDeviceInfoAsync"/>); cancellation propagates.</summary>
    public static async Task<Read<T>> TryCallAsync<T>(Func<Task<T?>> call) where T : class
    {
        try
        {
            var value = await call();
            return value is null ? new Read<T>(ReadState.Unsupported, null) : Read<T>.Success(value);
        }
        catch (IsapiException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                                           or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return new Read<T>(ReadState.Unsupported, null, e.Message);
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException or FormatException)
        {
            return new Read<T>(ReadState.Failed, null, e.Message);
        }
    }

    /// <summary>Capabilities are optional: without them the form offers the current value only.</summary>
    public static async Task<XDocument?> TryCapabilitiesAsync(IConfigTransport transport, string path, CancellationToken ct = default)
    {
        try
        {
            return await transport.GetConfigAsync(path, ct);
        }
        catch (IsapiException)
        {
            return null;
        }
    }
}

public static class RecorderText
{
    public const string Unreadable = "okunamadı";
    public const string Unsupported = "Bu kamera desteklemiyor";
    public const string Device = "Kayıt cihazı";

    public static string OnOff(bool value) => value ? "Açık" : "Kapalı";

    public static string Time(DateTimeOffset t) => t.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"+03:00" style offset as "UTC+03:00".</summary>
    public static string Utc(TimeSpan offset) =>
        "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    /// <summary>Capacity in MB (as ISAPI reports it) → "7,28 TB" / "512 GB".</summary>
    public static string Size(long megabytes)
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        if (megabytes <= 0)
        {
            return "0 GB";
        }
        var gb = megabytes / 1024.0;
        return gb >= 1024
            ? (gb / 1024).ToString("0.00", tr) + " TB"
            : gb >= 10 ? gb.ToString("0", tr) + " GB" : gb.ToString("0.0", tr) + " GB";
    }

    public static string Seconds(int seconds) =>
        seconds >= 60 && seconds % 60 == 0
            ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + " dk"
            : seconds.ToString(CultureInfo.InvariantCulture) + " sn";
}

// ---------------------------------------------------------------- Genel bakış / Sistem

public sealed record ChannelUsage(int Used, int? Total)
{
    public string Text => Total is { } t
        ? string.Create(CultureInfo.InvariantCulture, $"{Used} / {t} kullanımda")
        : string.Create(CultureInfo.InvariantCulture, $"{Used} kullanımda");

    /// <summary>Total from InputProxy/channels/capabilities (id max), e.g. 32.</summary>
    public static int? ParseTotal(XDocument caps) =>
        caps.Root is { } root && IsapiXml.Child(root, "InputProxyChannel") is { } channel
            ? IsapiXml.IntAttribute(channel, "id", "max")
            : null;
}

/// <summary>Read-only "Güvenlik · kilitli" values. The verification code is never parsed here.</summary>
public sealed record SecurityStatus(bool? StreamEncryption, bool? HikConnect)
{
    public static SecurityStatus Parse(XDocument doc)
    {
        var root = doc.Root!;
        return new SecurityStatus(
            IsapiXml.GetBool(root, "streamEncrypteEnabled") ?? IsapiXml.GetBool(root, "streamEncryptEnabled"),
            IsapiXml.GetBool(root, "enabled"));
    }
}

public sealed record DeviceTime(string? Mode, DateTimeOffset? Local, string? TimeZone)
{
    public bool IsNtp => string.Equals(Mode, "NTP", StringComparison.OrdinalIgnoreCase);

    public static DeviceTime Parse(XDocument doc)
    {
        var root = doc.Root!;
        DateTimeOffset? local = DateTimeOffset.TryParse(IsapiXml.Get(root, "localTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : null;
        return new DeviceTime(IsapiXml.Get(root, "timeMode"), local, IsapiXml.Get(root, "timeZone"));
    }

    public string ModeText => IsNtp ? "NTP" : string.Equals(Mode, "manual", StringComparison.OrdinalIgnoreCase) ? "Elle" : Mode ?? "—";
}

/// <summary>Hikvision POSIX-style zone strings: "CST-3:00:00" is UTC+3 (the sign is inverted).</summary>
public static partial class HikTimeZone
{
    [GeneratedRegex(@"^[A-Za-z]*([+-])(\d{1,2})(?::(\d{2}))?(?::(\d{2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex ZonePattern();

    public static TimeSpan? Offset(string? zone)
    {
        if (zone is null || ZonePattern().Match(zone) is not { Success: true } m)
        {
            return null;
        }
        var span = new TimeSpan(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0, 0);
        return m.Groups[1].Value == "-" ? span : -span;
    }

    public static string Format(TimeSpan offset) =>
        string.Create(CultureInfo.InvariantCulture,
            $"CST{(offset < TimeSpan.Zero ? "+" : "-")}{(int)Math.Abs(offset.TotalHours)}:{Math.Abs(offset.Minutes):00}:00");
}

public sealed record NtpInfo(string? Server, int? IntervalMinutes)
{
    public static NtpInfo Parse(XDocument doc)
    {
        var server = doc.Root is { } root ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "NTPServer") : null;
        if (server is null)
        {
            return new NtpInfo(null, null);
        }
        var host = string.Equals(IsapiXml.Get(server, "addressingFormatType"), "ipaddress", StringComparison.OrdinalIgnoreCase)
            ? IsapiXml.Get(server, "ipAddress")
            : IsapiXml.Get(server, "hostName");
        return new NtpInfo(string.IsNullOrEmpty(host) ? null : host, IsapiXml.GetInt(server, "synchronizeInterval"));
    }

    public string Describe(DeviceTime? time)
    {
        if (time is null)
        {
            return RecorderText.Unreadable;
        }
        if (!time.IsNtp)
        {
            return "Kapalı · saat elle ayarlı";
        }
        var parts = new List<string> { "Açık" };
        if (Server is { } s)
        {
            parts.Add(s);
        }
        if (IntervalMinutes is { } i)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{i} dk'da bir"));
        }
        return string.Join(" · ", parts);
    }
}

public sealed record HddInfo(int Id, string Name, string Type, string Status, long CapacityMb, long FreeMb)
{
    public static List<HddInfo> ParseList(XDocument doc) =>
        doc.Root!.Descendants().Where(e => e.Name.LocalName == "hdd")
            .Select(e => new HddInfo(
                IsapiXml.GetInt(e, "id") ?? 0,
                IsapiXml.Get(e, "hddName") ?? "",
                IsapiXml.Get(e, "hddType") ?? "",
                IsapiXml.Get(e, "status") ?? "",
                long.TryParse(IsapiXml.Get(e, "capacity"), CultureInfo.InvariantCulture, out var c) ? c : 0,
                long.TryParse(IsapiXml.Get(e, "freeSpace"), CultureInfo.InvariantCulture, out var f) ? f : 0))
            .ToList();

    /// <summary>Recording works: ok / normal, or spun down (idle, sleeping, standby). Anything else is a problem (DiskHealth).</summary>
    public bool Healthy => IsHealthy(Status);

    public static bool IsHealthy(string status) => status.Trim().ToLowerInvariant() is "ok" or "normal" or "idle" or "sleeping" or "standby";

    public string StatusText => StatusTextOf(Status);

    public static string StatusTextOf(string status) => status.Trim().ToLowerInvariant() switch
    {
        "ok" or "normal" => "Normal",
        "idle" or "standby" => "Beklemede",
        "sleeping" => "Uykuda",
        "unformatted" => "Biçimlenmemiş",
        "formatting" or "formating" => "Biçimlendiriliyor",
        "error" or "exception" => "Hata",
        "smartfailed" => "SMART hatası",
        "mismatch" => "Uyumsuz",
        "offline" => "Çevrimdışı",
        "abnormal" => "Arızalı",
        "notexist" or "notexisted" => "Takılı değil",
        "repairing" or "reparing" => "Onarılıyor",
        "unrecordhostformatted" => "Başka cihazda biçimlendirilmiş",
        "lowspeed" => "Yavaş",
        "noready" or "notready" => "Hazır değil",
        "" => "—",
        _ => status,
    };
}

public sealed record DiskSummary(int Count, long CapacityMb, long FreeMb, string Status)
{
    public static DiskSummary Of(IReadOnlyList<HddInfo> disks) => new(
        disks.Count,
        disks.Sum(d => d.CapacityMb),
        disks.Sum(d => d.FreeMb),
        disks.Count == 0 ? "Disk yok" : disks.FirstOrDefault(d => !d.Healthy) is { } bad ? $"{bad.Name}: {bad.StatusText}" : "Normal");
}

// ---------------------------------------------------------------- Hareket

public enum MotionLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Düşük / Orta / Yüksek ↔ the device's numeric range (0-100 step 20 on this NVR: 20 / 60 / 80).</summary>
public static class MotionSensitivity
{
    public static MotionLevel FromDevice(int value, int min, int max)
    {
        if (max <= min)
        {
            return MotionLevel.Medium;
        }
        var fraction = (value - min) / (double)(max - min);
        return fraction < 0.3 ? MotionLevel.Low : fraction < 0.7 ? MotionLevel.Medium : MotionLevel.High;
    }

    public static int ToDevice(MotionLevel level, int min, int max, int step)
    {
        if (max <= min)
        {
            return min;
        }
        var fraction = level switch { MotionLevel.Low => 0.2, MotionLevel.Medium => 0.6, _ => 0.8 };
        var raw = min + fraction * (max - min);
        var s = Math.Max(1, step);
        var snapped = min + (int)Math.Round((raw - min) / s, MidpointRounding.AwayFromZero) * s;
        return Math.Clamp(snapped, min, max);
    }

    public static string Text(MotionLevel level) => level switch
    {
        MotionLevel.Low => "Düşük",
        MotionLevel.Medium => "Orta",
        _ => "Yüksek",
    };
}

public sealed record MotionSettings(bool Enabled, int? Sensitivity, int Min, int Max, int Step)
{
    public const string EnabledPath = "enabled";
    public const string SensitivityPath = "MotionDetectionLayout/sensitivityLevel";

    public MotionLevel? Level => Sensitivity is { } s ? MotionSensitivity.FromDevice(s, Min, Max) : null;

    public static MotionSettings? Parse(XDocument doc, XDocument? caps)
    {
        var root = doc.Root!;
        if (IsapiXml.GetBool(root, EnabledPath) is not { } enabled)
        {
            return null;
        }
        var capRoot = caps?.Root;
        int Cap(string attribute, int fallback) =>
            capRoot is null ? fallback : IsapiXml.IntAttribute(capRoot, SensitivityPath, attribute) ?? fallback;
        return new MotionSettings(enabled, IsapiXml.GetInt(root, SensitivityPath), Cap("min", 0), Cap("max", 100), Cap("step", 1));
    }

    public string SensitivityText(MotionLevel level) =>
        string.Create(CultureInfo.InvariantCulture, $"{MotionSensitivity.Text(level)} ({MotionSensitivity.ToDevice(level, Min, Max, Step)})");
}

// ---------------------------------------------------------------- Kayıt

public enum RecordPreset
{
    Continuous,
    MotionOnly,
    Off,
    Custom,
}

/// <summary>Whole-week presets of a record track's schedule; anything else is a custom schedule left untouched.</summary>
public static class RecordSchedule
{
    public const string BlockPath = "TrackSchedule/ScheduleBlockList/ScheduleBlock";

    static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public static string Text(RecordPreset preset) => preset switch
    {
        RecordPreset.Continuous => "Sürekli",
        RecordPreset.MotionOnly => "Yalnızca hareket",
        RecordPreset.Off => "Kapalı",
        _ => "Özel program",
    };

    public static RecordPreset? Detect(XElement track)
    {
        if (IsapiXml.Find(track, BlockPath) is not { } block)
        {
            return null;
        }
        var actions = block.Elements().Where(e => e.Name.LocalName == "ScheduleAction").ToList();
        if (actions.Count == 0 || actions.All(a => IsapiXml.GetBool(a, "Actions/Record") == false))
        {
            return RecordPreset.Off;
        }
        if (actions.Count != 7 || actions.Any(a => IsapiXml.GetBool(a, "Actions/Record") != true))
        {
            return RecordPreset.Custom;
        }
        var modes = actions.Select(a => IsapiXml.Get(a, "Actions/ActionRecordingMode")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var preset = modes is [var only]
            ? only?.ToUpperInvariant() switch { "CMR" => RecordPreset.Continuous, "MOTION" => RecordPreset.MotionOnly, _ => RecordPreset.Custom }
            : RecordPreset.Custom;
        if (preset == RecordPreset.Custom)
        {
            return preset;
        }
        var covered = new HashSet<int>();
        foreach (var action in actions)
        {
            var startDay = Array.IndexOf(Days, IsapiXml.Get(action, "ScheduleActionStartTime/DayOfWeek"));
            var endDay = Array.IndexOf(Days, IsapiXml.Get(action, "ScheduleActionEndTime/DayOfWeek"));
            var start = IsapiXml.Get(action, "ScheduleActionStartTime/TimeOfDay");
            var end = IsapiXml.Get(action, "ScheduleActionEndTime/TimeOfDay");
            var fullDay = startDay >= 0 && start == "00:00:00" &&
                          ((endDay == (startDay + 1) % 7 && end == "00:00:00") || (endDay == startDay && end == "24:00:00"));
            if (!fullDay || !covered.Add(startDay))
            {
                return RecordPreset.Custom;
            }
        }
        return covered.Count == 7 ? preset : RecordPreset.Custom;
    }

    /// <summary>
    /// Replaces the schedule actions of the track's block with the preset (same element shape the device returns);
    /// the block's own elements (GUID, type) and the rest of the track stay as they are.
    /// </summary>
    public static void ApplyPreset(XElement track, RecordPreset preset)
    {
        if (preset == RecordPreset.Custom)
        {
            throw new ConfigEditException("Özel program SPVision'dan yazılamaz.");
        }
        var block = IsapiXml.Find(track, BlockPath) ?? throw new ConfigEditException("Cihaz belgesinde kayıt programı yok.");
        var ns = block.Name.Namespace;
        block.Elements().Where(e => e.Name.LocalName == "ScheduleAction").ToList().ForEach(e => e.Remove());
        if (preset == RecordPreset.Off)
        {
            return;
        }
        var mode = preset == RecordPreset.Continuous ? "CMR" : "MOTION";
        for (var i = 0; i < 7; i++)
        {
            block.Add(new XElement(ns + "ScheduleAction",
                new XElement(ns + "id", i + 1),
                new XElement(ns + "ScheduleActionStartTime",
                    new XElement(ns + "DayOfWeek", Days[i]),
                    new XElement(ns + "TimeOfDay", "00:00:00")),
                new XElement(ns + "ScheduleActionEndTime",
                    new XElement(ns + "DayOfWeek", Days[(i + 1) % 7]),
                    new XElement(ns + "TimeOfDay", "00:00:00")),
                new XElement(ns + "ScheduleDSTEnable", "false"),
                new XElement(ns + "Description", "nothing"),
                new XElement(ns + "Actions",
                    new XElement(ns + "Record", "true"),
                    new XElement(ns + "Log", "false"),
                    new XElement(ns + "SaveImg", "false"),
                    new XElement(ns + "ActionRecordingMode", mode))));
        }
    }
}

public sealed class RecordPresetEdit(RecordPreset preset, string oldText)
    : ConfigEdit("Kayıt modu", oldText, RecordSchedule.Text(preset))
{
    public RecordPreset Preset { get; } = preset;

    public override void Apply(XElement root) => RecordSchedule.ApplyPreset(root, Preset);

    public override string? Verify(XElement root) =>
        RecordSchedule.Detect(root) is { } actual && actual == Preset ? null : RecordSchedule.Text(RecordSchedule.Detect(root) ?? RecordPreset.Custom);
}

public sealed record RecordSettings(bool? Enabled, RecordPreset? Preset, int? PreSeconds, int? PostSeconds)
{
    const string Extension = "CustomExtensionList/CustomExtension/";
    public const string EnabledPath = Extension + "enableSchedule";
    public const string PrePath = Extension + "PreRecordTimeSeconds";
    public const string PostPath = Extension + "PostRecordTimeSeconds";

    public static readonly IReadOnlyList<int> PreOptions = [0, 5, 10, 15, 20, 25, 30];
    public static readonly IReadOnlyList<int> PostOptions = [5, 10, 30, 60, 120, 300, 600];

    public static RecordSettings? Parse(XDocument doc)
    {
        var root = doc.Root!;
        var settings = new RecordSettings(IsapiXml.GetBool(root, EnabledPath), RecordSchedule.Detect(root),
            IsapiXml.GetInt(root, PrePath), IsapiXml.GetInt(root, PostPath));
        return settings is { Enabled: null, Preset: null, PreSeconds: null, PostSeconds: null } ? null : settings;
    }

    /// <summary>The standard list plus the device's current value, sorted.</summary>
    public static List<int> WithCurrent(IReadOnlyList<int> options, int? current) =>
        options.Concat(current is { } c ? [c] : []).Distinct().Order().ToList();
}

// ---------------------------------------------------------------- Kameralar

/// <summary>
/// OSD of one camera (<c>ContentMgmt/InputProxy/channels/{n}/video/overlays</c>): date/time and name on/off, and the date/time
/// look (<c>DateTimeOverlay/dateStyle</c>, <c>timeStyle</c>, <c>displayWeek</c>). Null part = not in the device's document.
/// </summary>
public sealed record OverlaySettings(bool? DateTime, bool? ChannelName, string? DateStyle = null, string? TimeStyle = null, bool? DisplayWeek = null)
{
    public const string DateTimePath = "DateTimeOverlay/enabled";
    public const string ChannelNamePath = "channelNameOverlay/enabled";
    public const string DateStylePath = "DateTimeOverlay/dateStyle";
    public const string TimeStylePath = "DateTimeOverlay/timeStyle";
    public const string DisplayWeekPath = "DateTimeOverlay/displayWeek";

    public static OverlaySettings? Parse(XDocument doc)
    {
        var root = doc.Root!;
        var settings = new OverlaySettings(IsapiXml.GetBool(root, DateTimePath), IsapiXml.GetBool(root, ChannelNamePath),
            NonEmpty(IsapiXml.Get(root, DateStylePath)), NonEmpty(IsapiXml.Get(root, TimeStylePath)), IsapiXml.GetBool(root, DisplayWeekPath));
        return settings is { DateTime: null, ChannelName: null, DateStyle: null, TimeStyle: null, DisplayWeek: null } ? null : settings;
    }

    static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// Date / time styles the device offers (the <c>opt</c> lists of <c>…/video/overlays/capabilities</c>), each containing the
/// current value. Without capabilities only the current value is offered: SPVision never guesses a style the device
/// may refuse.
/// </summary>
public sealed record OverlayOptions(IReadOnlyList<string> DateStyles, IReadOnlyList<string> TimeStyles)
{
    public static OverlayOptions Parse(XDocument? caps, OverlaySettings current)
    {
        var root = caps?.Root;
        return new OverlayOptions(
            With(root is null ? [] : IsapiXml.Options(root, OverlaySettings.DateStylePath), current.DateStyle),
            With(root is null ? [] : IsapiXml.Options(root, OverlaySettings.TimeStylePath), current.TimeStyle));
    }

    static List<string> With(List<string> options, string? current)
    {
        var list = options.Distinct(StringComparer.Ordinal).ToList();
        if (current is not null && !list.Contains(current, StringComparer.Ordinal))
        {
            list.Add(current);
        }
        return list;
    }
}

/// <summary>Turkish names of the OSD date / time styles.</summary>
public static class OverlayText
{
    /// <summary>Drop-down text with an example of <paramref name="sample"/>: "22-10-2026 (gün-ay-yıl)".</summary>
    public static string DateStyle(string style, DateTime sample)
    {
        if (Parts(style) is not { } parts)
        {
            return style;
        }
        if (parts.Chinese)
        {
            return $"Çince karakterli ({parts.Order})";
        }
        var example = parts.Pattern
            .Replace("YYYY", sample.Year.ToString("0000", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("MM", sample.Month.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("DD", sample.Day.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return $"{example} ({parts.Order})";
    }

    /// <summary>Confirmation text (no date): "gün-ay-yıl (DD-MM-YYYY)".</summary>
    public static string DateStyleShort(string? style)
    {
        if (style is null)
        {
            return RecorderText.Unreadable;
        }
        if (Parts(style) is not { } parts)
        {
            return style;
        }
        var prefix = parts.Chinese ? "Çince, " : "";
        return $"{prefix}{parts.Order} ({style})";
    }

    public static string TimeStyle(string? style) => style switch
    {
        null => RecorderText.Unreadable,
        _ when style.Equals("24hour", StringComparison.OrdinalIgnoreCase) => "24 saat",
        _ when style.Equals("12hour", StringComparison.OrdinalIgnoreCase) => "12 saat (ÖÖ/ÖS)",
        _ => style,
    };

    /// <summary>"DD-MM-YYYY" / "CHR-YYYY/MM/DD": the pattern, the day-month-year order in Turkish and the Chinese flag; null if unknown.</summary>
    static (string Pattern, string Order, bool Chinese)? Parts(string style)
    {
        var chinese = style.StartsWith("CHR-", StringComparison.Ordinal);
        var pattern = chinese ? style[4..] : style;
        var tokens = new List<(int Index, string Name)>();
        foreach (var (token, name) in new[] { ("YYYY", "yıl"), ("MM", "ay"), ("DD", "gün") })
        {
            var index = pattern.IndexOf(token, StringComparison.Ordinal);
            if (index < 0 || pattern.IndexOf(token, index + token.Length, StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            tokens.Add((index, name));
        }
        var rest = pattern.Replace("YYYY", "", StringComparison.Ordinal).Replace("MM", "", StringComparison.Ordinal).Replace("DD", "", StringComparison.Ordinal);
        if (rest.Any(c => c is not ('-' or '/' or '.' or ' ')))
        {
            return null;
        }
        var separator = chinese ? "/" : "-";
        return (pattern, string.Join(separator, tokens.OrderBy(t => t.Index).Select(t => t.Name)), chinese);
    }
}

public sealed record ColorRange(int Min, int Max)
{
    public static readonly ColorRange Default = new(0, 100);
}

public sealed record ImageColor(int? Brightness, int? Contrast, int? Saturation, ColorRange Range)
{
    public const string BrightnessPath = "Color/brightnessLevel";
    public const string ContrastPath = "Color/contrastLevel";
    public const string SaturationPath = "Color/saturationLevel";

    public static ImageColor? Parse(XDocument doc, XDocument? caps)
    {
        var root = doc.Root!;
        var range = caps?.Root is { } c && IsapiXml.IntAttribute(c, BrightnessPath, "min") is { } min &&
                    IsapiXml.IntAttribute(c, BrightnessPath, "max") is { } max && max > min
            ? new ColorRange(min, max)
            : ColorRange.Default;
        var color = new ImageColor(IsapiXml.GetInt(root, BrightnessPath), IsapiXml.GetInt(root, ContrastPath),
            IsapiXml.GetInt(root, SaturationPath), range);
        return color is { Brightness: null, Contrast: null, Saturation: null } ? null : color;
    }
}

public sealed record StreamSettings(string Codec, int Width, int Height, int FrameRate, int Bitrate, string BitratePath)
{
    public const string CodecPath = "Video/videoCodecType";
    public const string WidthPath = "Video/videoResolutionWidth";
    public const string HeightPath = "Video/videoResolutionHeight";
    public const string FrameRatePath = "Video/maxFrameRate";

    public static StreamSettings? Parse(XDocument doc)
    {
        var root = doc.Root!;
        if (IsapiXml.Child(root, "Video") is null || IsapiXml.GetInt(root, WidthPath) is not { } width ||
            IsapiXml.GetInt(root, HeightPath) is not { } height)
        {
            return null;
        }
        // VBR limits the upper bit rate, CBR uses the constant one; only the one in use is offered.
        var cbr = string.Equals(IsapiXml.Get(root, "Video/videoQualityControlType"), "CBR", StringComparison.OrdinalIgnoreCase);
        var bitratePath = cbr ? "Video/constantBitRate" : "Video/vbrUpperCap";
        return new StreamSettings(IsapiXml.Get(root, CodecPath) ?? "", width, height,
            IsapiXml.GetInt(root, FrameRatePath) ?? 0, IsapiXml.GetInt(root, bitratePath) ?? 0, bitratePath);
    }

    public string ResolutionText => ResolutionTextOf(Width, Height);

    public static string ResolutionTextOf(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width}×{height}");

    /// <summary>ISAPI maxFrameRate is fps × 100; 0 = full frame rate.</summary>
    public static string FrameRateText(int value) => value switch
    {
        0 => "Tam kare hızı",
        50 => "1/2 fps",
        25 => "1/4 fps",
        12 => "1/8 fps",
        6 => "1/16 fps",
        _ when value % 100 == 0 => (value / 100).ToString(CultureInfo.InvariantCulture) + " fps",
        _ => (value / 100.0).ToString("0.##", CultureInfo.GetCultureInfo("tr-TR")) + " fps",
    };

    public static string BitrateText(int kbps) => kbps.ToString(CultureInfo.InvariantCulture) + " kbps";
}

/// <summary>Device-supported choices for one stream; each list contains the current value.</summary>
public sealed record StreamOptions(IReadOnlyList<string> Codecs, IReadOnlyList<(int Width, int Height)> Resolutions,
    IReadOnlyList<int> FrameRates, IReadOnlyList<int> Bitrates)
{
    static readonly int[] StandardBitrates = [256, 512, 768, 1024, 1536, 2048, 3072, 4096, 6144, 8192, 12288, 16384];

    public static StreamOptions Parse(XDocument? caps, StreamSettings current)
    {
        var root = caps?.Root;
        var codecs = root is null ? [] : IsapiXml.Options(root, StreamSettings.CodecPath).Distinct().ToList();
        if (!codecs.Contains(current.Codec) && current.Codec.Length > 0)
        {
            codecs.Add(current.Codec);
        }

        // Width and height option lists are paired by position (1280,1920,2560 / 720,1080,1440).
        var resolutions = new List<(int, int)>();
        if (root is not null)
        {
            var widths = IsapiXml.Options(root, StreamSettings.WidthPath);
            var heights = IsapiXml.Options(root, StreamSettings.HeightPath);
            if (widths.Count == heights.Count)
            {
                for (var i = 0; i < widths.Count; i++)
                {
                    if (int.TryParse(widths[i], CultureInfo.InvariantCulture, out var w) && int.TryParse(heights[i], CultureInfo.InvariantCulture, out var h))
                    {
                        resolutions.Add((w, h));
                    }
                }
            }
        }
        if (!resolutions.Contains((current.Width, current.Height)))
        {
            resolutions.Add((current.Width, current.Height));
        }
        resolutions = resolutions.Distinct().OrderBy(r => r.Item1 * r.Item2).ToList();

        var rates = root is null
            ? []
            : IsapiXml.Options(root, StreamSettings.FrameRatePath)
                .Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : -1).Where(v => v >= 0).Distinct().ToList();
        if (!rates.Contains(current.FrameRate))
        {
            rates.Add(current.FrameRate);
        }

        var min = root is null ? null : IsapiXml.IntAttribute(root, current.BitratePath, "min");
        var max = root is null ? null : IsapiXml.IntAttribute(root, current.BitratePath, "max");
        var bitrates = min is { } lo && max is { } hi ? StandardBitrates.Where(b => b >= lo && b <= hi).ToList() : [];
        if (!bitrates.Contains(current.Bitrate))
        {
            bitrates.Add(current.Bitrate);
        }
        bitrates.Sort();

        return new StreamOptions(codecs, resolutions, rates, bitrates);
    }
}

/// <summary>What the Kameralar tab shows for one camera (null part = "Bu kamera desteklemiyor").</summary>
public sealed record CameraSettings(string? Name, OverlaySettings? Overlay, ImageColor? Color, StreamSettings? Main, StreamSettings? Sub);

// ---------------------------------------------------------------- Sistem: saat eşitleme

/// <summary>"Bilgisayarla eşitle": manual mode, this PC's local time and UTC offset, taken when the write runs.</summary>
public sealed class TimeSyncEdit(Func<DateTimeOffset> clock, string oldText)
    : ConfigEdit("Tarih ve saat", oldText, "bilgisayar saati · elle")
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(30);

    public override void Apply(XElement root)
    {
        var now = clock();
        if (IsapiXml.Child(root, "timeMode") is not null)
        {
            IsapiXml.Set(root, "timeMode", "manual");
        }
        IsapiXml.Set(root, "localTime", FormatLocal(now));
        if (IsapiXml.Child(root, "timeZone") is { } zone && HikTimeZone.Offset(zone.Value) != now.Offset)
        {
            zone.Value = HikTimeZone.Format(now.Offset);
        }
    }

    public override string? Verify(XElement root)
    {
        var time = DeviceTime.Parse(new XDocument(new XElement(root)));
        var mode = IsapiXml.Child(root, "timeMode") is null || string.Equals(time.Mode, "manual", StringComparison.OrdinalIgnoreCase);
        var ok = mode && time.Local is { } t && (t - clock()).Duration() <= Tolerance;
        return ok ? null : $"{(time.Local is { } l ? RecorderText.Time(l) : "—")} · {time.ModeText}";
    }

    public static string FormatLocal(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------- Changes → writes

/// <summary>Builds the writes of each tab from the values last read ("before") and the form ("after").</summary>
public static class RecorderPlan
{
    public static ConfigWrite? Motion(int channel, string target, MotionSettings before, bool enabled, MotionLevel? level)
    {
        var edits = new List<ConfigEdit>();
        if (enabled != before.Enabled)
        {
            edits.Add(new SetValueEdit("Hareket algılama", MotionSettings.EnabledPath, IsapiXml.Bool(enabled),
                RecorderText.OnOff(before.Enabled), RecorderText.OnOff(enabled)));
        }
        if (level is { } l && before.Sensitivity is { } current && l != before.Level)
        {
            var value = MotionSensitivity.ToDevice(l, before.Min, before.Max, before.Step);
            edits.Add(new SetValueEdit("Hassasiyet", MotionSettings.SensitivityPath, value.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{MotionSensitivity.Text(before.Level!.Value)} ({current})"),
                before.SensitivityText(l)));
        }
        return edits.Count == 0 ? null : new ConfigWrite(IsapiPaths.Motion(channel), target, edits);
    }

    /// <summary>A custom schedule is only replaced when the user picked a preset (<paramref name="preset"/> differs).</summary>
    public static ConfigWrite? Record(int channel, string target, RecordSettings before, bool? enabled, RecordPreset? preset,
        int? preSeconds, int? postSeconds)
    {
        var edits = new List<ConfigEdit>();
        if (enabled is { } e && before.Enabled is { } was && e != was)
        {
            edits.Add(new SetValueEdit("Kayıt", RecordSettings.EnabledPath, IsapiXml.Bool(e), RecorderText.OnOff(was), RecorderText.OnOff(e)));
        }
        if (preset is { } p && p != RecordPreset.Custom && before.Preset is { } old && p != old)
        {
            edits.Add(new RecordPresetEdit(p, RecordSchedule.Text(old)));
        }
        if (preSeconds is { } pre && before.PreSeconds is { } oldPre && pre != oldPre)
        {
            edits.Add(new SetValueEdit("Olay öncesi kayıt", RecordSettings.PrePath, pre.ToString(CultureInfo.InvariantCulture),
                RecorderText.Seconds(oldPre), RecorderText.Seconds(pre)));
        }
        if (postSeconds is { } post && before.PostSeconds is { } oldPost && post != oldPost)
        {
            edits.Add(new SetValueEdit("Olay sonrası kayıt", RecordSettings.PostPath, post.ToString(CultureInfo.InvariantCulture),
                RecorderText.Seconds(oldPost), RecorderText.Seconds(post)));
        }
        return edits.Count == 0 ? null : new ConfigWrite(IsapiPaths.RecordTrack(channel), target, edits);
    }

    public static List<ConfigWrite> Camera(int channel, string target, CameraSettings before, CameraSettings after)
    {
        // The camera name is not written here (ISAPI): the recorder keeps it in the IP channel record
        // (InputProxy/channels/{n}) together with the camera's address and login, and that record's password is never
        // returned by a GET, so writing the record back could leave the camera without its password (offline, not
        // recording). Renames go through the SDK picture configuration instead (ChannelRenameWriter, name bytes only).
        var writes = new List<ConfigWrite>();

        if (before.Overlay is { } o && after.Overlay is { } n)
        {
            var edits = new List<ConfigEdit>();
            AddBool(edits, "Ekranda tarih / saat", OverlaySettings.DateTimePath, o.DateTime, n.DateTime);
            AddBool(edits, "Ekranda kamera adı", OverlaySettings.ChannelNamePath, o.ChannelName, n.ChannelName);
            if (o.DateStyle is { } oldDate && n.DateStyle is { } newDate && !string.Equals(oldDate, newDate, StringComparison.Ordinal))
            {
                edits.Add(new SetValueEdit("Tarih biçimi", OverlaySettings.DateStylePath, newDate,
                    OverlayText.DateStyleShort(oldDate), OverlayText.DateStyleShort(newDate)));
            }
            if (o.TimeStyle is { } oldTime && n.TimeStyle is { } newTime && !string.Equals(oldTime, newTime, StringComparison.Ordinal))
            {
                edits.Add(new SetValueEdit("Saat biçimi", OverlaySettings.TimeStylePath, newTime,
                    OverlayText.TimeStyle(oldTime), OverlayText.TimeStyle(newTime)));
            }
            AddBool(edits, "Haftanın günü", OverlaySettings.DisplayWeekPath, o.DisplayWeek, n.DisplayWeek);
            Add(writes, IsapiPaths.Overlays(channel), target, edits);
        }

        if (before.Color is { } c && after.Color is { } d)
        {
            var edits = new List<ConfigEdit>();
            AddInt(edits, "Parlaklık", ImageColor.BrightnessPath, c.Brightness, d.Brightness);
            AddInt(edits, "Kontrast", ImageColor.ContrastPath, c.Contrast, d.Contrast);
            AddInt(edits, "Doygunluk", ImageColor.SaturationPath, c.Saturation, d.Saturation);
            Add(writes, IsapiPaths.Image(channel), target, edits);
        }

        AddStream(writes, channel, target, StreamQuality.Main, "Ana akış", before.Main, after.Main);
        AddStream(writes, channel, target, StreamQuality.Sub, "Alt akış", before.Sub, after.Sub);
        return writes;
    }

    public static List<ConfigWrite> System(string? nameBefore, string? nameAfter, DeviceTime? time, bool syncTime, Func<DateTimeOffset> clock)
    {
        var writes = new List<ConfigWrite>();
        if (nameBefore is not null && nameAfter is not null && nameAfter != nameBefore)
        {
            writes.Add(new ConfigWrite(IsapiPaths.DeviceInfo, RecorderText.Device,
                [new SetValueEdit("Cihaz adı", "deviceName", nameAfter, nameBefore, nameAfter)]));
        }
        if (syncTime && time is not null)
        {
            var old = $"{(time.Local is { } t ? RecorderText.Time(t) : "—")} · {time.ModeText}";
            writes.Add(new ConfigWrite(IsapiPaths.Time, RecorderText.Device, [new TimeSyncEdit(clock, old)]));
        }
        return writes;
    }

    static void AddStream(List<ConfigWrite> writes, int channel, string target, StreamQuality quality, string name,
        StreamSettings? before, StreamSettings? after)
    {
        if (before is null || after is null)
        {
            return;
        }
        var edits = new List<ConfigEdit>();
        if ((after.Width, after.Height) != (before.Width, before.Height))
        {
            edits.Add(new SetValueEdit($"{name} çözünürlüğü",
                [(StreamSettings.WidthPath, Inv(after.Width)), (StreamSettings.HeightPath, Inv(after.Height))],
                before.ResolutionText, after.ResolutionText));
        }
        if (after.FrameRate != before.FrameRate)
        {
            edits.Add(new SetValueEdit($"{name} kare hızı", StreamSettings.FrameRatePath, Inv(after.FrameRate),
                StreamSettings.FrameRateText(before.FrameRate), StreamSettings.FrameRateText(after.FrameRate)));
        }
        if (after.Bitrate != before.Bitrate)
        {
            edits.Add(new SetValueEdit($"{name} bit hızı", before.BitratePath, Inv(after.Bitrate),
                StreamSettings.BitrateText(before.Bitrate), StreamSettings.BitrateText(after.Bitrate)));
        }
        if (after.Codec != before.Codec)
        {
            edits.Add(new SetValueEdit($"{name} sıkıştırma", StreamSettings.CodecPath, after.Codec, before.Codec, after.Codec));
        }
        Add(writes, IsapiPaths.Stream(channel, quality), target, edits);
    }

    static void AddBool(List<ConfigEdit> edits, string label, string path, bool? before, bool? after)
    {
        if (before is { } b && after is { } a && a != b)
        {
            edits.Add(new SetValueEdit(label, path, IsapiXml.Bool(a), RecorderText.OnOff(b), RecorderText.OnOff(a)));
        }
    }

    static void AddInt(List<ConfigEdit> edits, string label, string path, int? before, int? after)
    {
        if (before is { } b && after is { } a && a != b)
        {
            edits.Add(new SetValueEdit(label, path, Inv(a), Inv(b), Inv(a)));
        }
    }

    static void Add(List<ConfigWrite> writes, string path, string target, List<ConfigEdit> edits)
    {
        if (edits.Count > 0)
        {
            writes.Add(new ConfigWrite(path, target, edits));
        }
    }

    static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
}
