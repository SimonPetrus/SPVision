using System.Globalization;

namespace SPVision.Core;

/// <summary>
/// The time range of a recording download ("İndir"): validation and the default range. One MP4 per camera covers the
/// whole range (no hourly split, no maximum length: multi-day ranges are one file; the dialog checks the estimated size
/// against the free space instead, <see cref="DownloadEstimate"/>). The range is local recorder time, like everything
/// in playback.
/// </summary>
public static class DownloadRange
{
    /// <summary>Default range around a playback time: this much before and after it.</summary>
    public static TimeSpan DefaultHalfWidth { get; } = TimeSpan.FromMinutes(5);

    /// <summary>Why the range cannot be downloaded (Turkish, for the dialog); null when it is valid.</summary>
    public static string? Validate(DateTime start, DateTime end, DateTime now)
    {
        if (end <= start)
        {
            return "Bitiş, başlangıçtan sonra olmalı.";
        }
        if (start >= now)
        {
            return "Başlangıç gelecekte olamaz.";
        }
        if (end > now)
        {
            return $"Bitiş gelecekte olamaz (şu an {now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)}).";
        }
        return null;
    }

    /// <summary>
    /// The range the dialog opens with: the tile's playback time −5 … +5 min (shifted back so it does not reach into the
    /// future), or the selected day (00:00 until its end, or until now for today).
    /// </summary>
    public static (DateTime Start, DateTime End) Default(DateTime? playbackTime, DateTime day, DateTime now)
    {
        now = Truncate(now);
        if (playbackTime is { } time)
        {
            time = Truncate(time);
            var end = time + DefaultHalfWidth;
            if (end > now)
            {
                end = now;
            }
            var start = end - DefaultHalfWidth - DefaultHalfWidth;
            return (start, end);
        }
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1).AddSeconds(-1);
        if (dayEnd > now)
        {
            dayEnd = now;
        }
        if (dayEnd <= dayStart)
        {
            return (now - DefaultHalfWidth - DefaultHalfWidth, now); // just after midnight: the last ten minutes
        }
        return (dayStart, dayEnd);
    }

    /// <summary>Whole seconds (the dialog and the file name have no milliseconds).</summary>
    public static DateTime Truncate(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);
}

/// <summary>File names of downloads: <c>&lt;Kamera&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;.mp4</c>.</summary>
public static class DownloadNaming
{
    /// <summary>The camera name as a file name part: characters Windows does not allow become '_' (as for clips).</summary>
    public static string SafeName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat((name ?? "").Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Kamera" : safe;
    }

    public static string FileName(string? cameraName, DateTime start, DateTime end) =>
        $"{SafeName(cameraName)}_{Stamp(start)}_{Stamp(end)}.mp4";

    /// <summary>
    /// The path in <paramref name="folder"/>; when that file exists already, " (2)", " (3)"… is added before the
    /// extension (an earlier download of the same range is never overwritten).
    /// </summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        var path = Path.Combine(folder, fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; exists(path) && n < 1000; n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        }
        return path;
    }

    static string Stamp(DateTime time) => time.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
}

/// <summary>What happens to one decoded frame of a download.</summary>
public enum FrameDecision
{
    /// <summary>Before the (part's) start — the recorder starts on the key frame before it, sometimes at the file's start: not written.</summary>
    Skip,

    /// <summary>Inside the range: written with the given presentation time.</summary>
    Write,

    /// <summary>At or after the end of the current part (recording file): this session is done, the next part follows.</summary>
    PartEnd,

    /// <summary>At or after the end of the range: the download is complete.</summary>
    End,
}

