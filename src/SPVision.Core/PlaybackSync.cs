using System.Globalization;

namespace SPVision.Core;

/// <summary>
/// Which camera plays in which tile of the synchronous playback grid, and which tile is focused
/// (its recordings drive the timeline). Clicking a camera in the list:
/// it is already in the grid → it is removed; otherwise it goes into the first empty tile;
/// if the grid is full it replaces the camera of the focused tile. Removing a camera leaves its tile
/// empty, so the other tiles keep playing undisturbed. Drag and drop (<see cref="Place"/>, <see cref="Swap"/>)
/// never puts a camera in two tiles.
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

    /// <summary>
    /// A camera dragged from the list onto tile <paramref name="tile"/> (like the live wall's drop). Already in another
    /// tile → the two tiles trade places (an empty target simply takes it), so a camera never shows twice; otherwise it
    /// fills the tile or replaces its camera (which leaves the grid: its row is unchecked). The tile takes the focus.
    /// </summary>
    public SlotDrop Place(int tile, CameraRef camera)
    {
        if (tile < 0 || tile >= _slots.Count)
        {
            return SlotDrop.None;
        }
        var existing = _slots.IndexOf(camera);
        if (existing == tile)
        {
            Focus = tile;
            return SlotDrop.None; // dropped on its own tile
        }
        if (existing >= 0)
        {
            (_slots[existing], _slots[tile]) = (_slots[tile], _slots[existing]);
            Focus = tile;
            return new SlotDrop(SlotDropKind.Swapped, tile, existing);
        }
        var removed = _slots[tile];
        _slots[tile] = camera;
        Focus = tile;
        return new SlotDrop(SlotDropKind.Placed, tile, -1, removed);
    }

    /// <summary>
    /// A tile dragged onto another: the two trade places (onto an empty tile = a move). The focus goes with the dragged
    /// camera. False when nothing changes (same tile, out of range, both empty).
    /// </summary>
    public bool Swap(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _slots.Count || to >= _slots.Count ||
            (_slots[from] is null && _slots[to] is null))
        {
            return false;
        }
        (_slots[from], _slots[to]) = (_slots[to], _slots[from]);
        Focus = _slots[to] is not null ? to : Focus == to ? from : Focus;
        return true;
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

public enum SlotDropKind
{
    /// <summary>Nothing moved (dropped on its own tile, or outside the grid).</summary>
    None,

    /// <summary>The camera went into the tile (empty, or its camera replaced): that tile opens it.</summary>
    Placed,

    /// <summary>The camera was in another tile: the two tiles traded places (their streams keep running).</summary>
    Swapped,
}

/// <param name="Tile">The drop target.</param>
/// <param name="Other">Swapped: the tile the camera came from (it now holds the target's camera, or nothing).</param>
/// <param name="Removed">Placed: the camera the target showed before (it left the grid).</param>
public readonly record struct SlotDrop(SlotDropKind Kind, int Tile, int Other = -1, CameraRef? Removed = null)
{
    public static SlotDrop None => new(SlotDropKind.None, -1);
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

    /// <summary>
    /// The segments as the timeline draws them: clipped to <paramref name="day"/> (a recording running over
    /// midnight is cut at 00:00 / 24:00), empty ones dropped, sorted, overlapping or touching ones merged.
    /// </summary>
    public static IReadOnlyList<RecordingSegment> ClipToDay(IEnumerable<RecordingSegment> segments, DateTime day)
    {
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);
        var result = new List<RecordingSegment>();
        var clipped = segments
            .Select(s => s with { Start = s.Start < dayStart ? dayStart : s.Start, End = s.End > dayEnd ? dayEnd : s.End })
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start);
        foreach (var segment in clipped)
        {
            if (result.Count > 0 && segment.Start <= result[^1].End)
            {
                if (segment.End > result[^1].End)
                {
                    result[^1] = result[^1] with { End = segment.End };
                }
                continue;
            }
            result.Add(segment);
        }
        return result;
    }
}

public enum DayStartKind
{
    /// <summary>Recorded at 00:00 (or recordings unknown): play from midnight.</summary>
    Midnight,

    /// <summary>Not recorded at 00:00: play from the first recording of the day.</summary>
    FirstRecording,

    /// <summary>No camera recorded anything on the day.</summary>
    NoRecordings,
}

public readonly record struct DayStartResult(DayStartKind Kind, DateTime Target)
{
    /// <summary>Amber message for the info bar, or null when playback starts at 00:00.</summary>
    public string? Message => Kind switch
    {
        DayStartKind.FirstRecording => PlaybackText.StartedAtFirstRecording(Target),
        DayStartKind.NoRecordings => PlaybackText.NoRecordingsOnDay,
        _ => null,
    };
}

