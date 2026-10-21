namespace SPVision.Core;

/// <summary>"Windows ile birlikte başlat": the value written under HKCU\...\Run (the registry access lives in the app).</summary>
public static class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "SPVision";

    /// <summary>Quoted exe path, so a path with spaces starts correctly.</summary>
    public static string Command(string exePath) => $"\"{exePath}\"";

    /// <summary>True when the Run value starts this exe (quoted or not, case-insensitive).</summary>
    public static bool PointsTo(string? runValue, string exePath)
    {
        if (string.IsNullOrWhiteSpace(runValue))
        {
            return false;
        }
        var value = runValue.Trim();
        var path = value.StartsWith('"') && value.IndexOf('"', 1) is var end and > 0 ? value[1..end] : value;
        return string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase);
    }
}