/// <summary>
/// The playback sessions of a download: one per recording file (segment of the read-only recording search) inside the
/// range. Played across a file boundary, the DS-8632NI-I8 skips the first ~7 s of the next file (measured: 19:37:19.24 →
/// 19:37:26.03, although asked directly it sends 19:37:19.31 on), so every file gets its own session, clipped to the
/// range, and the files are joined in one MP4. Unknown segments (search failed): one session for the whole range.
/// </summary>
public static class DownloadParts
{
    public static IReadOnlyList<(DateTime Start, DateTime End)> Split(DateTime start, DateTime end, IReadOnlyList<RecordingSegment>? segments)
    {
        if (segments is null)
        {
            return end > start ? [(start, end)] : [];
        }
        var parts = new List<(DateTime Start, DateTime End)>();
        var from = start;
        foreach (var segment in segments.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            var s = segment.Start > from ? segment.Start : from;
            var e = segment.End < end ? segment.End : end;
            if (e <= s)
            {
                continue; // outside the range, or covered by the previous part (overlap)
            }
            parts.Add((s, e));
            from = e;
        }
        return parts;
    }
}

/// <summary>
/// Turns the decoded frames of a download into the MP4's presentation times. The download plays one session per part
/// (<see cref="DownloadParts"/>); <see cref="BeginPart"/> starts each. Frames before the part's start are skipped, the
/// first frame at or after its end ends the session (<see cref="FrameDecision.PartEnd"/>), and the first frame at or
/// after the range's end completes the file. Times follow the recording, so the file plays at real speed however fast
/// it was downloaded: the step between two frames is the stream stamp difference (ms), or the difference of their
/// absolute (OSD) times when the stamp is unusable (a new session restarts the stamps). A step of more than
/// <see cref="MaxGap"/> (no recording in between) advances by one nominal frame instead: the recorded parts are joined
/// without a long frozen picture; the OSD shows the jump. <see cref="Gaps"/> / <see cref="Skipped"/> count them.
/// Not thread-safe: the decode thread of the current session calls <see cref="Next"/>.
/// </summary>
public sealed class DownloadTimeline
{
    /// <summary>Largest believable distance between two frames of one recording.</summary>
    public static TimeSpan MaxGap { get; } = TimeSpan.FromSeconds(3);

    DateTime? _lastTime;
    long _lastStamp;
    long _pts;
    bool _newPart;

    public DownloadTimeline(DateTime start, DateTime end)
    {
        Start = start;
        End = end;
        PartStart = start;
        PartEnd = end;
    }

    public DateTime Start { get; }

    public DateTime End { get; }

    public DateTime PartStart { get; private set; }

    public DateTime PartEnd { get; private set; }

    /// <summary>Absolute time of the first written frame.</summary>
    public DateTime? FirstWritten { get; private set; }

    /// <summary>Absolute time of the last written frame (progress).</summary>
    public DateTime? LastWritten { get; private set; }

    public long FramesWritten { get; private set; }

    public long FramesSkipped { get; private set; }

    /// <summary>Gaps (no recording) joined over.</summary>
    public int Gaps { get; private set; }

    /// <summary>Recorder time left out by the joined gaps.</summary>
    public TimeSpan Skipped { get; private set; }

    /// <summary>The end of the range was reached.</summary>
    public bool Ended { get; private set; }

    /// <summary>The next session covers [<paramref name="start"/>, <paramref name="end"/>) (clipped to the range).</summary>
    public void BeginPart(DateTime start, DateTime end)
    {
        PartStart = start > Start ? start : Start;
        PartEnd = end < End ? end : End;
        _newPart = true;
    }

