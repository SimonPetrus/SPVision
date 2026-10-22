using System.IO.Compression;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>"Sorunu bildir…" zip: which files go in, scrubbing, sistem.txt without addresses or logins.</summary>
public class ProblemReportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spvision-report-tests-" + Guid.NewGuid().ToString("N"));

    public ProblemReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DefaultFileNameHasDateAndTime() =>
        Assert.Equal("SPVision-sorun-20261022-0905.zip", ProblemReport.DefaultFileName(new DateTime(2026, 10, 22, 9, 5, 59)));

    [Theory]
    [InlineData("crash.log", true)]
    [InlineData("crash.log.1", true)]
    [InlineData("CRASH.LOG", true)]
    [InlineData("shutdown.log", true)]
    [InlineData("player.log", true)]
    [InlineData("player.log.2", true)]
    [InlineData("player.1.log", true)]
    [InlineData("settings.json", false)]
    [InlineData("SdkLog_1_W.log", false)]
    [InlineData("kurulum.log", false)]
    [InlineData("crash.log.bak", false)]
    [InlineData("crash.log.1.json", false)]
    [InlineData("player.x.log", false)]
    [InlineData("shutdown.log.1", false)]
    [InlineData("crashlog", false)]
    public void OnlyTheAppsOwnLogsAreIncluded(string name, bool included) => Assert.Equal(included, ProblemReport.IsLogIncluded(name));

    static Device SampleDevice() => new()
    {
        Name = "Mağaza NVR",
        Host = "192.168.1.2",
        UserName = "yonetici",
        Password = "Gizli-Sifre-123",
        StreamKey = "AKISANAHTARI",
        Model = "DS-8632NI-I8",
        SerialNumber = "DS-8632NI-I81620200101CCRR",
        Cameras = [new Camera { Channel = 1, Name = "Giriş" }, new Camera { Channel = 2, Name = "Kasa" }],
    };

    [Fact]
    public void SystemTextHasOnlyModelFirmwareAndCameraCount()
    {
        var device = SampleDevice();
        var details = new DeviceDetails("Mağaza NVR", "DS-8632NI-I8", "DS-8632NI-I81620200101CCRR", "V4.61.025", "NVR");
        var system = new ReportSystem("1.0.0", "Microsoft Windows 10.0.26300 (64 bit)", ".NET 10.0.0", ["NVIDIA GeForce RTX 4070"],
            HardwareDecoding: true, [ReportDevice.From(device, details), ReportDevice.From(new Device { Host = "10.0.0.9" }, null)]);

        var text = ProblemReport.SystemText(system, new DateTimeOffset(2026, 10, 22, 9, 5, 0, TimeSpan.FromHours(3)));

        Assert.Contains("Uygulama sürümü: 1.0.0", text);
        Assert.Contains("İşletim sistemi: Microsoft Windows 10.0.26300 (64 bit)", text);
        Assert.Contains(".NET: .NET 10.0.0", text);
        Assert.Contains("Ekran kartı: NVIDIA GeForce RTX 4070", text);
        Assert.Contains("Donanım hızlandırma: Açık", text);
        Assert.Contains("Kayıt cihazları: 2", text);
        Assert.Contains("1. Model: DS-8632NI-I8 · Yazılım: V4.61.025 · Kamera: 2", text);
        Assert.Contains("2. Model: bilinmiyor · Yazılım: okunamadı · Kamera: 0", text);
        foreach (var secret in new[] { "192.168.1.2", "10.0.0.9", "yonetici", "Gizli-Sifre-123", "AKISANAHTARI", "CCRR", "Mağaza", "Giriş" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public void ModelFallsBackToTheSavedOneWhenTheRecorderDoesNotAnswer()
    {
        Assert.Equal(new ReportDevice("DS-8632NI-I8", null, 2), ReportDevice.From(SampleDevice(), null));
        Assert.Equal("Ekran kartı: bilinmiyor", ProblemReport.SystemText(new ReportSystem("1", "W", "N", [], false, []), DateTimeOffset.Now)
            .Split(Environment.NewLine).Single(l => l.StartsWith("Ekran kartı")));
    }

    [Fact]
    public void ZipHoldsScrubbedLogsAndSystemTextAndNothingElse()
    {
        File.WriteAllText(Path.Combine(_dir, "crash.log"), "Hata: rtsp://admin:Gizli1@192.168.1.2:554/x password=Gizli2\n");
        File.WriteAllText(Path.Combine(_dir, "crash.log.1"), "eski\n");
        File.WriteAllText(Path.Combine(_dir, "shutdown.log"), "Kapanış başladı\n");
        File.WriteAllText(Path.Combine(_dir, "player.log"), "open rtsp://u:Gizli3@h/ ok\n");
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Password\":\"dpapi:Gizli4\"}");
        File.WriteAllText(Path.Combine(_dir, "pins.json"), "SpkiSha256");
        Directory.CreateDirectory(Path.Combine(_dir, "sdk"));
        File.WriteAllText(Path.Combine(_dir, "sdk", "SdkLog_1_W.log"), "sdk");
        File.WriteAllText(Path.Combine(_dir, "sdk", "crash.log"), "nested");
        var zipPath = Path.Combine(_dir, "rapor.zip");

        var names = ProblemReport.Write(zipPath, _dir, "SPVision sorun raporu\npwd=Gizli5\n");

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["crash.log", "crash.log.1", "player.log", "shutdown.log", "sistem.txt"], entries);
        Assert.Equal(entries, names.Order(StringComparer.Ordinal).ToList());
        var all = string.Concat(zip.Entries.Select(e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        }));
        Assert.DoesNotContain("Gizli", all);
        Assert.DoesNotContain("SpkiSha256", all);
        Assert.DoesNotContain("nested", all);
        Assert.Contains("rtsp://***@192.168.1.2:554/x password=***", all);
        Assert.Contains("Kapanış başladı", all);
    }

    [Fact]
    public void ZipWithoutLogsHasSystemTextOnly()
    {
        var zipPath = Path.Combine(_dir, "bos.zip");

        ProblemReport.Write(zipPath, Path.Combine(_dir, "yok"), "x");

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal("sistem.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void LogOpenForWritingIsStillRead()
    {
        var path = Path.Combine(_dir, "player.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("açık dosya\n"u8);
        writer.Flush();

        ProblemReport.Write(Path.Combine(_dir, "r.zip"), _dir, "x");

        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "r.zip"));
        using var reader = new StreamReader(zip.GetEntry("player.log")!.Open());
        Assert.Equal("açık dosya\n", reader.ReadToEnd());
    }

    [Fact]
    public void BigLogKeepsItsEnd()
    {
        var path = Path.Combine(_dir, "shutdown.log");
        File.WriteAllText(path, new string('a', ProblemReport.MaxLogBytes) + "SON SATIR\n");

        ProblemReport.Write(Path.Combine(_dir, "r.zip"), _dir, "x");

        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "r.zip"));
        using var reader = new StreamReader(zip.GetEntry("shutdown.log")!.Open());
        var text = reader.ReadToEnd();
        Assert.StartsWith("(… dosyanın başı kesildi; son kısmı)", text);
        Assert.EndsWith("SON SATIR\n", text);
        Assert.True(text.Length < ProblemReport.MaxLogBytes + 100);
    }
}
