using System.Globalization;

namespace SPVision.Core;

/// <summary>How a day cell of the "Video indir" calendar is drawn under the selected range.</summary>
public enum RangeBand
{
    None,

    /// <summary>The only selected day (start = end): accent circle, no band.</summary>
    Single,

    /// <summary>Inside the range, with the band's rounded start (range start, or the week's first column).</summary>
    Start,

    /// <summary>Inside the range, band continues on both sides.</summary>
    Middle,

    /// <summary>Inside the range, with the band's rounded end (range end, or the week's last column).</summary>
    End,

    /// <summary>Rounded on both sides (a range of one week column, e.g. starting on a Sunday and running on).</summary>
    StartEnd,
}

/// <summary>
/// The range picker of the one-calendar "Video indir" panel: the first click picks the start day (and waits for the
/// end), the second the end day (an earlier day swaps them), a third click starts a new selection. Days after today
/// cannot be picked.
/// </summary>
public sealed class DateRangePick
{
    public DateRangePick(DateTime start, DateTime end)
    {
        Start = start.Date;
        End = end.Date < start.Date ? start.Date : end.Date;
    }

    public DateTime Start { get; private set; }

    public DateTime End { get; private set; }

    /// <summary>The start day was clicked; the next click picks the end day.</summary>
    public bool Picking { get; private set; }

    /// <summary>The hint under the calendar.</summary>
    public string Hint => Picking ? "Şimdi bitiş gününü seçin" : "Başlangıç gününe, sonra bitiş gününe tıklayın";

    /// <summary>A click on <paramref name="day"/>; false (nothing changes) for a day after <paramref name="today"/>.</summary>
    public bool Click(DateTime day, DateTime today)
    {
        day = day.Date;
        if (day > today.Date)
        {
            return false;
        }
        if (!Picking)
        {
            Start = End = day;
            Picking = true;
        }
        else
        {
            if (day < Start)
            {
                End = Start;
                Start = day;
            }
            else
            {
                End = day;
            }
            Picking = false;
        }
        return true;
    }

    /// <summary>Range start / end day (the circles).</summary>
    public bool IsEndpoint(DateTime day) => day.Date == Start || day.Date == End;

    /// <summary>Band of a day cell in calendar column <paramref name="column"/> (0 = Monday … 6 = Sunday).</summary>
    public RangeBand Band(DateTime day, int column)
    {
        day = day.Date;
        if (day < Start || day > End)
        {
            return RangeBand.None;
        }
        if (Start == End)
        {
            return RangeBand.Single;
        }
        var left = day == Start || column == 0;
        var right = day == End || column == 6;
        return (left, right) switch
        {
            (true, true) => RangeBand.StartEnd,
            (true, false) => RangeBand.Start,
            (false, true) => RangeBand.End,
            _ => RangeBand.Middle,
        };
    }
}

/// <summary>The time fields of the panel: "ss:dd:ss" (hours 0–23; one-digit hours allowed).</summary>
public static class DownloadTimeText
{
    public static TimeSpan? Parse(string? text)
    {
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length != 3 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2 || parts[2].Length != 2 ||
            !parts.All(p => p.All(char.IsAsciiDigit)))
        {
            return null;
        }
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var s = int.Parse(parts[2], CultureInfo.InvariantCulture);
        return h > 23 || m > 59 || s > 59 ? null : new TimeSpan(h, m, s);
    }

    public static string Format(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"dd.MM.yyyy" for the field labels.</summary>
    public static string Date(DateTime day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}

/// <summary>What the panel's summary box says, and whether İndir is allowed.</summary>
/// <param name="SizeLine">"Tahmini boyut: … · Boş alan: …"; null when no estimate is shown.</param>
public sealed record DownloadSummary(DateTime? Start, DateTime? End, string Text, string? SizeLine, bool CanStart);

/// <summary>
/// Checks the panel's inputs: camera count, the two times (with the calendar's start / end days), the range and the
/// estimated size against the free space of the target drive. No maximum length (multi-day ranges are one file per
/// camera).
/// </summary>
public static class DownloadForm
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <param name="bytesPerSecond">Sum over the checked cameras of the estimated file bytes per recorded second.</param>
    /// <param name="freeBytes">Free space of the target drive; null when unknown (no check).</param>
    public static DownloadSummary Evaluate(DateTime startDay, string? startText, DateTime endDay, string? endText,
        int cameras, double bytesPerSecond, long? freeBytes, DateTime now)
    {
        if (cameras <= 0)
        {
            return new DownloadSummary(null, null, "En az bir kamera seçin.", null, false);
        }
        if (DownloadTimeText.Parse(startText) is not { } a || DownloadTimeText.Parse(endText) is not { } b)
        {
            return new DownloadSummary(null, null, "Saatleri ss:dd:ss biçiminde yazın (ör. 08:30:00).", null, false);
        }
        var start = startDay.Date + a;
        var end = endDay.Date + b;
        if (DownloadRange.Validate(start, end, now) is { } problem)
        {
            return new DownloadSummary(start, end, problem, null, false);
        }
        var text = $"Süre: {Duration(end - start)}  ·  {cameras} kamera";
        var estimate = DownloadEstimate.Bytes(end - start, (long)(bytesPerSecond * 8));
        var sizeLine = $"Tahmini boyut: {DownloadEstimate.Text(estimate)}" +
            (freeBytes is { } free ? $"  ·  Boş alan: {DownloadEstimate.Text(free)}" : "");
        if (freeBytes is { } available && !DownloadEstimate.Fits(estimate, available))
        {
            return new DownloadSummary(start, end, text + "  ·  Diskte yeterli boş alan yok.", sizeLine, false);
        }
        return new DownloadSummary(start, end, text, sizeLine, true);
    }

    /// <summary>"2 gün 3 sa 10 dk", "10 dk", "45 sn".</summary>
    public static string Duration(TimeSpan span)
    {
        var seconds = (long)Math.Max(0, span.TotalSeconds);
        if (seconds < 60)
        {
            return $"{seconds} sn";
        }
        var d = seconds / 86400;
        var h = seconds / 3600 % 24;
        var m = seconds / 60 % 60;
        var parts = new List<string>();
        if (d > 0)
        {
            parts.Add($"{d} gün");
        }
        if (h > 0)
        {
            parts.Add($"{h} sa");
        }
        if (m > 0 || parts.Count == 0)
        {
            parts.Add($"{m} dk");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Estimated file bytes per recorded second of one camera from its main stream (ISAPI StreamingProxy).</summary>
    /// <remarks>
    /// The file is re-encoded (H.264 at the recording writer's rate for the picture size), so the writer's rate decides
    /// the size, not the camera's; the camera's bit rate is used only when its picture size is unknown.
    /// </remarks>
    public static double BytesPerSecond(int width, int height, int frameRateTimes100, int cameraKbps)
    {
        if (width >= 16 && height >= 16)
        {
            var fps = frameRateTimes100 >= 100 ? frameRateTimes100 / 100 : 25;
            return DownloadEstimate.BitsPerSecond(width, height, fps) / 8.0;
        }
        return (cameraKbps > 0 ? cameraKbps * 1000.0 : 4_000_000) / 8;
    }

    /// <summary>The counter at the camera list ("2 / 4").</summary>
    public static string Count(int selected, int total) => $"{selected} / {total}";

    /// <summary>"Ekim 2026".</summary>
    public static string MonthTitle(DateTime month) => CalendarMonth.Title(month);

    internal static string Number(double value) => value.ToString("0.#", Turkish);
}
