using System.Globalization;

namespace SPVision.Core;

/// <summary>Short device type text for the Settings device list, e.g. "NVR · 32 kanal" or "IP kamera".</summary>
public static class DeviceKind
{
    public static string Describe(Device device)
    {
        var type = device.DeviceType?.Trim() ?? "";
        string? kind =
            Contains(type, "NVR") ? "NVR" :
            Contains(type, "DVR") ? "DVR" :
            Contains(type, "IPC") || Contains(type, "IPCamera") || Contains(type, "Camera") || Contains(type, "IPDome") ? "IP kamera" :
            null;
        if (kind == "IP kamera")
        {
            return kind;
        }
        kind ??= "Kayıt cihazı";
        return device.ChannelCount is > 0 and var channels
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} · {channels} kanal")
            : kind;
    }

    static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}
