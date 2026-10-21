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

    /// <summary>
    /// What the field means as typed: <see cref="Parse"/> after <see cref="TimeFieldMask.Complete"/> ("17:52:" → 17:52:00,
    /// "9" → 09:00:00), so İndir already works before the field is left.
    /// </summary>
    public static TimeSpan? ParseInput(string? text) => Parse(TimeFieldMask.Complete(text));

    public static string Format(DateTime time) => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"dd.MM.yyyy" for the field labels.</summary>
    public static string Date(DateTime day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}

/// <summary>The field's text and where the caret goes after an edit.</summary>
public readonly record struct TimeFieldEdit(string Text, int Caret);

/// <summary>
/// The "ss:dd:ss" time fields type like a masked box: only digits count (at most 6); the colons are put in by the field
/// itself, right after the 2nd and the 4th digit ("17" shows "17:", "1752" shows "17:52:", the caret after the colon).
/// A typed colon (or "." / space) after one digit of a group pads it ("9:" → "09:"), otherwise it is skipped; Backspace /
/// Delete remove the digit before / after the caret, stepping over a colon; pasted "175200", "17:52:00" or "17.52.00" all
/// give "17:52:00". Leaving the field completes a shorter input (<see cref="Complete"/>). Positions are character indexes
/// in the text, a selection is (start, length).
/// </summary>
public static class TimeFieldMask
{
    public const int MaxDigits = 6;

    /// <summary>Digits of the text, at most <see cref="MaxDigits"/>.</summary>
    public static string Digits(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length > MaxDigits ? digits[..MaxDigits] : digits;
    }

    /// <summary>The masked text of some digits: a colon after the 2nd and the 4th.</summary>
    public static string Format(string digits)
    {
        var text = new System.Text.StringBuilder(8);
        for (var i = 0; i < digits.Length && i < MaxDigits; i++)
        {
            text.Append(digits[i]);
            if (i is 1 or 3)
            {
                text.Append(':');
            }
        }
        return text.ToString();
    }

    /// <summary>Caret position in the masked text after <paramref name="digits"/> digits (after a following colon).</summary>
    public static int CaretAfter(int digits) => digits + (digits >= 2 ? 1 : 0) + (digits >= 4 ? 1 : 0);

    /// <summary>How many digits stand before text position <paramref name="position"/>.</summary>
    static int DigitsBefore(string text, int position) =>
        text.Take(Math.Clamp(position, 0, text.Length)).Count(char.IsAsciiDigit);

    static TimeFieldEdit Render(string digits, int caretDigits) =>
        new(Format(digits), CaretAfter(Math.Clamp(caretDigits, 0, digits.Length)));

    /// <summary>
    /// Typed or pasted <paramref name="input"/> replacing the selection. Digits are inserted (a full field is overwritten
    /// from the caret on); colons, dots and spaces are separators; anything else is ignored.
    /// </summary>
    public static TimeFieldEdit Insert(string text, int selectionStart, int selectionLength, string input)
    {
        var digits = Digits(text);
        var a = Math.Min(DigitsBefore(text, selectionStart), digits.Length);
        var b = Math.Min(DigitsBefore(text, selectionStart + selectionLength), digits.Length);
        // A pasted time with separators ("8:30:00", "17.52") is read as a time first, so its groups keep their meaning.
        var typed = input.Length > 1 && input.Any(IsSeparator) && input.Any(char.IsAsciiDigit) && Groups(input.Trim()) is { } groups
            ? string.Concat(groups)
            : new string(input.Where(char.IsAsciiDigit).ToArray());
        if (typed.Length == 0)
        {
            if (input.Any(IsSeparator) && selectionLength == 0 && a is 1 or 3 && a == digits.Length && digits.Length < MaxDigits)
            {
                // "9:" — one digit of the hour / minutes, then a separator: it was meant as "09:".
                return Render(digits[..(a - 1)] + "0" + digits[(a - 1)..], a + 1);
            }
            return Render(digits, b); // a separator only moves past the selection / colon
        }
        typed = typed.Length > MaxDigits ? typed[..MaxDigits] : typed;
        var tail = digits[b..];
        var overflow = a + typed.Length + tail.Length - MaxDigits;
        if (overflow > 0)
        {
            tail = tail[Math.Min(overflow, tail.Length)..]; // overwrite what follows the caret
        }
        var result = digits[..a] + typed + tail;
        result = result.Length > MaxDigits ? result[..MaxDigits] : result;
        return Render(result, Math.Min(a + typed.Length, result.Length));
    }

