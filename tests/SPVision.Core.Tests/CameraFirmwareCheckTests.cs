using System.Net;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Bakım › Kamera yazılımları › "Denetle": version order, per-camera status, summary line, reads only.</summary>
public class CameraFirmwareCheckTests
{
    static FirmwareVersion V(string text) => FirmwareVersion.Parse(text)!;

    static CameraFirmware Cam(int channel, string? model, string? version, bool? online = true) =>
        new(channel, $"Kamera {channel}", model, version is null ? null : V(version), online, "HIKVISION");

    static readonly DateTimeOffset At = new(2026, 10, 21, 20, 14, 0, TimeSpan.FromHours(3));

    // ================================================================ version order

    [Theory]
    [InlineData("V5.5.800 build 210628", "V5.7.12 build 220819", -1)]
    [InlineData("V5.8.4 build 240613", "V5.7.21 build 231213", 1)]
    [InlineData("V5.7.20 build 230630", "V5.7.20 build 230630", 0)]
    [InlineData("V5.7.2", "V5.7.20", -1)]            // numeric parts, not text: 2 < 20
    [InlineData("V5.7.12", "V5.7.9", 1)]             // 12 > 9 although "1" < "9"
    [InlineData("V10.0.0", "V9.9.9", 1)]
    [InlineData("V5.5.800", "V5.5.80", 1)]
    [InlineData("V5.5", "V5.5.0", 0)]                // missing part = 0
    [InlineData("v5.6.0", "V5.5.9", 1)]
    [InlineData("V5.5.800 build 210628", "V5.5.800 build 220101", -1)] // same number: build date decides
    [InlineData("V5.5.800 build 220101", "V5.5.800 build 210628", 1)]
    [InlineData("V5.5.800", "V5.5.800 build 220101", 0)]               // unknown date: not newer
    public void ComparesVersionsNumericallyThenByBuildDate(string a, string b, int expected)
    {
        Assert.Equal(expected, FirmwareVersionOrder.Compare(V(a), V(b)));
        Assert.Equal(-expected, FirmwareVersionOrder.Compare(V(b), V(a)));
        Assert.Equal(expected > 0, FirmwareVersionOrder.IsNewer(V(a), V(b)));
    }

    [Fact]
    public void UnparseableVersionsAreNeitherOlderNorNewer()
    {
        Assert.Null(FirmwareVersionOrder.Compare(V("bilinmiyor"), V("V5.5.0")));
        Assert.Null(FirmwareVersionOrder.Compare(V("V5.5.0"), null));
        Assert.Null(FirmwareVersionOrder.Compare(null, null));
        Assert.False(FirmwareVersionOrder.IsNewer(V("V9.9.9"), V("x")));
        Assert.False(FirmwareVersionOrder.IsNewer(V("x"), V("V1.0")));
    }

    // ================================================================ per camera

    /// <summary>The real fleet of the DS-8632NI-I8 (models and versions only), 22 cameras.</summary>
    static List<CameraFirmware> Fleet()
    {
        var list = new List<CameraFirmware> { Cam(1, "DS-2CD1043G0-IUF", "V5.5.800 build 210628") };
        var channel = 2;
        for (var i = 0; i < 9; i++)
        {
            list.Add(Cam(channel++, "DS-2CD1323G0-IUF", "V5.5.800 build 210628"));
        }
        for (var i = 0; i < 3; i++)
        {
            list.Add(Cam(channel++, "DS-2CD1323G0-IUF", "V5.7.12 build 220819"));
        }
        for (var i = 0; i < 3; i++)
        {
            list.Add(Cam(channel++, "DS-2CD1323G0-IUF", "V5.7.20 build 230630"));
        }
        list.Add(Cam(channel++, "DS-2CD1343G0-IUF", "V5.7.21 build 231213"));
        for (var i = 0; i < 5; i++)
        {
            list.Add(Cam(channel++, "DS-2CD1343G2-LIUF", "V5.8.4 build 240613"));
        }
        return list;
    }

