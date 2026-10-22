using System.Globalization;

namespace SPVision.Core;

/// <summary>What happened, as the events panel and its settings group it ("HANGİ OLAYLAR").</summary>
public enum AlertKind
{
    /// <summary>Kamera bağlantısı koptu: the recorder's video loss, or a live tile that was playing and lost its stream.</summary>
    CameraOffline,

    /// <summary>Disk sorunu: the recorder's disk error / full event, or <see cref="DiskWatch"/> going from healthy to a problem.</summary>
    Disk,

    /// <summary>Hareket algılama (VMD).</summary>
    Motion,

    /// <summary>Çizgi geçme / alan ihlali (line crossing, intrusion, region entrance / exit).</summary>
    Smart,

    /// <summary>Görüntü kapatma (video tampering).</summary>
    Tamper,
}

/// <summary>Where an event came from (merging: the recorder and SPVision may both report the same lost camera).</summary>
public enum EventSource
{
    Recorder,
    LiveTile,
    DiskCheck,
}

/// <summary>Filter segments of the events panel: Tümü / Hareket / Bağlantı / Disk.</summary>
public enum EventFilter
{
    All,
    Motion,
    Connection,
    Disk,
}

/// <summary>
/// Ayarlar of the events panel ("Bildirim ayarları"), kept in <see cref="AppSettings.Notifications"/>. Which kinds are
/// collected (lost cameras and disk problems by default; motion, smart and tamper events only on request — they need the
/// detection to be enabled on the camera, which SPVision never changes), whether a pop-up and a sound go with a new event, and
/// how often one camera may pop up per kind.
/// </summary>
public sealed class EventNotificationSettings
{
    /// <summary>"Aynı kameradan en sık" choices, in seconds: 30 sn / 1 dk / 5 dk.</summary>
    public static readonly IReadOnlyList<int> Intervals = [30, 60, 300];

    public bool CameraOffline { get; set; } = true;

    public bool Disk { get; set; } = true;

    public bool Motion { get; set; }

    public bool Smart { get; set; }

    public bool Tamper { get; set; }

    /// <summary>"Windows bildirimi": the pop-up at the bottom right of the screen (also while SPVision is minimized).</summary>
    public bool Popup { get; set; } = true;

    /// <summary>"Ses çal": a short system sound with the pop-up.</summary>
    public bool Sound { get; set; }

    /// <summary>At most one pop-up per camera and kind within this many seconds (a stored value off the list reads as 1 dk).</summary>
    public int IntervalSeconds { get; set; } = 60;

    public TimeSpan Interval => TimeSpan.FromSeconds(Intervals.Contains(IntervalSeconds) ? IntervalSeconds : 60);

    public bool AnyKind => CameraOffline || Disk || Motion || Smart || Tamper;

    public bool IsOn(AlertKind kind) => kind switch
    {
        AlertKind.CameraOffline => CameraOffline,
        AlertKind.Disk => Disk,
        AlertKind.Motion => Motion,
        AlertKind.Smart => Smart,
        _ => Tamper,
    };

    public void Set(AlertKind kind, bool on)
    {
        switch (kind)
        {
            case AlertKind.CameraOffline: CameraOffline = on; break;
            case AlertKind.Disk: Disk = on; break;
            case AlertKind.Motion: Motion = on; break;
            case AlertKind.Smart: Smart = on; break;
            default: Tamper = on; break;
        }
    }
}

/// <summary>An event to record, before <see cref="EventCenter"/> decides whether it is new.</summary>
public sealed record EventCandidate(
    AlertKind Kind, Guid DeviceId, string DeviceName, int? Channel, string? CameraName, string Title, DateTime Time, EventSource Source);

/// <summary>One row of the events panel (newest first in <see cref="EventCenter.Events"/>).</summary>
public sealed class DeviceEvent
{
    public required long Id { get; init; }
    public required AlertKind Kind { get; init; }
    public required Guid DeviceId { get; init; }
    public required string DeviceName { get; init; }

    /// <summary>Camera channel (ISAPI number) of a camera event; null for the recorder itself (disk).</summary>
    public int? Channel { get; init; }

    /// <summary>Known camera of the device (the panel's "Canlı izle" / "Kaydı oynat" need one); else "Kanal n" or null.</summary>
    public string? CameraName { get; init; }

    /// <summary>True when <see cref="Channel"/> is a camera SPVision knows (its buttons work).</summary>
    public bool KnownCamera { get; init; }

    public required string Title { get; init; }

    /// <summary>When it happened: the recorder's own clock for its events (what Kayıt Oynatma searches), else this PC's.</summary>
    public required DateTime Time { get; init; }

    public required EventSource Source { get; init; }

