using System.Security.Cryptography;
using System.Text;
using SPVision.Core;

namespace SPVision.Core.Tests;

public sealed class LegacyMigrationTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"spvision-migration-{Guid.NewGuid()}");

    string LegacySettings => Path.Combine(_root, "AppData", AppPaths.LegacyAppName, "settings.json");
    string NewSettings => Path.Combine(_root, "AppData", AppPaths.AppName, "settings.json");

    public LegacyMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void WriteLegacy(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacySettings)!);
        File.WriteAllText(LegacySettings, content);
    }

    [Fact]
    public void CopiesLegacySettingsWhenNewFileIsMissing()
    {
        WriteLegacy("{\"old\":true}");

        Assert.True(LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings));

        Assert.Equal("{\"old\":true}", File.ReadAllText(NewSettings));
        Assert.True(File.Exists(LegacySettings)); // copied, not moved
        Assert.Equal("{\"old\":true}", File.ReadAllText(LegacySettings));
    }

    [Fact]
    public void NeverOverwritesExistingNewSettings()
    {
        WriteLegacy("{\"old\":true}");
        Directory.CreateDirectory(Path.GetDirectoryName(NewSettings)!);
        File.WriteAllText(NewSettings, "{\"new\":true}");

        Assert.False(LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings));

        Assert.Equal("{\"new\":true}", File.ReadAllText(NewSettings));
    }

    [Fact]
    public void DoesNothingWithoutLegacySettings()
    {
        Assert.False(LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings));

        Assert.False(File.Exists(NewSettings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(NewSettings)));
    }

    [Fact]
    public void RunsOnlyOnce()
    {
        WriteLegacy("{\"v\":1}");
        Assert.True(LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings));

        WriteLegacy("{\"v\":2}");
        Assert.False(LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings));

        Assert.Equal("{\"v\":1}", File.ReadAllText(NewSettings));
    }

    [Fact]
    public void MigratedSecretsStillDecrypt()
    {
        var device = new Device { Name = "NVR", Host = "10.0.0.1", UserName = "admin", Password = "test-pass", StreamKey = "TESTKEY" };
        device.Cameras.Add(new Camera { Channel = 1, Name = "Cam", StreamKey = "CAMKEY" });
        new SettingsStore(LegacySettings).Save(new AppSettings { Devices = [device] });

        LegacyMigration.CopySettingsIfMissing(NewSettings, LegacySettings);
        var loaded = new SettingsStore(NewSettings).Load().Devices.Single();

        Assert.Equal("test-pass", loaded.Password);
        Assert.Equal("TESTKEY", loaded.StreamKey);
        Assert.Equal("CAMKEY", loaded.Cameras[0].StreamKey);
    }

    [Fact]
    public void DpapiEntropyIsTheFormerAppName()
    {
        // Secrets written by "SP-2026 Lite" builds were protected with this exact entropy.
        var legacyEntropy = Encoding.UTF8.GetBytes("SP-2026 Lite");
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes("secret"), legacyEntropy, DataProtectionScope.CurrentUser);

        Assert.Equal("secret", SettingsStore.Unprotect("dpapi:" + Convert.ToBase64String(blob)));
    }

    [Fact]
    public void LegacyDefaultFoldersMoveToNewDefaults()
    {
        var oldPictures = Path.Combine(_root, "Pictures", AppPaths.LegacyAppName);
        var newPictures = Path.Combine(_root, "Pictures", AppPaths.AppName);
        var oldVideos = Path.Combine(_root, "Videos", AppPaths.LegacyAppName);
        var newVideos = Path.Combine(_root, "Videos", AppPaths.AppName);
        var settings = new AppSettings { SnapshotFolder = oldPictures.ToUpperInvariant() + "\\", RecordingFolder = oldVideos };

        Assert.True(LegacyMigration.UpdateLegacyFolders(settings, oldPictures, newPictures, oldVideos, newVideos));

        Assert.Equal(newPictures, settings.SnapshotFolder);
        Assert.Equal(newVideos, settings.RecordingFolder);
        Assert.False(LegacyMigration.UpdateLegacyFolders(settings, oldPictures, newPictures, oldVideos, newVideos));
    }

    [Fact]
    public void CustomFoldersAreKept()
    {
        var custom = Path.Combine(_root, "Elsewhere");
        var settings = new AppSettings { SnapshotFolder = custom, RecordingFolder = custom };

        Assert.False(LegacyMigration.UpdateLegacyFolders(settings,
            Path.Combine(_root, "Pictures", AppPaths.LegacyAppName), Path.Combine(_root, "Pictures", AppPaths.AppName),
            Path.Combine(_root, "Videos", AppPaths.LegacyAppName), Path.Combine(_root, "Videos", AppPaths.AppName)));

        Assert.Equal(custom, settings.SnapshotFolder);
        Assert.Equal(custom, settings.RecordingFolder);
    }

    [Fact]
    public void NewDefaultsUseTheNewName()
    {
        var settings = new AppSettings();

        Assert.EndsWith(Path.DirectorySeparatorChar + "SPVision", settings.SnapshotFolder);
        Assert.EndsWith(Path.DirectorySeparatorChar + "SPVision", settings.RecordingFolder);
        Assert.Equal(Path.Combine("SPVision", "settings.json"),
            Path.Combine(Path.GetFileName(Path.GetDirectoryName(new SettingsStore().Path))!, "settings.json"));
    }
}