/// <summary>A double-clicked calendar day (or Enter on it) plays from 00:00 on every tile.</summary>
public static class DayStart
{
    /// <summary>
    /// Where the day starts. <paramref name="focused"/>: the focused camera's recordings (null when its search
    /// failed: midnight, the recorder starts at its first recording). Not recorded at 00:00 → its first recording
    /// of the day; none at all → the first recording of any camera in <paramref name="all"/> (every camera of the
    /// grid, null entries unknown; <paramref name="all"/> itself null while some are still searching → null = wait).
    /// </summary>
    public static DayStartResult? Resolve(DateTime day, IReadOnlyList<RecordingSegment>? focused, IReadOnlyList<IReadOnlyList<RecordingSegment>?>? all)
    {
        var midnight = day.Date;
        if (focused is null)
        {
            return new DayStartResult(DayStartKind.Midnight, midnight);
        }
        var own = RecordingJump.ClipToDay(focused, midnight);
        if (own.Count > 0)
        {
            return From(own[0].Start, midnight);
        }
        if (all is null)
        {
            return null;
        }
        var any = RecordingJump.ClipToDay(all.OfType<IReadOnlyList<RecordingSegment>>().SelectMany(s => s), midnight);
        if (any.Count > 0)
        {
            return From(any[0].Start, midnight);
        }
        return new DayStartResult(all.Any(s => s is null) ? DayStartKind.Midnight : DayStartKind.NoRecordings, midnight);
    }

    static DayStartResult From(DateTime first, DateTime midnight) =>
        first <= midnight ? new DayStartResult(DayStartKind.Midnight, midnight) : new DayStartResult(DayStartKind.FirstRecording, first);
}

/// <summary>
/// Calendar dots of the playback sidebar. A day gets a dot when ANY of the checked cameras recorded on it;
/// days after today never get one. Answers are kept per camera and day, so unchecking a camera only removes
/// the dots it alone gave, and a day already dotted by one camera is not asked again for the others.
/// </summary>
public sealed class RecordedDays
{
    readonly Dictionary<(CameraRef Camera, DateTime Day), bool> _known = [];

    /// <summary>Stores the recorder's answer (a day search, or the full recording search of the playback day).</summary>
    public void Set(CameraRef camera, DateTime day, bool recorded) => _known[(camera, day.Date)] = recorded;

    /// <summary>The stored answer; null when the camera was not asked about that day yet.</summary>
    public bool? Get(CameraRef camera, DateTime day) => _known.TryGetValue((camera, day.Date), out var recorded) ? recorded : null;

    /// <summary>
    /// Stores the answer of the month query (<see cref="IsapiClient.GetRecordedDaysAsync"/>): every day of the
    /// month up to today is recorded or not; days after today are left unknown (no dot, whatever the recorder says).
    /// </summary>
    public void SetMonth(CameraRef camera, DateTime month, IReadOnlySet<int> recorded, DateTime today)
    {
        foreach (var day in DaysOf(month, today))
        {
            Set(camera, day, recorded.Contains(day.Day));
        }
    }

    /// <summary>Day numbers of <paramref name="month"/> that carry a dot for the given cameras.</summary>
    public IReadOnlySet<int> Marked(IEnumerable<CameraRef> cameras, DateTime month, DateTime today)
    {
        var list = cameras.Distinct().ToList();
        var days = new HashSet<int>();
        foreach (var day in DaysOf(month, today))
        {
            if (list.Any(c => Get(c, day) == true))
            {
                days.Add(day.Day);
            }
        }
        return days;
    }

    /// <summary>
    /// The next day <paramref name="camera"/> should be asked about, newest first: up to today, not answered
    /// for this camera yet and not already dotted by another of <paramref name="cameras"/>. Null when done.
    /// </summary>
    public DateTime? NextToSearch(CameraRef camera, IEnumerable<CameraRef> cameras, DateTime month, DateTime today)
    {
        var others = cameras.Where(c => c != camera).Distinct().ToList();
        foreach (var day in DaysOf(month, today).Reverse())
        {
            if (Get(camera, day) is null && !others.Any(c => Get(c, day) == true))
            {
                return day;
            }
        }
        return null;
    }

    /// <summary>
    /// A day search failed (recorder busy with the playback searches, timeout…): wait this long and ask the same
    /// day again; after the third failure in a row null = stop (asked again on the next focus, day or camera change).
    /// </summary>
    public static TimeSpan? RetryDelay(int failures) => failures switch
    {
        1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(30),
        _ => null,
    };

