using System.Globalization;

namespace SPVision.Core;

/// <summary>
/// Which camera plays in which tile of the synchronous playback grid, and which tile is focused
/// (its recordings drive the timeline). Clicking a camera in the list:
/// it is already in the grid → it is removed; otherwise it goes into the first empty tile;
/// if the grid is full it replaces the camera of the focused tile. Removing a camera leaves its tile
/// empty, so the other tiles keep playing undisturbed.
/// </summary>
public sealed class PlaybackSlots
{
    readonly List<CameraRef?> _slots;

    public PlaybackSlots(int tileCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tileCount, 1);
        _slots = [.. Enumerable.Repeat<CameraRef?>(null, tileCount)];
    }

    public IReadOnlyList<CameraRef?> Slots => _slots;

    public int TileCount => _slots.Count;

    /// <summary>Number of tiles with a camera.</summary>
    public int Count => _slots.Count(s => s is not null);

    /// <summary>Index of the focused tile.</summary>
    public int Focus { get; private set; }

    public CameraRef? Focused => _slots[Focus];

    public int IndexOf(CameraRef camera) => _slots.IndexOf(camera);

    /// <summary>Focuses a tile that shows a camera; empty tiles cannot take the focus.</summary>
    public bool SetFocus(int index)
    {
        if (index < 0 || index >= _slots.Count || _slots[index] is null || index == Focus)
        {
            return false;
        }
        Focus = index;
        return true;
    }

    /// <summary>A click on a camera row of the list. Returns the tile index that changed.</summary>
    public int Pick(CameraRef camera)
    {
        var existing = _slots.IndexOf(camera);
        if (existing >= 0)
        {
            _slots[existing] = null;
            if (existing == Focus)
            {
                Focus = NearestFilled(existing) ?? existing;
            }
            return existing;
        }
        var empty = _slots.IndexOf(null);
        if (empty >= 0)
        {
            _slots[empty] = camera;
            Focus = empty;
            return empty;
        }
        _slots[Focus] = camera;
        return Focus;
    }

    /// <summary>Removes a camera wherever it is (e.g. its device was deleted).</summary>
    public void Remove(Func<CameraRef, bool> match)
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            if (_slots[i] is { } camera && match(camera))
            {
                _slots[i] = null;
            }
        }
        if (_slots[Focus] is null)
        {
            Focus = NearestFilled(Focus) ?? Focus;
        }
    }

    /// <summary>
    /// Layout change. Cameras keep their tiles; when the grid shrinks, cameras of dropped tiles move
    /// into the empty tiles that remain (the focused camera first, then in order) and only what still
    /// does not fit is dropped.
    /// </summary>
    public void Resize(int tileCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tileCount, 1);
        var focused = Focused;
        if (tileCount >= _slots.Count)
        {
            _slots.AddRange(Enumerable.Repeat<CameraRef?>(null, tileCount - _slots.Count));
            return;
        }
        var overflow = new Queue<CameraRef>(_slots.Skip(tileCount).OfType<CameraRef>().OrderBy(c => c == focused ? 0 : 1));
        _slots.RemoveRange(tileCount, _slots.Count - tileCount);
        for (var i = 0; i < _slots.Count && overflow.Count > 0; i++)
        {
            _slots[i] ??= overflow.Dequeue();
        }
        var index = focused is null ? -1 : _slots.IndexOf(focused);
        Focus = index >= 0 ? index : NearestFilled(Math.Min(Focus, tileCount - 1)) ?? Math.Min(Focus, tileCount - 1);
    }

    /// <summary>The filled tile closest to <paramref name="index"/>, later tiles first (like the list moving up).</summary>
    int? NearestFilled(int index)
    {
        for (var i = index; i < _slots.Count; i++)
        {
            if (_slots[i] is not null)
            {
                return i;
            }
        }
        for (var i = Math.Min(index, _slots.Count) - 1; i >= 0; i--)
        {
            if (_slots[i] is not null)
            {
                return i;
            }
        }
        return null;
    }
}

public enum JumpKind
{
    /// <summary>The requested time is recorded: play from there.</summary>
    Exact,

    /// <summary>Not recorded: play from the start of the next recording.</summary>
    NextRecording,

    /// <summary>No recording after the requested time.</summary>
    NoneAfter,

    /// <summary>No recordings at all.</summary>
    NoRecordings,
}

public readonly record struct JumpResult(JumpKind Kind, DateTime Target, DateTime? LastEnd = null)
{
    /// <summary>Amber message for the info bar, or null when the jump landed where asked.</summary>
    public string? Message => Kind switch
    {
        JumpKind.NextRecording => PlaybackText.JumpedToNext(Target),
        JumpKind.NoneAfter => PlaybackText.NoneAfter(LastEnd!.Value),
        JumpKind.NoRecordings => PlaybackText.NoRecordingsToday,
        _ => null,
    };
}