    /// <summary>Last report of the same occurrence (the recorder repeats an active event while it lasts).</summary>
    public DateTime LastSeen { get; set; }

    public bool Read { get; set; }

    public CameraRef? Camera => KnownCamera && Channel is { } channel ? new CameraRef(DeviceId, channel) : null;

    /// <summary>Second line of the row and the pop-up: the camera, or "Kayıt cihazı · DS-8632NI-I8".</summary>
    public string Place => CameraName ?? $"Kayıt cihazı · {DeviceName}";

    public string TimeText => EventText.Time(Time);
}

/// <summary>What <see cref="EventCenter.Add"/> did with a candidate.</summary>
public enum EventOutcome
{
    /// <summary>Kind switched off in the settings: not kept.</summary>
    Ignored,

    /// <summary>The same occurrence again (a repeat of the recorder, or the other source of a lost camera): no new row.</summary>
    Merged,

    /// <summary>New row, no pop-up (the same camera and kind popped up within the chosen interval).</summary>
    Added,

    /// <summary>New row and a pop-up (and the sound, if switched on).</summary>
    Notify,
}

/// <summary>
/// The events of this run (memory only): history, merging, unread count and the pop-up throttle. UI thread only; the time
/// is passed in (unit tested, EventTests).
/// <list type="bullet">
/// <item>Only kinds switched on in <see cref="Settings"/> are kept.</item>
/// <item>The recorder repeats an active motion / smart / tamper event while it lasts: a report within <see cref="BurstGap"/>
/// of the previous one of the same camera, kind and title extends that row (<see cref="DeviceEvent.LastSeen"/>).</item>
/// <item>A lost camera is reported by the recorder (video loss) and by its live tile; a disk problem by the recorder and by
/// the disk check. Within <see cref="OfflineMerge"/> / <see cref="DiskMerge"/> of the last such row they are one event.</item>
/// <item>A new row pops up only when the same camera (the recorder itself for disks) did not pop up for the same kind
/// within <see cref="EventNotificationSettings.Interval"/>; later rows in that window are listed without a pop-up.</item>
/// <item>At most <see cref="Capacity"/> rows; the oldest go.</item>
/// </list>
/// </summary>
public sealed class EventCenter(EventNotificationSettings settings)
{
    public const int Capacity = 200;
    public static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan OfflineMerge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DiskMerge = TimeSpan.FromMinutes(10);

    readonly List<DeviceEvent> _events = []; // newest first
    readonly Dictionary<(Guid Device, int? Channel, AlertKind Kind), DateTime> _lastPopup = [];
    long _nextId = 1;

    public EventNotificationSettings Settings { get; set; } = settings;

    public IReadOnlyList<DeviceEvent> Events => _events;

    public int Unread => _events.Count(e => !e.Read);

    /// <summary>The row the last <see cref="Add"/> created or extended.</summary>
    public DeviceEvent? Last { get; private set; }

    public EventOutcome Add(EventCandidate candidate, DateTime now, bool knownCamera = true)
    {
        Last = null;
        if (!Settings.IsOn(candidate.Kind))
        {
            return EventOutcome.Ignored;
        }
        var window = candidate.Kind switch
        {
            AlertKind.CameraOffline => OfflineMerge,
            AlertKind.Disk => DiskMerge,
            _ => BurstGap,
        };
        var same = _events.FirstOrDefault(e =>
            e.Kind == candidate.Kind && e.DeviceId == candidate.DeviceId && e.Channel == candidate.Channel &&
            (candidate.Kind is AlertKind.CameraOffline or AlertKind.Disk || e.Title == candidate.Title));
        if (same is not null && now - same.LastSeen <= window && now >= same.LastSeen)
        {
            same.LastSeen = now;
            Last = same;
            return EventOutcome.Merged;
        }
        var row = new DeviceEvent
        {
            Id = _nextId++,
            Kind = candidate.Kind,
            DeviceId = candidate.DeviceId,
            DeviceName = candidate.DeviceName,
            Channel = candidate.Channel,
            CameraName = candidate.CameraName,
            KnownCamera = knownCamera && candidate.Channel is not null,
            Title = candidate.Title,
            Time = candidate.Time,
            Source = candidate.Source,
            LastSeen = now,
        };
        _events.Insert(0, row);
        if (_events.Count > Capacity)
        {
            _events.RemoveRange(Capacity, _events.Count - Capacity);
        }
        Last = row;
        var key = (candidate.DeviceId, candidate.Channel, candidate.Kind);
        if (_lastPopup.TryGetValue(key, out var last) && now - last < Settings.Interval && now >= last)
        {
            return EventOutcome.Added;
        }
        _lastPopup[key] = now;
        return EventOutcome.Notify;
    }