    /// <summary>Decision and presentation time (ms, from 0, strictly increasing) for a frame.</summary>
    public (FrameDecision Decision, long Pts) Next(DateTime frameTime, long stampMs, int frameRate)
    {
        if (Ended || frameTime >= End)
        {
            Ended = true;
            return (FrameDecision.End, _pts);
        }
        if (frameTime >= PartEnd)
        {
            return (FrameDecision.PartEnd, _pts);
        }
        if (frameTime < PartStart || (_lastTime is { } previous && _newPart && frameTime <= previous))
        {
            FramesSkipped++;
            return (FrameDecision.Skip, _pts);
        }
        if (_lastTime is not { } last)
        {
            _lastTime = frameTime;
            _lastStamp = stampMs;
            _pts = 0;
            _newPart = false;
            FirstWritten = LastWritten = frameTime;
            FramesWritten = 1;
            return (FrameDecision.Write, 0);
        }
        var nominal = FrameClock.NominalInterval(frameRate);
        var wall = frameTime - last;
        long step;
        if (wall > MaxGap)
        {
            Gaps++;
            Skipped += wall;
            step = nominal;
        }
        else
        {
            var stamp = stampMs - _lastStamp;
            step = !_newPart && stamp > 0 && stamp <= (long)MaxGap.TotalMilliseconds ? stamp
                : wall > TimeSpan.Zero ? (long)wall.TotalMilliseconds
                : nominal;
            if (step <= 0)
            {
                step = nominal;
            }
        }
        _newPart = false;
        _lastTime = frameTime > last ? frameTime : last;
        _lastStamp = stampMs;
        _pts += step;
        LastWritten = _lastTime;
        FramesWritten++;
        return (FrameDecision.Write, _pts);
    }
}

/// <summary>Progress of one camera's download, for the dialog.</summary>
/// <param name="Fraction">0…1, by recorded time covered.</param>
/// <param name="Speed">Recorded time downloaded per second of wall time (× real time); null until measurable.</param>
/// <param name="Remaining">Estimated time left; null until measurable.</param>
public sealed record DownloadProgressInfo(double Fraction, double? Speed, TimeSpan? Remaining, DateTime? FrameTime)
{
    public int Percent => (int)Math.Floor(Math.Clamp(Fraction, 0, 1) * 100);
}

/// <summary>
/// Progress by recorded time: how much of the recording inside the range (the read-only recording search's
/// segments; the whole range when they are unknown) lies before the last written frame. Gaps count for nothing, so a
/// range that is mostly empty does not crawl and then jump. Speed is the recorded time covered per wall second
/// since the first frame, the estimate the rest at that speed.
/// </summary>
public sealed class DownloadProgressTracker
{
    readonly List<(DateTime Start, DateTime End)> _parts;

    public DownloadProgressTracker(DateTime start, DateTime end, IReadOnlyList<RecordingSegment>? segments)
    {
        Start = start;
        End = end;
        _parts = Merge(start, end, segments);
        Total = _parts.Aggregate(TimeSpan.Zero, (sum, part) => sum + (part.End - part.Start));
    }

    public DateTime Start { get; }

    public DateTime End { get; }

    /// <summary>Recorded time inside the range (the whole range when the segments are unknown).</summary>
    public TimeSpan Total { get; }

    /// <summary>The range holds no recording at all (known from the search).</summary>
    public bool IsEmpty => Total <= TimeSpan.Zero;

    /// <summary>Recorded time of the range before <paramref name="time"/>.</summary>
    public TimeSpan Covered(DateTime time)
    {
        var covered = TimeSpan.Zero;
        foreach (var (start, end) in _parts)
        {
            if (time <= start)
            {
                break;
            }
            covered += (time < end ? time : end) - start;
        }
        return covered;
    }

    public double Fraction(DateTime time) => Total <= TimeSpan.Zero ? 0 : Math.Clamp(Covered(time) / Total, 0, 1);

    /// <summary>
    /// Progress at the frame time <paramref name="frameTime"/>, <paramref name="sinceFirstFrame"/> of wall time after
    /// the first written frame (speed and estimate need at least a second of it).
    /// </summary>
    public DownloadProgressInfo Measure(DateTime? frameTime, DateTime? firstFrame, TimeSpan sinceFirstFrame)
    {
        if (frameTime is not { } time)
        {
            return new DownloadProgressInfo(0, null, null, null);
        }
        var fraction = Fraction(time);
        double? speed = null;
        TimeSpan? remaining = null;
        if (firstFrame is { } first && sinceFirstFrame >= TimeSpan.FromSeconds(1))
        {
            var done = Covered(time) - Covered(first);
            if (done > TimeSpan.Zero)
            {
                var rate = done / sinceFirstFrame;
                speed = rate;
                remaining = TimeSpan.FromSeconds(Math.Max(0, (Total - Covered(time)).TotalSeconds / rate));
            }
        }
        return new DownloadProgressInfo(fraction, speed, remaining, time);
    }

