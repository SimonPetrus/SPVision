namespace SPVision.Core;

/// <summary>
/// Default per-user folders. The app was called "SP-2026 Lite" before it was renamed to SPVision;
/// the <c>Legacy*</c> members point at the folders that name used, for the one-time migration in
/// <see cref="LegacyMigration"/>.
/// </summary>
public static class AppPaths
{
    public const string AppName = "SPVision";

    /// <summary>Former product name (data folders only; never shown in the UI).</summary>
    public const string LegacyAppName = "SP-2026 Lite";

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>%APPDATA%\SPVision\settings.json (logs live next to it under <c>logs\</c>).</summary>
    public static string SettingsFile => Path.Combine(AppData, AppName, "settings.json");

    public static string DefaultSnapshotFolder => Path.Combine(Pictures, AppName);

    public static string DefaultRecordingFolder => Path.Combine(Videos, AppName);

    public static string LegacySettingsFile => Path.Combine(AppData, LegacyAppName, "settings.json");

    public static string LegacySnapshotFolder => Path.Combine(Pictures, LegacyAppName);

    public static string LegacyRecordingFolder => Path.Combine(Videos, LegacyAppName);
}