    /// <summary>The device was removed: its rows go (their buttons could not work any more).</summary>
    public void ForgetDevice(Guid deviceId)
    {
        _events.RemoveAll(e => e.DeviceId == deviceId);
        foreach (var key in _lastPopup.Keys.Where(k => k.Device == deviceId).ToList())
        {
            _lastPopup.Remove(key);
        }
    }

    public void MarkAllRead()
    {
        foreach (var e in _events)
        {
            e.Read = true;
        }
    }

    /// <summary>Rows of one filter segment (Hareket = motion, smart and tamper; Bağlantı = lost cameras; Disk = disks).</summary>
    public IEnumerable<DeviceEvent> Filtered(EventFilter filter) => _events.Where(e => Matches(e.Kind, filter));

    public static bool Matches(AlertKind kind, EventFilter filter) => filter switch
    {
        EventFilter.Motion => kind is AlertKind.Motion or AlertKind.Smart or AlertKind.Tamper,
        EventFilter.Connection => kind == AlertKind.CameraOffline,
        EventFilter.Disk => kind == AlertKind.Disk,
        _ => true,
    };
}

/// <summary>
/// Recorder alert → event (unit tested). Event types as the ISAPI alert stream sends them, case-insensitive: VMD → Hareket;
/// linedetection / fielddetection / regionEntrance / regionExiting → Çizgi geçme / alan ihlali; shelteralarm / tamper… →
/// Görüntü kapatma; videoloss and IP channel disconnect events → Kamera bağlantısı koptu; disk… / hdd… (diskfull,
/// diskerror…) → Disk sorunu. Everything else (and every inactive state — the heartbeat) is ignored.
/// </summary>
public static class AlertMapping
{
    static readonly HashSet<string> Motion = new(StringComparer.OrdinalIgnoreCase) { "VMD", "motion", "motiondetection" };
    static readonly HashSet<string> Smart = new(StringComparer.OrdinalIgnoreCase)
    {
        "linedetection", "fielddetection", "regionEntrance", "regionExiting",
    };
    static readonly HashSet<string> Tamper = new(StringComparer.OrdinalIgnoreCase)
    {
        "shelteralarm", "tamper", "tamperdetection", "videotampering",
    };
    static readonly HashSet<string> Offline = new(StringComparer.OrdinalIgnoreCase)
    {
        "videoloss", "IPCDisconnect", "ipcOffline", "ipcNoLink", "channelOffline", "IPCamDisconnect",
    };

    public static AlertKind? KindOf(string eventType)
    {
        var type = eventType.Trim();
        if (Motion.Contains(type))
        {
            return AlertKind.Motion;
        }
        if (Smart.Contains(type))
        {
            return AlertKind.Smart;
        }
        if (Tamper.Contains(type))
        {
            return AlertKind.Tamper;
        }
        if (Offline.Contains(type))
        {
            return AlertKind.CameraOffline;
        }
        return type.StartsWith("disk", StringComparison.OrdinalIgnoreCase) || type.StartsWith("hdd", StringComparison.OrdinalIgnoreCase)
            ? AlertKind.Disk
            : null;
    }

    /// <summary>Title of the row and the pop-up.</summary>
    public static string Title(string eventType, AlertKind kind) => kind switch
    {
        AlertKind.Motion => "Hareket algılandı",
        AlertKind.Smart => eventType.Trim().ToLowerInvariant() switch
        {
            "linedetection" => "Çizgi geçildi",
            "regionentrance" => "Alana girildi",
            "regionexiting" => "Alandan çıkıldı",
            _ => "Alan ihlali",
        },
        AlertKind.Tamper => "Görüntü kapatıldı",
        AlertKind.CameraOffline => "Kamera bağlantısı koptu",
        _ => eventType.Contains("full", StringComparison.OrdinalIgnoreCase) ? "Disk sorunu: disk dolu" : "Disk sorunu: disk hatası",
    };

