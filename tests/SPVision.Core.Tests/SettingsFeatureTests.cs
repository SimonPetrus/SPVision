using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Theme resolution, per-camera list visibility, device type text and the autostart value.</summary>
public class SettingsFeatureTests
{
    [Theory]
    [InlineData(AppTheme.Light, null, EffectiveTheme.Light)]
    [InlineData(AppTheme.Light, false, EffectiveTheme.Light)]
    [InlineData(AppTheme.Dark, true, EffectiveTheme.Dark)]
    [InlineData(AppTheme.Dark, null, EffectiveTheme.Dark)]
    [InlineData(AppTheme.System, true, EffectiveTheme.Light)]
    [InlineData(AppTheme.System, false, EffectiveTheme.Dark)]
    [InlineData(AppTheme.System, null, EffectiveTheme.Light)]
    public void ResolvesEffectiveTheme(AppTheme setting, bool? systemUsesLight, EffectiveTheme expected) =>
        Assert.Equal(expected, ThemeResolver.Resolve(setting, systemUsesLight));

    [Fact]
    public void ReadsAppsUseLightThemeRegistryValue()
    {
        Assert.True(ThemeResolver.SystemUsesLight(1));
        Assert.False(ThemeResolver.SystemUsesLight(0));
        Assert.Null(ThemeResolver.SystemUsesLight(null));
        Assert.Null(ThemeResolver.SystemUsesLight("1"));
        Assert.Null(ThemeResolver.SystemUsesLight(7));
    }

    [Fact]
    public void HiddenCamerasAreLeftOutOfTheList()
    {
        var device = new Device
        {
            Cameras =
            [
                new Camera { Channel = 2, Name = "B" },
                new Camera { Channel = 1, Name = "A", Hidden = true },
                new Camera { Channel = 3, Name = "C" },
            ],
        };

        Assert.Equal([2, 3], device.ListedCameras().Select(c => c.Channel));
    }

    [Fact]
    public void NewSettingsPersistAndOldFilesGetDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings
            {
                Theme = AppTheme.Dark,
                ShowMotionBoxes = false,
                RestoreLastView = false,
                StartFullScreen = true,
                Devices = [new Device { Host = "10.0.0.1", Cameras = [new Camera { Channel = 1, Hidden = true }, new Camera { Channel = 2 }] }],
            };

            store.Save(settings);
            var json = File.ReadAllText(path);
            var loaded = store.Load();

            Assert.Contains("\"Theme\": \"Dark\"", json);
            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.False(loaded.ShowMotionBoxes);
            Assert.False(loaded.RestoreLastView);
            Assert.True(loaded.StartFullScreen);
            Assert.True(loaded.Devices[0].Cameras[0].Hidden);
            Assert.False(loaded.Devices[0].Cameras[1].Hidden);

            // A settings.json written before these options existed keeps today's behaviour.
            File.WriteAllText(path, """{ "Devices": [ { "Host": "10.0.0.1", "Cameras": [ { "Channel": 1, "Name": "A" } ] } ] }""");
            var old = store.Load();
            Assert.Equal(AppTheme.System, old.Theme);
            Assert.True(old.ShowMotionBoxes);
            Assert.True(old.RestoreLastView);
            Assert.False(old.StartFullScreen);
            Assert.False(old.Devices[0].Cameras[0].Hidden);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MergeKeepsUserFlagsAndCameraObjects()
    {
        var kept = new Camera { Channel = 1, Name = "OLD", StreamKey = "KEY1", Hidden = true };
        var existing = new List<Camera> { kept, new Camera { Channel = 5, Name = "GONE" } };
        var fetched = new[]
        {
            new Camera { Channel = 3, Name = "NEW", Online = false },
            new Camera { Channel = 1, Name = "RENAMED", Online = false },
        };

        var merged = CameraList.Merge(existing, fetched);

        Assert.Equal([1, 3], merged.Select(c => c.Channel));
        Assert.Same(kept, merged[0]);
        Assert.Equal("RENAMED", kept.Name);
        Assert.False(kept.Online);
        Assert.Equal("KEY1", kept.StreamKey);
        Assert.True(kept.Hidden);
        Assert.False(merged[1].Hidden);
    }

    [Theory]
    [InlineData("NVR", 32, "NVR · 32 kanal")]
    [InlineData("NVR", null, "NVR")]
    [InlineData("DVR", 16, "DVR · 16 kanal")]
    [InlineData("IPCamera", null, "IP kamera")]
    [InlineData("IPC", 1, "IP kamera")]
    [InlineData(null, null, "Kayıt cihazı")]
    [InlineData(null, 8, "Kayıt cihazı · 8 kanal")]
    public void DescribesDeviceType(string? deviceType, int? channels, string expected) =>
        Assert.Equal(expected, DeviceKind.Describe(new Device { DeviceType = deviceType, ChannelCount = channels }));

    [Fact]
    public void AutoStartValueQuotesAndMatchesThePath()
    {
        const string exe = @"C:\Program Files\SPVision\SPVision.exe";

        Assert.Equal("\"C:\\Program Files\\SPVision\\SPVision.exe\"", AutoStart.Command(exe));
        Assert.True(AutoStart.PointsTo(AutoStart.Command(exe), exe));
        Assert.True(AutoStart.PointsTo(@"c:\program files\spvision\spvision.exe", exe));
        Assert.False(AutoStart.PointsTo(@"""C:\Other\SPVision.exe""", exe));
        Assert.False(AutoStart.PointsTo(null, exe));
    }
}
