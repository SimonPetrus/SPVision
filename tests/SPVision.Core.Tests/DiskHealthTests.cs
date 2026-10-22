using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Status bar disk warning: which statuses are problems, and that only successful reads change the warning.</summary>
public class DiskHealthTests
{
    static readonly Guid Nvr = Guid.NewGuid();
    static readonly Guid Dvr = Guid.NewGuid();

    static HddInfo Disk(string status, int id = 1, string name = "hdd1") => new(id, name, "SATA", status, 7630885, 0);

    static Read<List<HddInfo>> Ok(params HddInfo[] disks) => Read<List<HddInfo>>.Success([.. disks]);

    static Read<List<HddInfo>> Failed => new(ReadState.Failed, null, "Cihaza bağlanılamadı");

    [Theory]
    [InlineData("ok")]
    [InlineData("OK")]
    [InlineData("normal")]
    [InlineData("idle")]
    [InlineData("sleeping")]
    [InlineData("standby")]
    [InlineData(" ok ")]
    public void HealthyStatuses(string status) => Assert.True(HddInfo.IsHealthy(status));

    [Theory]
    [InlineData("error", "Hata")]
    [InlineData("unformatted", "Biçimlenmemiş")]
    [InlineData("smartFailed", "SMART hatası")]
    [InlineData("offline", "Çevrimdışı")]
    [InlineData("notexist", "Takılı değil")]
    [InlineData("mismatch", "Uyumsuz")]
    [InlineData("abnormal", "Arızalı")]
    [InlineData("formatting", "Biçimlendiriliyor")]
    [InlineData("reparing", "Onarılıyor")]
    [InlineData("unRecordHostFormatted", "Başka cihazda biçimlendirilmiş")]
    [InlineData("", "—")]
    [InlineData("weird", "weird")]
    public void EverythingElseIsAProblemWithATurkishText(string status, string text)
    {
        Assert.False(HddInfo.IsHealthy(status));
        Assert.Equal(text, HddInfo.StatusTextOf(status));
    }

    [Fact]
    public void NormalStatusReadsNormal() => Assert.Equal("Normal", HddInfo.StatusTextOf("normal"));

    [Fact]
    public void HealthyRecorderShowsNoWarning()
    {
        var watch = new DiskWatch();

        Assert.False(watch.Update(Nvr, "NVR", Ok(Disk("ok"), Disk("idle", 2, "hdd2"))));
        Assert.False(watch.HasProblem);
        Assert.Equal("", watch.ToolTip);
        Assert.Null(watch.FirstProblemDevice);
        Assert.True(watch.Knows(Nvr));
    }

    [Fact]
    public void BadDiskWarnsWithDeviceDiskAndStatus()
    {
        var watch = new DiskWatch();

        Assert.True(watch.Update(Nvr, "Mağaza NVR", Ok(Disk("ok"), Disk("smartFailed", 2, "hdd2"))));

        Assert.True(watch.HasProblem);
        var problem = Assert.Single(watch.Problems);
        Assert.Equal("Mağaza NVR · hdd2: SMART hatası", problem.Text);
        Assert.Equal(Nvr, watch.FirstProblemDevice);
        Assert.Contains("Mağaza NVR · hdd2: SMART hatası", watch.ToolTip);
        Assert.Equal("Disk sorunu — kayıt yapılmıyor olabilir", DiskWatch.WarningText);
    }

    [Fact]
    public void RecorderWithoutDisksWarns()
    {
        var watch = new DiskWatch();

        Assert.True(watch.Update(Nvr, "NVR", Ok()));

        Assert.Equal("NVR: Disk yok", Assert.Single(watch.Problems).Text);
    }

    [Fact]
    public void UnnamedDiskUsesItsNumber() =>
        Assert.Equal("NVR · Disk 3: Hata", Assert.Single(DiskWatch.ProblemsOf(Nvr, "NVR", [Disk("error", 3, "")])).Text);

    [Fact]
    public void NetworkErrorNeitherRaisesNorClearsTheWarning()
    {
        var watch = new DiskWatch();
        Assert.False(watch.Update(Nvr, "NVR", Failed));
        Assert.False(watch.HasProblem);
        Assert.False(watch.Knows(Nvr));

        watch.Update(Nvr, "NVR", Ok(Disk("error")));
        Assert.False(watch.Update(Nvr, "NVR", Failed));
        Assert.False(watch.Update(Nvr, "NVR", new Read<List<HddInfo>>(ReadState.Unsupported, null)));
        Assert.True(watch.HasProblem); // last known state kept
    }

    [Fact]
    public void WarningHidesWhenHealthyAgain()
    {
        var watch = new DiskWatch();
        watch.Update(Nvr, "NVR", Ok(Disk("unformatted")));

        Assert.True(watch.Update(Nvr, "NVR", Ok(Disk("ok"))));

        Assert.False(watch.HasProblem);
    }

    [Fact]
    public void SameProblemAgainIsNoChange()
    {
        var watch = new DiskWatch();
        watch.Update(Nvr, "NVR", Ok(Disk("error")));

        Assert.False(watch.Update(Nvr, "NVR", Ok(Disk("error"))));
        Assert.True(watch.Update(Nvr, "NVR", Ok(Disk("offline"))));
    }

    [Fact]
    public void SeveralRecordersListedInReadOrder()
    {
        var watch = new DiskWatch();
        watch.Update(Dvr, "DVR", Ok(Disk("error")));
        watch.Update(Nvr, "NVR", Ok());

        Assert.Equal(["DVR · hdd1: Hata", "NVR: Disk yok"], watch.Problems.Select(p => p.Text).ToList());
        Assert.Equal(Dvr, watch.FirstProblemDevice);

        Assert.True(watch.Forget(Dvr));
        Assert.Equal(Nvr, watch.FirstProblemDevice);
        Assert.False(watch.Forget(Dvr));
        Assert.True(watch.Forget(Nvr));
        Assert.False(watch.HasProblem);
    }
}