    static List<(DateTime Start, DateTime End)> Merge(DateTime start, DateTime end, IReadOnlyList<RecordingSegment>? segments)
    {
        if (segments is null)
        {
            return [(start, end)];
        }
        var parts = new List<(DateTime Start, DateTime End)>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            var s = segment.Start < start ? start : segment.Start;
            var e = segment.End > end ? end : segment.End;
            if (e <= s)
            {
                continue;
            }
            if (parts.Count > 0 && s <= parts[^1].End)
            {
                if (e > parts[^1].End)
                {
                    parts[^1] = (parts[^1].Start, e);
                }
            }
            else
            {
                parts.Add((s, e));
            }
        }
        return parts;
    }

    /// <summary>End of the last recording inside the range (the stream ends there); null when unknown or empty.</summary>
    public DateTime? LastRecordedEnd => _parts.Count == 0 ? null : _parts[^1].End;
}

/// <summary>How the download loop goes on, from the stream's activity.</summary>
public enum DownloadState
{
    Running,

    /// <summary>The end is reached, or the stream went quiet after the last recording: finish the file.</summary>
    Finished,

    /// <summary>The recorder sent nothing usable: fail.</summary>
    NoData,

    /// <summary>The stream stopped in the middle of the range: finish what was written, say it is incomplete.</summary>
    Stalled,
}

/// <summary>
/// The recorder sends no end-of-recording signal (no data type, no exception): the download ends at the first frame
/// at or after the range's end, otherwise when the stream has been quiet for a while. Quiet near the end of the last
/// recording is a normal end; quiet in the middle of the range is a stall.
/// </summary>
public static class DownloadEnd
{
    /// <summary>No first frame within this time: the recorder sends nothing for the range.</summary>
    public static TimeSpan FirstFrameWait { get; } = TimeSpan.FromSeconds(25);

    /// <summary>Quiet this long right at the end of the last recording: done.</summary>
    public static TimeSpan EndQuiet { get; } = TimeSpan.FromSeconds(4);

    /// <summary>Quiet this long in the middle of the range (gaps are crossed in about a second): stalled.</summary>
    public static TimeSpan StallQuiet { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Near the last recording's end (the last frames arrive within this much of it).</summary>
    public static TimeSpan EndSlack { get; } = TimeSpan.FromSeconds(3);

    /// <param name="ended">A frame at or after the range's end was decoded.</param>
    /// <param name="lastFrame">Absolute time of the last decoded frame inside the range; null before the first.</param>
    /// <param name="quiet">Wall time since the last packet or decoded frame (time the data path spends waiting for the
    /// encoder does not count: it is not quiet).</param>
    /// <param name="lastRecordedEnd">End of the last recording in the range (search); null when unknown.</param>
    public static DownloadState Decide(bool ended, DateTime? lastFrame, TimeSpan quiet, TimeSpan sinceOpen, DateTime rangeEnd, DateTime? lastRecordedEnd)
    {
        if (ended)
        {
            return DownloadState.Finished;
        }
        if (lastFrame is not { } last)
        {
            return sinceOpen >= FirstFrameWait && quiet >= EndQuiet ? DownloadState.NoData : DownloadState.Running;
        }
        var streamEnd = lastRecordedEnd is { } recorded && recorded < rangeEnd ? recorded : rangeEnd;
        if (last >= streamEnd - EndSlack)
        {
            return quiet >= EndQuiet ? DownloadState.Finished : DownloadState.Running;
        }
        return quiet >= StallQuiet ? DownloadState.Stalled : DownloadState.Running;
    }
}

/// <summary>Turkish texts of the download dialog.</summary>
public static class DownloadText
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public const string NoRecordings = "Bu aralıkta kayıt yok";