    /// <summary>
    /// The event of an alert of <paramref name="device"/>, or null (inactive / heartbeat, unknown type, a camera event without a
    /// channel). The channel is the ISAPI channel of the alert (<c>channelID</c>, else <c>dynChannelID</c>) and is named after
    /// the device's camera; an unknown channel keeps "Kanal n" (the row then has no buttons). Disk events belong to the recorder.
    /// </summary>
    public static (EventCandidate Candidate, bool KnownCamera)? Candidate(Device device, AlertEvent alert, DateTime now)
    {
        if (!alert.IsActive || KindOf(alert.EventType) is not { } kind)
        {
            return null;
        }
        int? channel = null;
        string? cameraName = null;
        var known = false;
        if (kind != AlertKind.Disk)
        {
            var camera = device.Cameras.FirstOrDefault(c => c.Channel == alert.Channel) ??
                         device.Cameras.FirstOrDefault(c => c.Channel == alert.DynChannel);
            channel = camera?.Channel ?? alert.Channel ?? alert.DynChannel;
            if (channel is null)
            {
                return null;
            }
            known = camera is not null;
            cameraName = camera?.Name ?? alert.ChannelName ?? string.Create(CultureInfo.InvariantCulture, $"Kanal {channel}");
        }
        var candidate = new EventCandidate(kind, device.Id, device.Name, channel, cameraName, Title(alert.EventType, kind),
            alert.Time ?? now, EventSource.Recorder);
        return (candidate, known);
    }
}

/// <summary>Texts of the events panel and the pop-up.</summary>
public static class EventText
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public const string Offline = "Kamera bağlantısı koptu";

    public static string Time(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Pop-up second line: "DIS KAPI · 10:52:14".</summary>
    public static string PlaceAndTime(DeviceEvent e) => $"{e.Place} · {Time(e.Time)}";

    /// <summary>Bell badge: the number, "99+" above 99, nothing at 0.</summary>
    public static string Badge(int unread) => unread <= 0 ? "" : unread > 99 ? "99+" : unread.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Disk sorunu: HDD 2 (hata)" for the first problem of a recorder; "+n" when more disks have one.</summary>
    public static string DiskTitle(IReadOnlyList<DiskProblem> problems)
    {
        if (problems.Count == 0)
        {
            return "Disk sorunu";
        }
        var first = problems[0];
        var text = first.Disk.Length > 0 ? $"{first.Disk} ({first.Status.ToLower(Turkish)})" : first.Status;
        return problems.Count > 1 ? $"Disk sorunu: {text} +{problems.Count - 1}" : $"Disk sorunu: {text}";
    }
}

/// <summary>
/// "Kamera bağlantısı koptu" from SPVision's own live wall: a camera whose tile was playing and is now waiting to reconnect
/// after a lost or failed stream. Leaving the screen (paging, closing the tile), stopping or an intentional reopen (quality,
/// decoder switch: connecting without a failure) is not a drop. Unit tested.
/// </summary>
public sealed class TileDropWatch
{
    public enum TileCondition
    {
        Playing,
        Connecting,
        Lost,
        Other,
    }

    readonly HashSet<CameraRef> _playing = [];

    /// <summary>The tiles on screen now; returns the cameras that were playing and have just lost their stream.</summary>
    public IReadOnlyList<CameraRef> Update(IEnumerable<(CameraRef Camera, TileCondition Condition)> tiles)
    {
        var seen = new HashSet<CameraRef>();
        var dropped = new List<CameraRef>();
        foreach (var (camera, condition) in tiles)
        {
            seen.Add(camera);
            switch (condition)
            {
                case TileCondition.Playing:
                    _playing.Add(camera);
                    break;
                case TileCondition.Lost:
                    if (_playing.Remove(camera))
                    {
                        dropped.Add(camera);
                    }
                    break;
                case TileCondition.Other:
                    _playing.Remove(camera);
                    break;
            }
        }
        _playing.RemoveWhere(c => !seen.Contains(c));
        return dropped;
    }
}

/// <summary>
/// "Disk sorunu" from the status bar's disk check (<see cref="DiskWatch"/>, every 5 minutes): one event when a recorder goes
/// from no known problem (healthy, or not read yet in this run) to a problem — not on every later read while it lasts; a
/// healthy read arms it again. Failed reads are not passed in (they keep the last state). Unit tested.
/// </summary>
public sealed class DiskEventWatch
{
    readonly Dictionary<Guid, bool> _problem = [];

    /// <summary>A successful read of <paramref name="deviceId"/>: the problems to announce now (empty: nothing new).</summary>
    public IReadOnlyList<DiskProblem> Update(Guid deviceId, IReadOnlyList<DiskProblem> problems)
    {
        var before = _problem.TryGetValue(deviceId, out var had) && had;
        _problem[deviceId] = problems.Count > 0;
        return !before && problems.Count > 0 ? problems : [];
    }

    public void Forget(Guid deviceId) => _problem.Remove(deviceId);
}

/// <summary>Reconnect delays of a dropped alert stream: 5, 10, 30, 60, then 120 s; back to 5 s after a stream that lasted a minute.</summary>
public static class AlertBackoff
{
    static readonly int[] Seconds = [5, 10, 30, 60, 120];

    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

    public static TimeSpan Delay(int failures) => TimeSpan.FromSeconds(Seconds[Math.Clamp(failures, 0, Seconds.Length - 1)]);
}
