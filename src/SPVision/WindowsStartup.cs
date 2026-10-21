using Microsoft.Win32;
using SPVision.Core;

namespace SPVision;

/// <summary>"Windows ile birlikte başlat": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
static class WindowsStartup
{
    static string ExePath => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "SPVision.exe");

    /// <summary>True when the Run value exists and starts this exe.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStart.RunKey);
            return AutoStart.PointsTo(key?.GetValue(AutoStart.ValueName) as string, ExePath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Writes or deletes the Run value. Throws when the registry cannot be written.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AutoStart.RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(AutoStart.ValueName, AutoStart.Command(ExePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AutoStart.ValueName, throwOnMissingValue: false);
        }
    }
}