    public const string NoAudio = "Ses dahil değil";

    public static string Done(string path) => $"İndirildi: {path}";

    public static string Failed(string reason) => $"İndirilemedi: {reason}";

    public const string Cancelled = "İptal edildi; yarım dosya silindi.";

    public static string Incomplete(DateTime lastFrame) =>
        $"Kayıt cihazı akışı {lastFrame.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}'da kesti; o ana kadarki kısım kaydedildi.";

    public static string Refused(string reason) =>
        $"Kayıt cihazı indirme için oynatma açmadı: {reason}. Eş zamanlı oynatma sınırı dolmuş olabilir; oynatılan kare sayısını azaltıp yeniden deneyin.";

    /// <summary>"%42 · 6,3× · kalan ~1 dk 20 sn".</summary>
    public static string Progress(DownloadProgressInfo info)
    {
        var text = $"%{info.Percent}";
        if (info.Speed is { } speed)
        {
            text += $" · {speed.ToString(speed >= 10 ? "0" : "0.0", Turkish)}×";
        }
        if (info.Remaining is { } remaining)
        {
            text += $" · kalan ~{Duration(remaining)}";
        }
        return text;
    }

    /// <summary>"45 sn", "3 dk 05 sn", "1 sa 02 dk".</summary>
    public static string Duration(TimeSpan time)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, time.TotalSeconds));
        if (seconds < 60)
        {
            return $"{seconds} sn";
        }
        if (seconds < 3600)
        {
            return $"{seconds / 60} dk {seconds % 60:00} sn";
        }
        return $"{seconds / 3600} sa {seconds % 3600 / 60:00} dk";
    }
}

/// <summary>
/// Size estimate of a download, for the free-space check: the files are re-encoded H.264 at the recording writer's rate
/// (<see cref="Mp4VideoWriter"/>: ~1 bit per 12 pixels per frame, 1–8 Mb/s) over the recorded time (gaps cost nothing).
/// </summary>
public static class DownloadEstimate
{
    /// <summary>Kept free on the target drive besides the estimate.</summary>
    public const long Reserve = 512L * 1024 * 1024;

    /// <summary>Container overhead (sample tables, headers) over the raw video bits.</summary>
    const double Overhead = 1.02;

    /// <summary>Encoder bit rate for a picture size / frame rate (25 fps when unknown).</summary>
    public static long BitsPerSecond(int width, int height, int frameRate) =>
        Mp4VideoWriter.Bitrate(width & ~1, height & ~1, frameRate is >= 1 and <= 120 ? frameRate : 25);

    /// <summary>Estimated bytes of one camera's file.</summary>
    public static long Bytes(TimeSpan recorded, long bitsPerSecond) =>
        recorded <= TimeSpan.Zero ? 0 : (long)(recorded.TotalSeconds * bitsPerSecond / 8 * Overhead);

    /// <summary>The estimate (all cameras) fits on a drive with <paramref name="freeBytes"/> free, keeping <see cref="Reserve"/>.</summary>
    public static bool Fits(long estimate, long freeBytes) => estimate + Reserve <= freeBytes;

    /// <summary>"1,4 GB", "820 MB".</summary>
    public static string Text(long bytes)
    {
        var turkish = CultureInfo.GetCultureInfo("tr-TR");
        const double mb = 1024 * 1024;
        return bytes >= 1024 * mb
            ? $"{(bytes / (1024 * mb)).ToString("0.0", turkish)} GB"
            : $"{Math.Max(1, Math.Round(bytes / mb)).ToString("0", turkish)} MB";
    }
}