    static IEnumerable<DateTime> DaysOf(DateTime month, DateTime today)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        for (var day = first; day.Month == first.Month && day <= today.Date; day = day.AddDays(1))
        {
            yield return day;
        }
    }
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

    /// <summary>The tile is behind: play it one speed step faster until it is in step (no reopen).</summary>
    CatchUp,

    /// <summary>The tile is ahead: pause it until the shared time reaches it (no reopen).</summary>
    Hold,

    /// <summary>The nudged tile is in step again: back to the shared speed / pause state.</summary>
    Resume,
}

/// <summary>How a playing tile is being brought back in step without reopening it.</summary>
public enum SyncNudge
{
    None,
    CatchUp,
    Hold,
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

    /// <summary>
    /// Farthest a tile may be off (of recording, scaled by speeds above 1×) and still be nudged; beyond it — a wrong
    /// start position, a recorder that skipped — it is re-seeked.
    /// </summary>
    public static TimeSpan NudgeLimit { get; } = TimeSpan.FromSeconds(20);

    /// <summary>A nudge ends when the tile is this close (of recording, scaled by speeds above 1×).</summary>
    public static TimeSpan Settle { get; } = TimeSpan.FromSeconds(0.5);

    /// <summary>A nudge that has not brought the tile in step after this long (real time) becomes a re-seek.</summary>
    public static TimeSpan NudgeTimeout { get; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Shortest time (real time) between two nudge changes of a tile — start, end, start again: its speed / pause is never
    /// flipped more than once a second, even when a speed change lands late (~250 ms) or the clocks jitter.
    /// </summary>
    public static TimeSpan MinNudgeHold { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Drift correction for tiles whose time is the real frame time (SDK path: PlayM4_GetSystemTime). The recorder
    /// starts a playback on a key frame up to a GOP (seconds) before the requested time, so re-seeking a tile that is
    /// a few seconds off would land it off again, every 5 s. Such a tile is nudged instead: one speed step faster while
    /// behind (<see cref="SyncAction.CatchUp"/>), paused while ahead (<see cref="SyncAction.Hold"/>), back to normal
    /// within <see cref="Settle"/> (<see cref="SyncAction.Resume"/>). Only a tile more than <see cref="NudgeLimit"/> off,
    /// or a nudge older than <see cref="NudgeTimeout"/>, is re-seeked. Hysteresis: a nudge starts beyond the tolerance
    /// (2 s at 1×) and ends within <see cref="Settle"/>, and nothing changes within <see cref="MinNudgeHold"/> of the last
    /// change (<paramref name="sinceNudgeChange"/>: real time since the tile's nudge last started or ended). <paramref name="canNudge"/>
    /// false (Flyleaf, whose RTSP is paced by the recorder) = the plain rule of the other overload.
    /// </summary>
    public static SyncAction Decide(DateTime master, DateTime? tileTime, bool parked, IReadOnlyList<RecordingSegment>? segments,
        TimeSpan tolerance, double rate, bool canNudge, SyncNudge nudge, TimeSpan sinceNudgeChange)
    {
        var plain = Decide(master, tileTime, parked, segments, tolerance);
        if (!canNudge || parked || plain == SyncAction.Park)
        {
            return plain;
        }
        if (tileTime is not { } time)
        {
            return SyncAction.None; // not known right now (control command in progress): keep what runs
        }
        var scale = Math.Max(1, rate);
        var diff = time - master;
        if (diff.Duration() > NudgeLimit * scale || (nudge != SyncNudge.None && sinceNudgeChange > NudgeTimeout))
        {
            return SyncAction.Seek;
        }
        if (sinceNudgeChange < MinNudgeHold)
        {
            return SyncAction.None; // changed less than a second ago: keep it (no back-and-forth)
        }
        var settle = Settle * scale;
        return nudge switch
        {
            SyncNudge.CatchUp => diff >= -settle ? SyncAction.Resume : SyncAction.None,
            SyncNudge.Hold => diff <= settle ? SyncAction.Resume : SyncAction.None,
            _ => diff < -tolerance ? SyncAction.CatchUp : diff > tolerance ? SyncAction.Hold : SyncAction.None,
        };
    }
}

/// <summary>Checks where the recorder really started a playback (SDK path: absolute time of the first frame).</summary>
public static class PlaybackStart
{
    /// <summary>
    /// How far before the requested time a correct start may land: the recorder starts on the key frame before it
    /// (one GOP; ~3 s measured on the DS-8632NI-I8, longer with H.265+ / smart codecs). A start earlier than this is
    /// the recorder starting at the beginning of the recording file instead (seen: 23:11:04 for a request of
    /// 00:00:00, the file began at 23:10:49).
    /// </summary>
    public static TimeSpan MaxEarly { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Re-opens of one seek for a wrong start before the tile accepts it (drift correction takes over).</summary>
    public const int MaxRetries = 2;

    /// <summary>
    /// True when the first frame is too early to be the key frame before <paramref name="requested"/>. A later start
    /// is not wrong: a request inside a gap starts at the next recording.
    /// </summary>
    public static bool IsWrong(DateTime requested, DateTime firstFrame) => firstFrame < requested - MaxEarly;

    /// <summary>Where to reopen: the requested time plus what the shared clock advanced since the seek.</summary>
    public static DateTime RetryTarget(DateTime requested, TimeSpan sinceSeek, double rate, bool paused) =>
        paused ? requested : requested + TimeSpan.FromTicks((long)(sinceSeek.Ticks * Math.Max(0, rate)));
}

/// <summary>What a freshly opened SDK playback needs before the user may see it (<see cref="PlaybackCatchUp"/>).</summary>
public enum CatchUpAction
{
    /// <summary>The first frame's absolute time is not known yet: keep the video hidden and wait.</summary>
    WaitForTime,

    /// <summary>The recorder started at the beginning of the recording file (<see cref="PlaybackStart.IsWrong"/>): reopen.</summary>
    Reopen,

    /// <summary>Far before the requested time: play hidden at <see cref="PlaybackCatchUp.Steps"/>.</summary>
    FastForward,

    /// <summary>
    /// Within <see cref="PlaybackCatchUp.SlowLead"/> of the requested time: play hidden at normal speed (a slow-down takes
    /// effect late, so the last second is not fast-forwarded) until it is reached.
    /// </summary>
    Settle,

    /// <summary>The requested time is on screen (or waiting longer makes no sense): show the video.</summary>
    Reveal,
}

/// <summary>
/// "Nothing before the requested time" rule of the SDK path. PlayBackByTime_V40 starts on the key frame before the
/// requested time (measured on the DS-8632NI-I8: 3.8 s before 00:00:00 = the previous day's 23:59:56 in the camera OSD;
/// 2.2 s before 12:30:00; the GOP there is ~6.7 s). The tile keeps its video window hidden while the stream plays at
/// 4× (two PLAYFAST steps on the recorder + PlayM4_Fast on our port; 8× and above make the recorder send key frames
/// only, which would jump past the requested time) up to <see cref="SlowLead"/> before it, then at normal speed until
/// our port — the window's only renderer (the SDK gets no window) — is <see cref="Margin"/> past it; then the shared
/// speed / pause is applied and the video shown. Measured: 3.8 s caught up in ~1.0 s, 2.2 s in ~0.6 s.
/// </summary>
public static class PlaybackCatchUp
{
    /// <summary>Speed steps while catching up (2 = 4×; the recorder still sends every frame).</summary>
    public const int Steps = 2;

    /// <summary>
    /// Back to normal speed this far before the requested time: after PLAYSLOW / PlayM4_Slow the picture keeps running
    /// at 4× for ~250 ms (measured: 1.1 s of recording), which would otherwise land a second past the requested time.
    /// </summary>
    public static TimeSpan SlowLead { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far past the requested time the frame must be before the window is shown: a few frames, so the last
    /// picture presented while hidden (which the renderer may show again) is already at the requested time.
    /// </summary>
    public static TimeSpan Margin { get; } = TimeSpan.FromMilliseconds(120);

    /// <summary>A stream that reports no absolute time is shown after this long (as before: the SDK's estimate is used).</summary>
    public static TimeSpan TimeWait { get; } = TimeSpan.FromSeconds(4);

    /// <summary>Longest hidden catch-up (30 s early at 4× = 7.5 s); after it the video is shown anyway.</summary>
    public static TimeSpan Limit { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <paramref name="frame"/>: absolute time of our port's current frame (null before the first one);
    /// <paramref name="elapsed"/>: since the catch-up started; <paramref name="canReopen"/>: retries of a wrong start left.
    /// </summary>
    public static CatchUpAction Decide(DateTime requested, DateTime? frame, TimeSpan elapsed, bool canReopen)
    {
        if (frame is not { } ours)
        {
            return elapsed >= TimeWait ? CatchUpAction.Reveal : CatchUpAction.WaitForTime;
        }
        if (PlaybackStart.IsWrong(requested, ours))
        {
            return canReopen ? CatchUpAction.Reopen : CatchUpAction.Reveal; // minutes early: no catching up that
        }
        if (ours >= requested + Margin || elapsed >= Limit)
        {
            return CatchUpAction.Reveal;
        }
        return ours >= requested - SlowLead ? CatchUpAction.Settle : CatchUpAction.FastForward;
    }

    /// <summary>
    /// Whether the shared clock may take a tile's time: never before the last requested time (<paramref name="floor"/>),
    /// so the clock and the timeline cursor never show a moment before what the user asked for.
    /// </summary>
    public static bool MayFollow(DateTime tileTime, DateTime floor) => tileTime >= floor;
}

/// <summary>State of the playback transport (play / pause / stop buttons of Kayıt Oynatma).</summary>
public enum TransportState
{
    /// <summary>No shared time yet (no day picked / restored cameras waiting / searches running).</summary>
    NoTime,
    Playing,
    Paused,

    /// <summary>Stop pressed: streams closed, clips finalized, the shared time kept where it was.</summary>
    Stopped,
}

/// <summary>What the play / pause button does in a <see cref="TransportState"/>.</summary>
public enum PlayPauseAction
{
    /// <summary>No time yet: start at the first recording of the day (or 00:00 after a double-click).</summary>
    Start,

    /// <summary>Stopped: every tile reopens at the kept shared time and plays.</summary>
    Restart,
    Pause,
    Resume,
}

/// <summary>Transport rules of Kayıt Oynatma (pure; the view applies them).</summary>
public static class PlaybackTransport
{
    public static TransportState State(bool hasTime, bool paused, bool stopped) =>
        !hasTime ? TransportState.NoTime : stopped ? TransportState.Stopped : paused ? TransportState.Paused : TransportState.Playing;

    /// <summary>Stop is enabled while streams are open (playing or paused) and some tile has a camera.</summary>
    public static bool CanStop(TransportState state, bool anyCamera) =>
        anyCamera && state is TransportState.Playing or TransportState.Paused;

    /// <summary>The play / pause button shows "pause" only while playing.</summary>
    public static bool ShowsPause(TransportState state) => state == TransportState.Playing;

    public static PlayPauseAction PlayPause(TransportState state) => state switch
    {
        TransportState.NoTime => PlayPauseAction.Start,
        TransportState.Stopped => PlayPauseAction.Restart,
        TransportState.Paused => PlayPauseAction.Resume,
        _ => PlayPauseAction.Pause,
    };

    /// <summary>
    /// Whether tiles open streams on their own (a camera added, a search finished, the view shown again, drift
    /// correction): only while there is a shared time and playback is not stopped.
    /// </summary>
    public static bool TilesJoin(TransportState state) => state is TransportState.Playing or TransportState.Paused;
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

    /// <summary>Double-click on a day: no camera of the grid recorded anything on it.</summary>
    public const string NoRecordingsOnDay = "Bu günde kayıt yok";

    /// <summary>Double-click on a day with no camera checked.</summary>
    public const string SelectCameraForDay = "Önce kamera seçin";

    /// <summary>Tooltip of the calendar days.</summary>
    public const string DayToolTip = "Çift tıklayın: günün kaydını 00:00'dan oynat";

    /// <summary>Double-click on a day that is not recorded at 00:00.</summary>
    public static string StartedAtFirstRecording(DateTime first) =>
        $"00:00'da kayıt yok · ilk kayıt {first.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}'den başlatıldı";

    /// <summary>The calendar's day searches of a camera kept failing (status bar; the dots of the days left stay off).</summary>
    public static string CalendarSearchFailed(string cameraName, string reason) =>
        $"Takvim için kayıt günleri alınamadı ({cameraName}): {reason}";

    public static string JumpedToNext(DateTime target) =>
        $"Bu saatte kayıt yok, en yakın kayda atlandı ({target.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})";

    public static string NoneAfter(DateTime lastEnd) =>
        $"Bu saatten sonra kayıt yok (son kayıt {lastEnd.ToString("HH:mm", CultureInfo.InvariantCulture)})";

    /// <summary>The recording search of the focused camera failed: no green segments, playback still works.</summary>
    public static string SearchFailed(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "Kayıt listesi alınamadı" : $"Kayıt listesi alınamadı: {reason}";

    /// <summary>"Saate git": two digit fields; null when they do not form a valid time of day.</summary>
    public static TimeSpan? ParseTimeOfDay(string? hours, string? minutes) =>
        int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out var h) &&
        int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var m) &&
        h is >= 0 and <= 23 && m is >= 0 and <= 59
            ? new TimeSpan(h, m, 0)
            : null;
}