/// <summary>Where a seek ("Saate git", timeline click, ±30 s) lands in the focused camera's recordings.</summary>
public static class RecordingJump
{
    public static JumpResult Resolve(IReadOnlyList<RecordingSegment> segments, DateTime time)
    {
        if (segments.Count == 0)
        {
            return new JumpResult(JumpKind.NoRecordings, time);
        }
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (time >= segment.Start && time < segment.End)
            {
                return new JumpResult(JumpKind.Exact, time);
            }
            if (time < segment.Start)
            {
                return new JumpResult(JumpKind.NextRecording, segment.Start);
            }
        }
        return new JumpResult(JumpKind.NoneAfter, time, segments.Max(s => s.End));
    }

    /// <summary>True when <paramref name="time"/> lies inside one of the segments.</summary>
    public static bool IsRecorded(IReadOnlyList<RecordingSegment> segments, DateTime time) =>
        segments.Any(s => time >= s.Start && time < s.End);

    /// <summary>Start of the first recording after <paramref name="time"/>, if any.</summary>
    public static DateTime? NextStart(IReadOnlyList<RecordingSegment> segments, DateTime time) =>
        segments.Where(s => s.Start > time).Select(s => (DateTime?)s.Start).Min();
}

/// <summary>Playback speed steps: 1/8×, 1/4×, 1/2×, 1×, 2×, 4×, 8×.</summary>
public static class PlaybackSpeed
{
    static readonly string[] Labels = ["1/8×", "1/4×", "1/2×", "1×", "2×", "4×", "8×"];

    public const int Normal = 3;
    public const int Slowest = 0;
    public static int Fastest => Labels.Length - 1;

    public static double Rate(int index) => Math.Pow(2, Clamp(index) - Normal);

    public static string Label(int index) => Labels[Clamp(index)];

    public static bool CanSlowDown(int index) => index > Slowest;

    public static bool CanSpeedUp(int index) => index < Fastest;

    public static int Slower(int index) => Clamp(index - 1);

    public static int Faster(int index) => Clamp(index + 1);

    /// <summary>
    /// Speed relative to normal in SDK steps: positive = that many NET_DVR_PLAYFAST (+ PlayM4_Fast) calls,
    /// negative = NET_DVR_PLAYSLOW (+ PlayM4_Slow) calls. Each step doubles or halves the speed.
    /// </summary>
    public static int Steps(int index) => Clamp(index) - Normal;

    public static int Clamp(int index) => Math.Clamp(index, Slowest, Labels.Length - 1);
}

public enum SyncAction
{
    None,

    /// <summary>Re-seek the tile to the shared playback time.</summary>
    Seek,

    /// <summary>The camera has no recording at the shared time: stop it and wait.</summary>
    Park,
}

/// <summary>Drift correction of synchronous playback: every tile follows the focused tile's time.</summary>
public static class PlaybackSync
{
    /// <summary>How often tile times are compared.</summary>
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Allowed difference before a tile is re-seeked: 2 s of recording at normal speed or slower. Faster
    /// speeds scale it (2 s of recording pass in 0.25 s at 8×), so a tile is never re-seeked for less
    /// than a quarter second of real time.
    /// </summary>
    public static TimeSpan Tolerance(int speedIndex) => TimeSpan.FromSeconds(2 * Math.Max(1, PlaybackSpeed.Rate(speedIndex)));

    /// <summary>
    /// What a tile needs. <paramref name="tileTime"/> is the time of the frame it shows (null when it is not
    /// playing), <paramref name="parked"/> whether it is waiting for its next recording,
    /// <paramref name="segments"/> its recordings (null when unknown: the tile is then never parked).
    /// </summary>
    public static SyncAction Decide(DateTime master, DateTime? tileTime, bool parked, IReadOnlyList<RecordingSegment>? segments, TimeSpan tolerance)
    {
        var recorded = segments is null || RecordingJump.IsRecorded(segments, master);
        if (parked)
        {
            return recorded ? SyncAction.Seek : SyncAction.None;
        }
        if (!recorded)
        {
            return SyncAction.Park;
        }
        return tileTime is { } time && (time - master).Duration() > tolerance ? SyncAction.Seek : SyncAction.None;
    }
}

/// <summary>Month view of the playback calendar (weeks start on Monday, as in Turkey).</summary>
public static class CalendarMonth
{
    public static IReadOnlyList<string> WeekdayNames { get; } = ["Pt", "Sa", "Ça", "Pe", "Cu", "Ct", "Pz"];

    /// <summary>Empty cells before day 1 in a Monday-first week.</summary>
    public static int LeadingBlanks(int year, int month) => ((int)new DateTime(year, month, 1).DayOfWeek + 6) % 7;

    /// <summary>"Ekim 2026".</summary>
    public static string Title(DateTime month) =>
        month.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR")) is var text && text.Length > 0
            ? char.ToUpper(text[0], CultureInfo.GetCultureInfo("tr-TR")) + text[1..]
            : text;
}

/// <summary>User-facing playback texts (Turkish), kept here so tests pin them down.</summary>
public static class PlaybackText
{
    public const string NoRecordingsToday = "Bu gün için kayıt bulunamadı";
    public const string SelectCameraFirst = "Önce listeden kamera seçin";
    public const string InvalidTime = "Geçerli bir saat girin (00:00 – 23:59)";

    public static string JumpedToNext(DateTime target) =>
        $"Bu saatte kayıt yok, en yakın kayda atlandı ({target.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";

    public static string NoneAfter(DateTime lastEnd) =>
        $"Bu saatten sonra kayıt yok (son kayıt {lastEnd.ToString("HH:mm", CultureInfo.InvariantCulture)})";

    /// <summary>"Saate git": two digit fields; null when they do not form a valid time of day.</summary>
    public static TimeSpan? ParseTimeOfDay(string? hours, string? minutes) =>
        int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out var h) &&
        int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var m) &&
        h is >= 0 and <= 23 && m is >= 0 and <= 59
            ? new TimeSpan(h, m, 0)
            : null;
}