    [Fact]
    public void FleetWithoutOnlineSourceShowsNewerVersionsOfTheSameModel()
    {
        var cameras = Fleet();
        var results = CameraFirmwareCheck.Evaluate(cameras);
        Assert.Equal(cameras.Select(c => c.Channel), results.Select(r => r.Channel));

        var updates = results.Where(r => r.State == CameraCheckState.NewVersion).ToList();
        Assert.Equal(12, updates.Count);                                   // 9 × V5.5.800 + 3 × V5.7.12 of DS-2CD1323G0-IUF
        Assert.All(updates, r => Assert.Equal("V5.7.20", r.Newer!.Version));
        Assert.All(updates, r => Assert.Equal(NewerSource.OtherCamera, r.Source));
        Assert.All(updates, r => Assert.Equal(14, r.SourceChannel));        // first camera on V5.7.20
        Assert.Equal("Güncelleme var · V5.7.20", updates[0].Badge);
        Assert.Contains("kanal 14", updates[0].Detail, StringComparison.Ordinal);
        Assert.Contains("SPVision dosyayı indirmez", updates[0].Detail, StringComparison.Ordinal);

        var cannotTell = results.Where(r => r.State == CameraCheckState.Unchecked).ToList();
        Assert.Equal(10, cannotTell.Count);                                 // newest of their model: cannot tell
        Assert.Equal("Denetlenemedi", cannotTell[0].Badge);
        Assert.Contains("Hikvision sitesinden kontrol edin", cannotTell[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(results, r => r.State == CameraCheckState.UpToDate); // never claimed without a source

        var check = new CameraCheck(At, results);
        Assert.Equal(CameraCheckOverall.Updates, CameraFirmwareCheck.Overall(check));
        Assert.Equal("Güncelleme var", CameraFirmwareCheck.OverallBadge(CameraCheckOverall.Updates));
        Assert.Equal("Son denetim 20:14 · 12 kamerada güncelleme var · 10 kamerada sürüm denetlenemedi",
            CameraFirmwareCheck.Summary(check, At.AddMinutes(5)));
        Assert.Equal(["DS-2CD1043G0-IUF", "DS-2CD1323G0-IUF", "DS-2CD1343G0-IUF", "DS-2CD1343G2-LIUF"],
            CameraFirmwareCheck.ModelsToLookUp(cameras, results));
    }

    [Fact]
    public void OfflineUnknownAndUnreadableCameras()
    {
        var cameras = new List<CameraFirmware>
        {
            Cam(1, "M", "V5.5.0", online: false),                 // offline: not checked, but its version still counts for the model
            Cam(2, "M", "V5.4.0", online: null),                  // online state unknown: still compared
            Cam(3, null, "V5.4.0"),                               // no model
            Cam(4, "M", null),                                    // no version
            Cam(5, "m", "V5.4.0 build 200101"),                   // model compared ignoring case
            Cam(6, "N", "V1.0 build 200101"),
            Cam(7, "N", "V1.0 build 210101"),                     // same number, newer build
        };
        var results = CameraFirmwareCheck.Evaluate(cameras);
        Assert.Equal(CameraCheckState.Offline, results[0].State);
        Assert.Equal("Çevrimdışı", results[0].Badge);
        Assert.Equal(CameraCheckState.NewVersion, results[1].State);
        Assert.Equal(1, results[1].SourceChannel);
        Assert.Equal(CameraCheckState.Unchecked, results[2].State);
        Assert.Equal(CameraCheckState.Unchecked, results[3].State);
        Assert.Equal(CameraCheckState.NewVersion, results[4].State);
        Assert.Equal(CameraCheckState.NewVersion, results[5].State);
        Assert.Equal("Güncelleme var · V1.0", results[5].Badge);
        Assert.Equal(new DateOnly(2021, 1, 1), results[5].Newer!.BuildDate);
        Assert.Equal(CameraCheckState.Unchecked, results[6].State);
        Assert.Equal("Son denetim 20:14 · 3 kamerada güncelleme var · 3 kamerada sürüm denetlenemedi · 1 kamera çevrimdışı",
            CameraFirmwareCheck.Summary(new CameraCheck(At, results), At));
        Assert.Equal(["M", "N"], CameraFirmwareCheck.ModelsToLookUp(cameras, results));
    }

    [Fact]
    public void OnlineSourceGivesUpToDateAndNewVersion()
    {
        var cameras = new List<CameraFirmware>
        {
            Cam(1, "A", "V5.7.20 build 230630"),
            Cam(2, "B", "V5.5.800 build 210628"),
            Cam(3, "C", "V5.5.0"),
            Cam(4, "C", "V5.9.0"),                                // another camera newer than the online answer
            Cam(5, "A", "V5.7.20 build 230630", online: false),
        };
        var latest = new Dictionary<string, FirmwareVersion>
        {
            ["a"] = V("V5.7.20 build 230630"),
            ["B"] = V("V5.8.0 build 240101"),
            ["C"] = V("V5.6.0"),
        };
        var results = CameraFirmwareCheck.Evaluate(cameras, latest);
        Assert.Equal(CameraCheckState.UpToDate, results[0].State);
        Assert.Equal("Güncel", results[0].Badge);
        Assert.Equal(CameraCheckState.NewVersion, results[1].State);
        Assert.Equal(NewerSource.Online, results[1].Source);
        Assert.Equal("Güncelleme var · V5.8.0", results[1].Badge);
        Assert.Contains("Hikvision sunucusunda yeni sürüm var", results[1].Detail, StringComparison.Ordinal);
        Assert.Equal(NewerSource.OtherCamera, results[2].Source);    // the newest known wins
        Assert.Equal("V5.9.0", results[2].Newer!.Version);
        Assert.Equal(CameraCheckState.UpToDate, results[3].State);
        Assert.Equal(CameraCheckState.Offline, results[4].State);
        Assert.Equal(["C"], CameraFirmwareCheck.ModelsToLookUp(cameras, results));
    }

    [Fact]
    public void AllUpToDateSummaries()
    {
        var latest = new Dictionary<string, FirmwareVersion> { ["A"] = V("V2.0") };
        var all = new CameraCheck(At, CameraFirmwareCheck.Evaluate([Cam(1, "A", "V2.0"), Cam(2, "A", "V2.0")], latest));
        Assert.Equal(CameraCheckOverall.UpToDate, CameraFirmwareCheck.Overall(all));
        Assert.Equal("Güncel", CameraFirmwareCheck.OverallBadge(CameraCheckOverall.UpToDate));
        Assert.Equal("Son denetim 20:14 · Tüm kameralar güncel", CameraFirmwareCheck.Summary(all, At));

        var withOffline = new CameraCheck(At, CameraFirmwareCheck.Evaluate([Cam(1, "A", "V2.0"), Cam(2, "A", "V1.0", online: false)], latest));
        Assert.Equal("Son denetim 20:14 · Tüm çevrimiçi kameralar güncel · 1 kamera çevrimdışı", CameraFirmwareCheck.Summary(withOffline, At));

        var mixed = new CameraCheck(At, CameraFirmwareCheck.Evaluate([Cam(1, "A", "V2.0"), Cam(2, "Z", "V1.0")], latest));
        Assert.Equal(CameraCheckOverall.Unchecked, CameraFirmwareCheck.Overall(mixed)); // one camera unknown: not "all up to date"
        Assert.Equal("Son denetim 20:14 · 1 kamerada sürüm denetlenemedi", CameraFirmwareCheck.Summary(mixed, At));
    }

    [Fact]
    public void SummaryBeforeAndAfterFailedChecks()
    {
        Assert.Equal(CameraFirmwareCheck.NotCheckedText, CameraFirmwareCheck.Summary(null, At));
        Assert.Equal(CameraCheckOverall.NotChecked, CameraFirmwareCheck.Overall(null));
        Assert.Equal("Denetlenmedi", CameraFirmwareCheck.OverallBadge(CameraCheckOverall.NotChecked));
        Assert.Equal("—", new CameraCheckResult(1, CameraCheckState.NotChecked).Badge);

        var failed = new CameraCheck(At, null, "bağlantı zaman aşımı");
        Assert.Equal(CameraCheckOverall.Unchecked, CameraFirmwareCheck.Overall(failed));
        Assert.Equal("Son denetim 20:14 · kamera listesi okunamadı: bağlantı zaman aşımı", CameraFirmwareCheck.Summary(failed, At));
        Assert.Null(failed.For(1));

        Assert.Equal("Son denetim 20:14 · kayıt cihazında IP kamera yok", CameraFirmwareCheck.Summary(new CameraCheck(At, []), At));
        Assert.StartsWith("Son denetim 21.10.2026 20:14 ·", CameraFirmwareCheck.Summary(new CameraCheck(At, []), At.AddDays(1)), StringComparison.Ordinal);
    }

    // ================================================================ reads only

    sealed class ReadFake : IConfigTransport
    {
        public Dictionary<string, Func<XDocument>> Answers { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default)
        {
            Calls.Add(path);
            return Answers.TryGetValue(path, out var answer) ? Task.FromResult(answer()) : throw new IsapiException("yok", HttpStatusCode.NotFound);
        }

        public Task<IsapiResponseStatus> PutConfigAsync(string path, XDocument body, CancellationToken ct = default) =>
            throw new InvalidOperationException("Denetle never writes.");
    }

    const string Channels = """
        <InputProxyChannelList xmlns="http://www.isapi.org/ver20/XMLSchema">
          <InputProxyChannel><id>1</id><name>Giris</name><sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol>
            <model>DS-2CD1323G0-IUF</model><firmwareVersion>V5.5.800 build 210628</firmwareVersion></sourceInputPortDescriptor></InputProxyChannel>
          <InputProxyChannel><id>2</id><name>Bahce</name><sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol>
            <model>DS-2CD1323G0-IUF</model><firmwareVersion>V5.7.20 build 230630</firmwareVersion></sourceInputPortDescriptor></InputProxyChannel>
        </InputProxyChannelList>
        """;

    [Fact]
    public async Task ReaderGetsOnlyTheCameraTable()
    {
        var fake = new ReadFake();
        fake.Answers[MaintenancePaths.Channels] = () => XDocument.Parse(Channels);
        fake.Answers[MaintenancePaths.ChannelStatus] = () => XDocument.Parse(
            "<InputProxyChannelStatusList><InputProxyChannelStatus><id>1</id><online>true</online></InputProxyChannelStatus>" +
            "<InputProxyChannelStatus><id>2</id><online>false</online></InputProxyChannelStatus></InputProxyChannelStatusList>");
        var read = await CameraFirmwareReader.ReadAsync(fake);
        Assert.True(read.Ok);
        Assert.Equal([MaintenancePaths.Channels, MaintenancePaths.ChannelStatus], fake.Calls);
        Assert.All(fake.Calls, c => Assert.DoesNotContain("upgrade", c, StringComparison.OrdinalIgnoreCase));
        Assert.Equal([true, false], read.Value!.Select(c => c.Online));

        var results = CameraFirmwareCheck.Evaluate(read.Value!);
        Assert.Equal(CameraCheckState.NewVersion, results[0].State);   // channel 2 (offline) runs V5.7.20
        Assert.Equal(CameraCheckState.Offline, results[1].State);
    }

    [Fact]
    public async Task ReaderWithoutStatusOrChannels()
    {
        var fake = new ReadFake();
        fake.Answers[MaintenancePaths.Channels] = () => XDocument.Parse(Channels);
        var read = await CameraFirmwareReader.ReadAsync(fake);       // /status 404: online unknown
        Assert.True(read.Ok);
        Assert.All(read.Value!, c => Assert.Null(c.Online));

        var none = await CameraFirmwareReader.ReadAsync(new ReadFake());
        Assert.False(none.Ok);
        Assert.Null(none.Value);
    }
}
