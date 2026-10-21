namespace SPVision.Core;

/// <summary>Default per-user folders.</summary>
public static class AppPaths
{
    public const string AppName = "SPVision";

    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    static string Videos => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>%APPDATA%\SPVision\settings.json (logs live next to it under <c>logs\</c>).</summary>
    public static string SettingsFile => Path.Combine(AppData, AppName, "settings.json");

    public static string DefaultSnapshotFolder => Path.Combine(Pictures, AppName);

    public static string DefaultRecordingFolder => Path.Combine(Videos, AppName);
}