    /// <summary>Backspace: the selection, or the digit before the caret (over a colon: the digit before it).</summary>
    public static TimeFieldEdit Backspace(string text, int selectionStart, int selectionLength)
    {
        var digits = Digits(text);
        var a = Math.Min(DigitsBefore(text, selectionStart), digits.Length);
        if (selectionLength > 0)
        {
            var b = Math.Min(DigitsBefore(text, selectionStart + selectionLength), digits.Length);
            return Render(digits[..a] + digits[b..], a);
        }
        return a == 0 ? Render(digits, 0) : Render(digits[..(a - 1)] + digits[a..], a - 1);
    }

    /// <summary>Delete: the selection, or the digit after the caret (over a colon: the digit after it).</summary>
    public static TimeFieldEdit Delete(string text, int selectionStart, int selectionLength)
    {
        if (selectionLength > 0)
        {
            return Backspace(text, selectionStart, selectionLength);
        }
        var digits = Digits(text);
        var a = Math.Min(DigitsBefore(text, selectionStart), digits.Length);
        return a >= digits.Length ? Render(digits, a) : Render(digits[..a] + digits[(a + 1)..], a);
    }

    /// <summary>Any other change (cut, undo, …): the text masked again, the caret kept after the same digits.</summary>
    public static TimeFieldEdit Normalize(string text, int caret)
    {
        var digits = Digits(text);
        return Render(digits, DigitsBefore(text, caret));
    }

    /// <summary>
    /// Leaving the field: a shorter input is completed to "ss:dd:ss" — "1752" → "17:52:00", "17" → "17:00:00",
    /// "9" → "09:00:00", "175" → "17:50:00". Empty stays empty (and invalid). Validity is <see cref="DownloadTimeText.Parse"/>'s.
    /// </summary>
    public static string Complete(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Any(IsSeparator) && Groups(trimmed) is { } groups)
        {
            return string.Join(':', groups);
        }
        var digits = Digits(trimmed);
        if (digits.Length == 0)
        {
            return "";
        }
        if (digits.Length == 1)
        {
            digits = "0" + digits;
        }
        digits = digits.PadRight(MaxDigits, '0');
        return $"{digits[..2]}:{digits[2..4]}:{digits[4..]}";
    }

    static bool IsSeparator(char c) => c is ':' or '.' or ' ';

    /// <summary>
    /// Text with separators read group by group ("8:30:00" → 08 30 00, "17:5" → 17 50 00: a one-digit hour or finished
    /// group is padded in front, the group being typed at the end behind); null when it is not 1–3 groups of 1–2 digits.
    /// </summary>
    static string[]? Groups(string text)
    {
        var parts = text.Split(':', '.', ' ').ToList();
        while (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1); // "17:52:" — the colon the mask put in
        }
        if (parts.Count is < 1 or > 3 || parts.Any(p => p.Length is < 1 or > 2 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }
        var groups = new string[3];
        for (var i = 0; i < 3; i++)
        {
            groups[i] = i >= parts.Count ? "00"
                : parts[i].Length == 2 ? parts[i]
                : i == 0 || i < parts.Count - 1 ? "0" + parts[i]
                : parts[i] + "0";
        }
        return groups;
    }
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
        if (DownloadTimeText.ParseInput(startText) is not { } a || DownloadTimeText.ParseInput(endText) is not { } b)
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
