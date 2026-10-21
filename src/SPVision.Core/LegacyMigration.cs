namespace SPVision.Core;

/// <summary>
/// One-time carry-over from the app's former name ("SP-2026 Lite"). The old folders are never moved or
/// deleted: the settings file is copied, and old snapshots/recordings stay where they are.
/// </summary>
public static class LegacyMigration
{
    /// <summary>
    /// Copies <paramref name="legacySettingsPath"/> to <paramref name="settingsPath"/> when the new file does not
    /// exist yet and the old one does. Returns true if a copy was made. Never overwrites, never deletes.
    /// </summary>
    public static bool CopySettingsIfMissing(string settingsPath, string legacySettingsPath)
    {
        if (File.Exists(settingsPath) || !File.Exists(legacySettingsPath))
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
        try
        {
            File.Copy(legacySettingsPath, settingsPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(settingsPath))
        {
            return false; // created concurrently; keep the existing file
        }
    }

    /// <summary>
    /// Points the snapshot/recording folders at the new defaults when they still hold the old defaults, so new
    /// files go to the new folders. Folders the user chose themselves are kept. Returns true if anything changed.
    /// </summary>
    public static bool UpdateLegacyFolders(AppSettings settings,
        string legacySnapshotFolder, string snapshotFolder,
        string legacyRecordingFolder, string recordingFolder)
    {
        var changed = false;
        if (SamePath(settings.SnapshotFolder, legacySnapshotFolder))
        {
            settings.SnapshotFolder = snapshotFolder;
            changed = true;
        }
        if (SamePath(settings.RecordingFolder, legacyRecordingFolder))
        {
            settings.RecordingFolder = recordingFolder;
            changed = true;
        }
        return changed;
    }

    /// <summary><see cref="UpdateLegacyFolders(AppSettings, string, string, string, string)"/> with the real default folders.</summary>
    public static bool UpdateLegacyFolders(AppSettings settings) => UpdateLegacyFolders(settings,
        AppPaths.LegacySnapshotFolder, AppPaths.DefaultSnapshotFolder,
        AppPaths.LegacyRecordingFolder, AppPaths.DefaultRecordingFolder);

    static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a))
        {
            return false;
        }
        try
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
