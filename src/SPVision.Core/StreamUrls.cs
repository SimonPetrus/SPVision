using System.Globalization;
using System.Text.RegularExpressions;

namespace SPVision.Core;

/// <summary>Builds Hikvision RTSP URLs. Device times are the recorder's local wall-clock time.</summary>
public static partial class StreamUrls
{
    const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string Live(Device device, int channel, StreamQuality quality) =>
        $"{Base(device)}/Streaming/Channels/{TrackId(channel, quality)}";

    /// <summary>
    /// Playback by time. Hikvision recorders read the "Z" suffixed timestamps as their own local time,
    /// which is what both the old client and the web UI send.
    /// </summary>
    public static string Playback(Device device, int channel, DateTime start, DateTime end) =>
        $"{Base(device)}/Streaming/tracks/{TrackId(channel, StreamQuality.Main)}" +
        $"?starttime={start.ToString(TimeFormat, CultureInfo.InvariantCulture)}" +
        $"&endtime={end.ToString(TimeFormat, CultureInfo.InvariantCulture)}";

    public static int TrackId(int channel, StreamQuality quality) => channel * 100 + (int)quality;

    /// <summary>Same URL with the password replaced, for logs and error messages.</summary>
    public static string Redact(string url, Device device) =>
        string.IsNullOrEmpty(device.Password) ? url : url.Replace(Uri.EscapeDataString(device.Password), "***");

    /// <summary>
    /// Any text with the user:password@ part of every URL in it replaced by "***@" (FFmpeg / Flyleaf errors may
    /// quote the RTSP URL). For everything shown in the UI or logged; apply before shortening the text.
    /// </summary>
    public static string RedactCredentials(string? text) =>
        string.IsNullOrEmpty(text) ? "" : UserInfo().Replace(text, "${scheme}***@");

    // Up to the last '@' before the path, query or a space: an unescaped '@' in the password is covered too.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.\-]*://)[^\s/?#]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();

    static string Base(Device device) =>
        $"rtsp://{Uri.EscapeDataString(device.UserName)}:{Uri.EscapeDataString(device.Password)}@{device.Host}:{device.RtspPort}";
}
