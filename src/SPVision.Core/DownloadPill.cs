using System.Globalization;

namespace SPVision.Core;

/// <summary>What the pill shows at its width.</summary>
public enum DownloadPillLayout
{
    /// <summary>Not even the button and the percentage fit.</summary>
    Hidden,

    /// <summary>Percentage, bar and İptal (no icon, no camera); the result pill: only "Klasörü aç".</summary>
    Mini,

    /// <summary>Icon, camera, percentage, bar, İptal (no "kamera i / n", no time left).</summary>
    Compact,

    /// <summary>Everything (the approved 440 px design).</summary>
    Full,
}

/// <summary>How a finished download batch went, for the result pill (counts per camera).</summary>
/// <param name="Done">Complete files.</param>
/// <param name="Incomplete">Files the recorder cut short (kept, verified).</param>
/// <param name="Failed">Cameras without a file because of an error.</param>
/// <param name="NoRecordings">Cameras with no recording in the range (nothing opened).</param>
/// <param name="Cancelled">The user pressed İptal (partial files were deleted).</param>
public readonly record struct DownloadResultCounts(int Done, int Incomplete, int Failed, int NoRecordings, bool Cancelled)
{
    public int Files => Done + Incomplete;
}

/// <summary>
/// The download pill in the middle of the main window's top bar: while a batch runs it shows the camera, "kamera i / n",
/// the percentage, the time left and İptal; when it ends a result pill (green when every camera gave a complete file,
/// amber otherwise) with "Klasörü aç", which goes away by itself after <see cref="ResultTimeout"/> (paused while the mouse
/// is over it). It is <see cref="MaxWidth"/> wide, exactly centred in the window, and narrower when the tabs on the left or
/// the tools on the right would be touched.
/// </summary>
public static class DownloadPill
{
    public const double MaxWidth = 440;

    public const double Height = 36;

    /// <summary>Narrower than this the pill is not shown at all (not even "NN%" and the button fit).</summary>
    public const double MinWidth = 120;

    /// <summary>From this width on everything shows (<see cref="DownloadPillLayout.Full"/>).</summary>
    public const double FullWidth = 300;

    /// <summary>From this width on the icon and the camera show (<see cref="DownloadPillLayout.Compact"/>).</summary>
    public const double CompactWidth = 200;

    /// <summary>Space kept to the tabs / tools on either side.</summary>
    public const double Gap = 12;

    public static TimeSpan ResultTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Width of the pill centred in a bar <paramref name="width"/> wide (same unit; the bar is symmetric in the window) when
    /// the left content ends at <paramref name="leftEnd"/> and the right content starts at <paramref name="rightStart"/>:
    /// <see cref="MaxWidth"/>, or as wide as fits between them; 0 when less than <see cref="MinWidth"/> fits.
    /// </summary>
    public static double Width(double width, double leftEnd, double rightStart)
    {
        var centre = width / 2;
        var half = Math.Min(centre - leftEnd, rightStart - centre) - Gap;
        var fit = Math.Min(MaxWidth, Math.Floor(2 * half));
        return fit >= MinWidth ? fit : 0;
    }

    /// <summary>What fits in a pill <paramref name="width"/> wide (<see cref="Width"/>'s result).</summary>
    public static DownloadPillLayout LayoutFor(double width) =>
        width >= FullWidth ? DownloadPillLayout.Full
        : width >= CompactWidth ? DownloadPillLayout.Compact
        : width >= MinWidth ? DownloadPillLayout.Mini
        : DownloadPillLayout.Hidden;

    /// <summary>"kamera 2 / 3" for a batch of several cameras, "İndiriliyor" for one.</summary>
    public static string Step(int number, int count) => count > 1 ? $"kamera {number} / {count}" : "İndiriliyor";

    /// <summary>"42%" (0% until the first frame).</summary>
    public static string Percent(DownloadProgressInfo? progress) => $"{progress?.Percent ?? 0}%";

    /// <summary>"kalan 1:05", "kalan 1:02:09"; empty while unknown.</summary>
    public static string Remaining(TimeSpan? remaining)
    {
        if (remaining is not { } left)
        {
            return "";
        }
        var seconds = (long)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return seconds >= 3600
            ? $"kalan {seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
            : $"kalan {seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>
    /// The result pill: success (green) only when every camera with a recording gave a complete file; otherwise a warning
    /// with the short reason ("İndirme bitti · 2 dosya kaydedildi · 1 dosya eksik indirildi", "Bu aralıkta kayıt yok").
    /// </summary>
    public static (bool Success, string Text) Result(DownloadResultCounts counts)
    {
        var files = counts.Files;
        if (counts.Cancelled)
        {
            return (false, "İndirme iptal edildi" + (files > 0 ? $" · {Saved(files)}" : ""));
        }
        var problems = new List<string>();
        if (counts.Incomplete > 0)
        {
            problems.Add($"{counts.Incomplete} dosya eksik indirildi");
        }
        if (counts.Failed > 0)
        {
            problems.Add($"{counts.Failed} kamera indirilemedi");
        }
        if (counts.NoRecordings > 0)
        {
            problems.Add(files == 0 && problems.Count == 0 ? "Bu aralıkta kayıt yok" : $"{counts.NoRecordings} kamerada kayıt yok");
        }
        if (files == 0)
        {
            return (false, string.Join(" · ", problems.DefaultIfEmpty("Hiçbir dosya kaydedilmedi")));
        }
        var success = counts.Incomplete == 0 && counts.Failed == 0;
        // Files that are complete are named first when some others are not ("2 dosya kaydedildi · 1 dosya eksik …").
        var saved = success ? Saved(files) : counts.Done > 0 ? Saved(counts.Done) : null;
        return (success, string.Join(" · ", new[] { "İndirme bitti", saved }.Concat(problems).Where(p => p is not null)));
    }

    static string Saved(int files) => $"{files} dosya kaydedildi";

    /// <summary>
    /// What "Klasörü aç" shows: one file → that file selected in Explorer; several → their folder (the common one, else the
    /// batch's); none → the batch's folder.
    /// </summary>
    public static (string Path, bool Select) OpenTarget(IReadOnlyList<string> files, string folder)
    {
        if (files.Count == 1)
        {
            return (files[0], true);
        }
        var folders = files.Select(f => System.IO.Path.GetDirectoryName(f) ?? folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (folders.Count == 1 ? folders[0] : folder, false);
    }

    /// <summary>Tool tip line of one camera ("DIS KAPI: İndirildi: C:\…\x.mp4").</summary>
    public static string Detail(string camera, string status) => string.Create(CultureInfo.InvariantCulture, $"{camera}: {status}");
}
