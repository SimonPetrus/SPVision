using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SPVision.Core;

/// <summary>What sistem.txt says about one recorder: model, firmware and camera count only — never name, address or login.</summary>
public sealed record ReportDevice(string? Model, string? Firmware, int Cameras)
{
    /// <summary><paramref name="details"/>: a fresh deviceInfo read, or null when the recorder did not answer.</summary>
    public static ReportDevice From(Device device, DeviceDetails? details) =>
        new(details?.Model is { Length: > 0 } model ? model : device.Model, details?.Firmware, device.Cameras.Count);
}

/// <summary>Inputs of sistem.txt (gathered by the app; this class only formats).</summary>
public sealed record ReportSystem(
    string AppVersion,
    string Os,
    string Runtime,
    IReadOnlyList<string> Gpus,
    bool HardwareDecoding,
    IReadOnlyList<ReportDevice> Devices);

/// <summary>
/// Ayarlar › Uygulama ayarları › "Sorunu bildir…": a zip with the app's own logs (<c>crash.log*</c>, <c>shutdown.log</c>,
/// <c>player.log*</c>, each through <see cref="LogScrubber"/>) and a generated <c>sistem.txt</c>. Only those names directly in
/// the logs folder are taken: settings.json, the SDK logs (<c>logs\sdk\</c>), certificate pins and credentials never go in.
/// Unit tested (ProblemReportTests).
/// </summary>
public static class ProblemReport
{
    public const string SystemFileName = "sistem.txt";

    /// <summary>A log bigger than this is cut to its last part (the newest lines matter).</summary>
    public const int MaxLogBytes = 4 * 1024 * 1024;

    public static string DefaultFileName(DateTime now) =>
        "SPVision-sorun-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>crash.log, crash.log.1, shutdown.log, player.log, player.log.1 / player.1.log … — nothing else.</summary>
    public static bool IsLogIncluded(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        if (name != Path.GetFileName(name))
        {
            return false;
        }
        return name == "shutdown.log" || IsRolled(name, "crash") || IsRolled(name, "player");
    }

    // "<base>.log", "<base>.log.N" or "<base>.N.log" (N digits).
    static bool IsRolled(string name, string stem)
    {
        if (name == stem + ".log")
        {
            return true;
        }
        if (name.StartsWith(stem + ".log.", StringComparison.Ordinal))
        {
            return name[(stem.Length + 5)..] is { Length: > 0 } n && n.All(char.IsAsciiDigit);
        }
        if (name.StartsWith(stem + ".", StringComparison.Ordinal) && name.EndsWith(".log", StringComparison.Ordinal))
        {
            var middle = name[(stem.Length + 1)..^4];
            return middle.Length > 0 && middle.All(char.IsAsciiDigit);
        }
        return false;
    }

    public static string SystemText(ReportSystem system, DateTimeOffset createdAt)
    {
        var text = new StringBuilder()
            .AppendLine("SPVision sorun raporu")
            .AppendLine("Oluşturulma: " + createdAt.ToString("dd.MM.yyyy HH:mm zzz", CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("Uygulama sürümü: " + system.AppVersion)
            .AppendLine("İşletim sistemi: " + system.Os)
            .AppendLine(".NET: " + system.Runtime)
            .AppendLine("Ekran kartı: " + (system.Gpus.Count > 0 ? string.Join("; ", system.Gpus) : "bilinmiyor"))
            .AppendLine("Donanım hızlandırma: " + RecorderText.OnOff(system.HardwareDecoding))
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture, $"Kayıt cihazları: {system.Devices.Count}"));
        for (var i = 0; i < system.Devices.Count; i++)
        {
            var device = system.Devices[i];
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1}. Model: {Or(device.Model, "bilinmiyor")} · Yazılım: {Or(device.Firmware, "okunamadı")} · Kamera: {device.Cameras}"));
        }
        return LogScrubber.Scrub(text.ToString());
    }

    static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>
    /// Writes the zip to <paramref name="zipPath"/> (replaced if it exists): the included logs of <paramref name="logDirectory"/>
    /// (top level only, scrubbed, at most <see cref="MaxLogBytes"/> each) + sistem.txt. Returns the entry names.
    /// </summary>
    public static IReadOnlyList<string> Write(string zipPath, string logDirectory, string systemText)
    {
        var names = new List<string>();
        using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        if (Directory.Exists(logDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(logDirectory, "*", SearchOption.TopDirectoryOnly)
                         .Where(p => IsLogIncluded(Path.GetFileName(p)))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (ReadTail(path) is not { } content)
                {
                    continue;
                }
                AddText(zip, Path.GetFileName(path), LogScrubber.Scrub(content));
                names.Add(Path.GetFileName(path));
            }
        }
        AddText(zip, SystemFileName, LogScrubber.Scrub(systemText));
        names.Add(SystemFileName);
        return names;
    }

    static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    /// <summary>The file as text (shared read: the player log is open for writing); null when it cannot be read.</summary>
    static string? ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var cut = stream.Length > MaxLogBytes;
            if (cut)
            {
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return cut ? "(… dosyanın başı kesildi; son kısmı)" + Environment.NewLine + text : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
