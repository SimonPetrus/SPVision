using SP2026Lite.Core;

namespace SP2026Lite.Core.Tests;

public class StreamUrlsAndSettingsTests
{
    static Device SampleDevice() => new()
    {
        Name = "NVR",
        Host = "192.168.1.2",
        UserName = "admin",
        Password = "p@ss:w/rd",
        StreamKey = "ABCDEF",
        Cameras = [new Camera { Channel = 3, Name = "HESAP MASA", StreamKey = "QWERTY" }, new Camera { Channel = 4, Name = "CILA 2" }],
    };

    [Fact]
    public void LiveUrlsUseMainAndSubTracks()
    {
        var device = SampleDevice();

        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@192.168.1.2:554/Streaming/Channels/301",
            StreamUrls.Live(device, 3, StreamQuality.Main));
        Assert.EndsWith("/Streaming/Channels/1202", StreamUrls.Live(device, 12, StreamQuality.Sub));
    }

    [Fact]
    public void PlaybackUrlCarriesTimeRange()
    {
        var url = StreamUrls.Playback(SampleDevice(), 1, new DateTime(2026, 10, 20, 8, 0, 0), new DateTime(2026, 10, 20, 9, 0, 0));

        Assert.EndsWith("/Streaming/tracks/101?starttime=20261020T080000Z&endtime=20261020T090000Z", url);
    }

    [Fact]
    public void RedactHidesPassword()
    {
        var device = SampleDevice();

        var redacted = StreamUrls.Redact(StreamUrls.Live(device, 1, StreamQuality.Main), device);

        Assert.DoesNotContain("p%40ss", redacted);
        Assert.Contains("admin:***@", redacted);
    }

    [Fact]
    public void CameraStreamKeyOverridesDeviceKey()
    {
        var device = SampleDevice();

        Assert.Equal("QWERTY", device.StreamKeyFor(device.Cameras[0]));
        Assert.Equal("ABCDEF", device.StreamKeyFor(device.Cameras[1]));
    }

    [Fact]
    public void SettingsRoundTripEncryptsSecretsOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sp2026-{Guid.NewGuid()}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings { Devices = [SampleDevice()] };

            store.Save(settings);
            var json = File.ReadAllText(path);
            var loaded = store.Load();

            Assert.DoesNotContain("p@ss", json);
            Assert.DoesNotContain("ABCDEF", json);
            Assert.DoesNotContain("QWERTY", json);
            Assert.Equal("p@ss:w/rd", settings.Devices[0].Password); // in-memory copy untouched
            var device = loaded.Devices.Single();
            Assert.Equal("p@ss:w/rd", device.Password);
            Assert.Equal("ABCDEF", device.StreamKey);
            Assert.Equal("QWERTY", device.Cameras[0].StreamKey);
            Assert.Null(device.Cameras[1].StreamKey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
