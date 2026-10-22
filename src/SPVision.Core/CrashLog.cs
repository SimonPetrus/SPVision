using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SPVision.Core;

/// <summary>
/// Removes secrets from text that leaves the program (crash log, "Sorunu bildir" zip): <c>scheme://user:pass@</c> →
/// <c>scheme://***@</c> (<see cref="StreamUrls.RedactCredentials"/>), and the values of password-like keys
/// (<c>password=</c>, <c>pwd:</c>, JSON <c>"Password": "…"</c>, XML <c>&lt;password&gt;…&lt;/password&gt;</c>, stream keys,
/// tokens, Authorization headers) → <c>***</c>. Unit tested (CrashLogTests).
/// </summary>
public static partial class LogScrubber
{
    public const string Mask = "***";

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var result = StreamUrls.RedactCredentials(text);
        result = AuthorizationHeader().Replace(result, "${key}" + Mask);
        result = XmlSecret().Replace(result, "<${tag}>" + Mask + "</");
        result = KeyValueSecret().Replace(result, "${key}${sep}" + Mask);
        return result;
    }

    // "Authorization: Digest username=…, response=…" — the whole header value.
    [GeneratedRegex(@"(?<key>\b(?:proxy-)?authorization\s*[:=]\s*)[^\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"<(?<tag>(?:\w+:)?(?:password|passwd|pwd|secretKey|streamKey|token))>[^<]*</",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlSecret();

    // key = value, key: value, "key": "value", key='value'. The key must not be part of a longer word ("passwordless" is
    // fine, "user_password=" is masked because '_' separates). Quoted values are masked whole, plain ones up to a separator.
    [GeneratedRegex(@"(?<key>(?<![A-Za-z0-9])(?:password|passwd|passphrase|pwd|pass|secret|token|api[_-]?key|stream[_-]?key|secret[_-]?key)[""']?)(?<sep>\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s&;,""'<>]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecret();
}

/// <summary>
/// <c>logs\crash.log</c>: unhandled exceptions (UI thread, other threads, unobserved tasks) with time, app version, OS and the
/// full exception text, scrubbed (<see cref="LogScrubber"/>). Rolls at <see cref="MaxBytes"/> to <c>crash.log.1</c> (two files
/// kept). Writing never throws: a failing log must not turn a handled error into a crash. Unit tested (CrashLogTests).
/// </summary>
public static class CrashLog
{
    public const string FileName = "crash.log";
    public const long MaxBytes = 1024 * 1024;

    static readonly object Gate = new();
    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One entry; <paramref name="details"/> is normally <c>exception.ToString()</c>.</summary>
    public static string Entry(DateTimeOffset at, string source, string appVersion, string os, string runtime, string details)
    {
        var text = new StringBuilder()
            .Append("==== ").Append(at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" · ").Append(source).AppendLine(" ====")
            .Append("SPVision ").Append(appVersion).Append(" · ").Append(os).Append(" · ").AppendLine(runtime)
            .AppendLine(details.TrimEnd())
            .AppendLine();
        return LogScrubber.Scrub(text.ToString());
    }

    /// <summary>Appends <paramref name="entry"/> (scrubbed again) to <paramref name="directory"/>\crash.log; rolls first when full.</summary>
    public static bool Append(string directory, string entry, long maxBytes = MaxBytes)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, FileName);
                var bytes = Utf8.GetByteCount(entry);
                var current = new FileInfo(path);
                if (current.Exists && current.Length > 0 && current.Length + bytes > maxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true); // the older one goes; two files at most
                }
                File.AppendAllText(path, LogScrubber.Scrub(entry), Utf8);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}
