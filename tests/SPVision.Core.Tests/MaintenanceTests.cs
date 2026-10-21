using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Phase 3 (Bakım): the exact maintenance entries of the write policy (factory reset mode=basic|full, recorder and camera
/// firmware through the SDK), everything around them that stays denied, typed words, the single-use token, the firmware
/// file binding, the parsers of what the tab reads and the upgrade orchestration. No network, no SDK: fakes only.
/// </summary>
public class MaintenanceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spvision-maintenance-tests-" + Guid.NewGuid().ToString("N"));

    public MaintenanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        GC.SuppressFinalize(this);
    }

    // ================================================================ policy: exact entries

    public static TheoryData<RiskyOperation, string, string> AllowedMaintenance() => new()
    {
        { RiskyOperation.FactoryReset, "PUT", "ISAPI/System/factoryReset?mode=basic" },
        { RiskyOperation.FactoryReset, "PUT", "ISAPI/System/factoryReset?mode=full" },
        { RiskyOperation.RecorderFirmware, "SDK", "NET_DVR_Upgrade_V50/ENUM_UPGRADE_DVR" },
        { RiskyOperation.CameraFirmware, "SDK", "NET_DVR_StartRemoteConfig/NET_DVR_UPGRADE_IPC" },
    };

    [Theory]
    [MemberData(nameof(AllowedMaintenance))]
    public void AllowsEachMaintenanceEntryForItsOperationAndMethodOnly(RiskyOperation operation, string method, string target)
    {
        Assert.Null(IsapiWritePolicy.CheckRisky(operation, method, target));
        foreach (var other in Enum.GetValues<RiskyOperation>().Where(o => o != operation))
        {
            Assert.NotNull(IsapiWritePolicy.CheckRisky(other, method, target));
        }
        foreach (var otherMethod in new[] { "GET", "PUT", "POST", "DELETE", "PATCH", "SDK", "put", "Put", "sdk", "Sdk", "" }.Where(m => m != method))
        {
            Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, otherMethod, target));
        }
        Assert.NotNull(IsapiWritePolicy.Check(target)); // never an ordinary write
    }

    [Fact]
    public void BuildersUseExactlyThePolicyEntries()
    {
        Assert.Equal("ISAPI/System/factoryReset?mode=basic", MaintenancePaths.FactoryReset(FactoryResetMode.Basic));
        Assert.Equal("ISAPI/System/factoryReset?mode=full", MaintenancePaths.FactoryReset(FactoryResetMode.Full));
        Assert.Equal(4, MaintenancePolicy.Entries.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => MaintenancePaths.FactoryReset((FactoryResetMode)7));
    }

    [Theory]
    [InlineData("ISAPI/System/factoryReset")]                         // default mode is "full": never without the parameter
    [InlineData("ISAPI/System/factoryReset?mode=part")]
    [InlineData("ISAPI/System/factoryReset?mode=other")]
    [InlineData("ISAPI/System/factoryReset?mode=")]
    [InlineData("ISAPI/System/factoryReset?mode=Basic")]
    [InlineData("ISAPI/System/factoryReset?mode=BASIC")]
    [InlineData("ISAPI/System/factoryReset?mode=FULL")]
    [InlineData("ISAPI/System/factoryReset?Mode=basic")]
    [InlineData("ISAPI/System/factoryReset?mode=basic&mode=full")]
    [InlineData("ISAPI/System/factoryReset?mode=basic&x=1")]
    [InlineData("ISAPI/System/factoryReset?x=1&mode=basic")]
    [InlineData("ISAPI/System/factoryReset?mode=basic&")]
    [InlineData("ISAPI/System/factoryReset?mode=basic#x")]
    [InlineData("ISAPI/System/factoryReset?mode=basic/")]
    [InlineData("ISAPI/System/factoryReset?mode=%62asic")]
    [InlineData("ISAPI/System/factoryReset?mode=basic%26x=1")]
    [InlineData("ISAPI/System/factoryReset/?mode=basic")]
    [InlineData("ISAPI/System%2FfactoryReset?mode=basic")]
    [InlineData("ISAPI/System/factoryreset?mode=basic")]
    [InlineData("ISAPI/system/factoryReset?mode=basic")]
    [InlineData("ISAPI/System/FactoryReset?mode=full")]
    [InlineData("/ISAPI/System/factoryReset?mode=basic")]
    [InlineData("ISAPI//System/factoryReset?mode=basic")]
    [InlineData("ISAPI/System/../System/factoryReset?mode=basic")]
    [InlineData(" ISAPI/System/factoryReset?mode=basic")]
    [InlineData("ISAPI/System/factoryReset?mode=full ")]
    [InlineData("ISAPI\\System\\factoryReset?mode=full")]
    [InlineData("http://192.0.2.1/ISAPI/System/factoryReset?mode=full")]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1/factoryReset?mode=basic")]   // camera reset: not offered
    [InlineData("ISAPI/System/factoryReset?mode=basic\n")]
    [InlineData("")]
    public void DeniesFactoryResetVariants(string path)
    {
        foreach (var operation in Enum.GetValues<RiskyOperation>())
        {
            foreach (var method in new[] { "GET", "PUT", "POST", "DELETE", "SDK" })
            {
                Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, path));
            }
        }
        Assert.NotNull(IsapiWritePolicy.Check(path));
    }

    [Theory]
    [InlineData(RiskyOperation.FactoryReset, "GET", "ISAPI/System/factoryReset?mode=basic")]
    [InlineData(RiskyOperation.FactoryReset, "POST", "ISAPI/System/factoryReset?mode=full")]
    [InlineData(RiskyOperation.FactoryReset, "DELETE", "ISAPI/System/factoryReset?mode=full")]
    [InlineData(RiskyOperation.FactoryReset, "put", "ISAPI/System/factoryReset?mode=full")]
    [InlineData(RiskyOperation.FactoryReset, null, "ISAPI/System/factoryReset?mode=full")]
    [InlineData(RiskyOperation.FactoryReset, "PUT", null)]
    [InlineData(RiskyOperation.RecorderFirmware, "PUT", "NET_DVR_Upgrade_V50/ENUM_UPGRADE_DVR")]
    [InlineData(RiskyOperation.RecorderFirmware, "SDK", "NET_DVR_Upgrade_V50/ENUM_UPGRADE_DVR ")]
    [InlineData(RiskyOperation.RecorderFirmware, "SDK", "net_dvr_upgrade_v50/enum_upgrade_dvr")]
    [InlineData(RiskyOperation.RecorderFirmware, "SDK", "NET_DVR_Upgrade_V50/ENUM_UPGRADE_CUSTOM")]
    [InlineData(RiskyOperation.RecorderFirmware, "SDK", "NET_DVR_Upgrade/ENUM_UPGRADE_DVR")]
    [InlineData(RiskyOperation.RecorderFirmware, "SDK", "ISAPI/System/updateFirmware")]
    [InlineData(RiskyOperation.RecorderFirmware, "PUT", "ISAPI/System/updateFirmware")]
    [InlineData(RiskyOperation.RecorderFirmware, "POST", "ISAPI/System/updateFirmware")]
    [InlineData(RiskyOperation.RecorderFirmware, "PUT", "ISAPI/System/onlineUpgrade/downloadPackage?format=json")]
    [InlineData(RiskyOperation.RecorderFirmware, "PUT", "ISAPI/System/onlineUpgrade/upgradeWithoutDownload?format=json")]
    [InlineData(RiskyOperation.RecorderFirmware, "PUT", "ISAPI/System/onlineUpgrade/deviceParameter?format=json")]
    [InlineData(RiskyOperation.CameraFirmware, "SDK", "NET_DVR_StartRemoteConfig/NET_DVR_UPGRADE_IPC/33")]
    [InlineData(RiskyOperation.CameraFirmware, "SDK", "NET_DVR_StartRemoteConfig/NET_DVR_IMPORT_IPC_CFG_FILE")]
    [InlineData(RiskyOperation.CameraFirmware, "PUT", "ISAPI/ContentMgmt/InputProxy/channels/1/firmware")]
    [InlineData(RiskyOperation.CameraFirmware, "PUT", "ISAPI/ContentMgmt/InputProxy/channels/1/upgrade")]
    public void DeniesWrongMethodsTargetsAndOtherUpgradeEndpoints(RiskyOperation operation, string? method, string? target)
    {
        Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, target));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyAllowed(operation, method, target));
    }

    /// <summary>Phase 3 opened exact entries only: the security vocabulary stays closed for the new operations too.</summary>
    [Theory]
    [InlineData("ISAPI/System/Network/EZVIZ")]
    [InlineData("ISAPI/Streaming/encryption")]
    [InlineData("ISAPI/System/HikConnect")]
    [InlineData("ISAPI/System/Hik-Connect")]
    [InlineData("ISAPI/System/Network/cloud")]
    [InlineData("ISAPI/System/platform/1")]
    [InlineData("ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/Security/serverCertificate/certificate")]
    [InlineData("ISAPI/Security/TLS")]
    [InlineData("ISAPI/Security/SRTP")]
    [InlineData("ISAPI/System/activate")]
    [InlineData("ISAPI/System/configurationData")]
    [InlineData("ISAPI/System/shutdown")]
    [InlineData("ISAPI/System/reboot")]
    [InlineData("ISAPI/System/upgradeStatus")]
    public void MaintenanceOperationsNeverReachOtherEndpoints(string path)
    {
        foreach (var operation in new[] { RiskyOperation.FactoryReset, RiskyOperation.RecorderFirmware, RiskyOperation.CameraFirmware })
        {
            foreach (var method in new[] { "GET", "PUT", "POST", "DELETE", "SDK" })
            {
                Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, path));
            }
        }
    }

    [Fact]
    public void OldOperationsCannotReachTheMaintenanceTargets()
    {
        foreach (var (_, method, target) in MaintenancePolicy.Entries)
        {
            foreach (var operation in Enum.GetValues<RiskyOperation>().Where(o => !MaintenancePolicy.Handles(o)))
            {
                Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, target));
            }
        }
    }

    [Theory]
    [InlineData(RiskyOperation.FactoryReset)]
    [InlineData(RiskyOperation.RecorderFirmware)]
    [InlineData(RiskyOperation.CameraFirmware)]
    public void MaintenanceOperationsCarryNoXmlBody(RiskyOperation operation)
    {
        IsapiWritePolicy.EnsureRiskyBodyAllowed(operation, null);
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(operation, XDocument.Parse("<x/>")));
    }

    // ================================================================ typed words

    [Theory]
    [InlineData("GÜNCELLE", true)]
    [InlineData("güncelle", true)]
    [InlineData("  Güncelle ", true)]
    [InlineData("GUNCELLE", false)]
    [InlineData("guncelle", false)]
    [InlineData("GÜNCEL", false)]
    [InlineData("GÜNCELLE.", false)]
    [InlineData("EVET", false)]
    [InlineData("", false)]
    public void UpdateWordUsesTurkishCasing(string typed, bool ok) =>
        Assert.Equal(ok, RiskyWrites.WordMatches(MaintenancePolicy.UpdateWord, typed));

    [Theory]
    [InlineData("SIFIRLA", true)]
    [InlineData("sıfırla", true)]
    [InlineData(" Sıfırla ", true)]
    [InlineData("sifirla", false)]   // Turkish upper-casing: i → İ, so "SİFİRLA"
    [InlineData("SİFİRLA", false)]
    [InlineData("SIFIRLA!", false)]
    [InlineData("BİÇİMLENDİR", false)]
    [InlineData("", false)]
    public void ResetWordUsesTurkishCasing(string typed, bool ok)
    {
        Assert.Equal(ok, RiskyWrites.WordMatches(MaintenancePolicy.ResetWord, typed));
        var token = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.FactoryReset("NVR", FactoryResetMode.Basic), new Answer(typed));
        Assert.Equal(ok, token is not null);
    }

    // ================================================================ factory reset

    [Theory]
    [InlineData(FactoryResetMode.Basic, "/ISAPI/System/factoryReset?mode=basic")]
    [InlineData(FactoryResetMode.Full, "/ISAPI/System/factoryReset?mode=full")]
    public async Task FactoryResetSendsExactlyTheConfirmedRequestOnce(FactoryResetMode mode, string expected)
    {
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var token = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.FactoryReset("NVR", mode), new Answer("SIFIRLA"))!;

        var status = await client.SendRiskyAsync(token);

        Assert.True(status.IsOk);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("PUT", sent.Method);
        Assert.Equal(expected, sent.PathAndQuery);
        Assert.Null(sent.Body);
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExpiredResetTokenSendsNothing()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var token = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.FactoryReset("NVR", FactoryResetMode.Full), new Answer("SIFIRLA"), () => now)!;
        now += ConfirmedRiskyWrite.Lifetime + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ResetRequestIsSevereAndSaysWhatItKeepsAndResets()
    {
        var basic = MaintenanceWrites.FactoryReset("Kayıt", FactoryResetMode.Basic);
        var full = MaintenanceWrites.FactoryReset("Kayıt", FactoryResetMode.Full);
        foreach (var request in new[] { basic, full })
        {
            Assert.Equal(RiskyOperation.FactoryReset, request.Operation);
            Assert.Equal("PUT", request.Method);
            Assert.Equal(MaintenancePolicy.ResetWord, request.RequiredWord);
            Assert.True(request.Severe);
            Assert.False(request.HasBody);
            Assert.Contains("akış şifrelemeyi ve Hik-Connect'i", request.Warning, StringComparison.Ordinal);
            Assert.Contains("SPVision bağlantıyı kaybeder", request.Warning, StringComparison.Ordinal);
            Assert.Contains("etkinleştirme", request.Warning, StringComparison.Ordinal);
        }
        Assert.Contains(basic.Changes, l => l.StartsWith("Korunur: ağ ayarları", StringComparison.Ordinal));
        Assert.Contains(full.Changes, l => l.StartsWith("Korunur: hiçbir ayar", StringComparison.Ordinal));
        Assert.Contains("mode=basic", FactoryResetText.Help(FactoryResetMode.Basic), StringComparison.Ordinal);
        Assert.Contains("mode=full", FactoryResetText.Help(FactoryResetMode.Full), StringComparison.Ordinal);
    }

    // ================================================================ firmware file + token

    string WriteFile(string name, int bytes, byte fill = 0x5A)
    {
        var path = Path.Combine(_dir, name);
        var data = new byte[bytes];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData("digicap.dav", 2 << 20, true)]
    [InlineData("DIGICAP.DAV", 2 << 20, true)]
    [InlineData("nvr.digicap", 2 << 20, true)]
    [InlineData("digicap.dav.exe", 2 << 20, false)]
    [InlineData("firmware.zip", 2 << 20, false)]
    [InlineData("digicap", 2 << 20, false)]
    [InlineData("digicap.dav", 1000, false)]
    [InlineData("digicap.dav", 0, false)]
    public void FileNameAndSizeAreChecked(string name, long length, bool ok) =>
        Assert.Equal(ok, FirmwareFile.Validate(name, length) is null);

    [Fact]
    public void TooLargeFileIsRefused() =>
        Assert.NotNull(FirmwareFile.Validate("digicap.dav", FirmwareFile.MaxBytes + 1));

    [Fact]
    public async Task OpenHashesTheWholeFile()
    {
        var path = WriteFile("digicap.dav", (1 << 20) + 17);
        var file = await FirmwareFile.OpenAsync(path);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), file.Sha256);
        Assert.Equal((1 << 20) + 17, file.Length);
        Assert.Equal("digicap.dav", file.Name);
        Assert.Equal("1,0 MB", file.SizeText);
    }

    [Fact]
    public async Task OpenRefusesMissingSmallOrWrongFiles()
    {
        await Assert.ThrowsAsync<FirmwareFileException>(() => FirmwareFile.OpenAsync(Path.Combine(_dir, "yok.dav")));
        await Assert.ThrowsAsync<FirmwareFileException>(() => FirmwareFile.OpenAsync(WriteFile("small.dav", 10)));
        await Assert.ThrowsAsync<FirmwareFileException>(() => FirmwareFile.OpenAsync(WriteFile("big.zip", 2 << 20)));
    }

    async Task<(FirmwareFile File, string Path)> Firmware(string name = "digicap.dav")
    {
        var path = WriteFile(name, 2 << 20);
        return (await FirmwareFile.OpenAsync(path), path);
    }

    static RecorderFirmware Nvr => new("DS-8632NI-I8", FirmwareVersion.Parse("V4.61.025", "build 220905"), "NVR");

    [Fact]
    public async Task RecorderFirmwareTokenGivesOneJobForTheConfirmedFile()
    {
        var (file, _) = await Firmware();
        var request = MaintenanceWrites.RecorderFirmware("NVR", Nvr, file);
        Assert.Equal(MaintenancePolicy.UpdateWord, request.RequiredWord);
        Assert.Contains(request.Changes, l => l.Contains(file.Sha256, StringComparison.Ordinal));
        Assert.Contains("elektriğini KESMEYİN", request.Warning, StringComparison.Ordinal);
        Assert.False(request.Severe);

        Assert.Null(ConfirmedRiskyWrite.Confirm(request, new Answer("guncelle")));
        var token = ConfirmedRiskyWrite.Confirm(request, new Answer("güncelle"))!;
        var job = await token.TakeFirmwareJobAsync();

        Assert.Equal(RiskyOperation.RecorderFirmware, job.Operation);
        Assert.Same(file, job.File);
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => token.TakeFirmwareJobAsync()); // single use
    }

    [Fact]
    public async Task ExpiredFirmwareTokenGivesNoJob()
    {
        var (file, _) = await Firmware();
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var token = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.RecorderFirmware("NVR", Nvr, file), new Answer("GÜNCELLE"), () => now)!;
        now += ConfirmedRiskyWrite.Lifetime + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => token.TakeFirmwareJobAsync());
    }

    [Fact]
    public async Task FileChangedAfterConfirmationGivesNoJob()
    {
        var (file, path) = await Firmware();
        var token = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.RecorderFirmware("NVR", Nvr, file), new Answer("GÜNCELLE"))!;
        var data = File.ReadAllBytes(path);
        data[1234] ^= 0xFF;
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, file.LastWriteUtc); // even with the old time stamp the hash differs

        await Assert.ThrowsAsync<FirmwareFileException>(() => token.TakeFirmwareJobAsync());
    }

    [Fact]
    public async Task FirmwareTokenIsNeverSentOverIsapiAndResetTokenGivesNoJob()
    {
        var (file, _) = await Firmware();
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var firmware = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.RecorderFirmware("NVR", Nvr, file), new Answer("GÜNCELLE"))!;
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(firmware));
        Assert.Empty(handler.Requests);

        var reset = ConfirmedRiskyWrite.Confirm(MaintenanceWrites.FactoryReset("NVR", FactoryResetMode.Basic), new Answer("SIFIRLA"))!;
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => reset.TakeFirmwareJobAsync());
        var reboot = ConfirmedRiskyWrite.Confirm(RiskyWrites.Reboot("NVR"), new Answer(""))!;
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => reboot.TakeFirmwareJobAsync());
    }

    static CameraFirmware Cam(int channel, string model = "DS-2CD1323G0-IUF", bool? online = true, string? protocol = "HIKVISION", string version = "V5.5.800 build 210628") =>
        new(channel, $"Kamera {channel}", model, FirmwareVersion.Parse(version), online, protocol);

    [Fact]
    public async Task CameraRequestListsTheCamerasInOrderAndWarnsAboutMixedModels()
    {
        var (file, _) = await Firmware();
        var same = MaintenanceWrites.CameraFirmware([Cam(4), Cam(2)], file, stopOnFirstFailure: true);
        Assert.Equal([4, 2], same.Channels);
        Assert.DoesNotContain("farklı modellerde", same.Warning, StringComparison.Ordinal);
        Assert.Contains(same.Changes, l => l.StartsWith("İlk hatada durulur", StringComparison.Ordinal));
        Assert.Equal(MaintenancePolicy.CameraFirmwareCall, same.Path);

        var mixed = MaintenanceWrites.CameraFirmware([Cam(1, "DS-2CD1043G0-IUF"), Cam(2), Cam(3)], file, stopOnFirstFailure: false);
        Assert.Contains("farklı modellerde (DS-2CD1043G0-IUF, DS-2CD1323G0-IUF)", mixed.Warning, StringComparison.Ordinal);
        Assert.Contains(mixed.Changes, l => l.StartsWith("Hata olsa da", StringComparison.Ordinal));

        var job = await ConfirmedRiskyWrite.Confirm(mixed, new Answer("GÜNCELLE"))!.TakeFirmwareJobAsync();
        Assert.Equal([1, 2, 3], job.Channels);
    }

    [Fact]
    public async Task CameraRequestRefusesEmptyOfflineForeignOrDuplicateLists()
    {
        var (file, _) = await Firmware();
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([Cam(1, online: false)], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([Cam(1, online: null)], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([Cam(1, protocol: "ONVIF")], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([Cam(1), Cam(1)], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() => MaintenanceWrites.CameraFirmware([Cam(0)], file, true));
        Assert.Throws<IsapiWriteDeniedException>(() =>
            MaintenanceWrites.CameraFirmware(Enumerable.Range(1, MaintenancePolicy.MaxCameras + 1).Select(c => Cam(c)).ToList(), file, true));
    }

    // ================================================================ parsers

    const string DeviceInfoXml = """
        <DeviceInfo version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
          <deviceName>Kayit</deviceName><deviceID>x</deviceID><model>DS-8632NI-I8</model><serialNumber>S</serialNumber>
          <macAddress>m</macAddress><firmwareVersion>V4.61.025</firmwareVersion><firmwareReleasedDate>build 220905</firmwareReleasedDate>
          <deviceType>NVR</deviceType>
        </DeviceInfo>
        """;

    [Fact]
    public void ParsesRecorderFirmware()
    {
        var nvr = RecorderFirmware.Parse(XDocument.Parse(DeviceInfoXml));
        Assert.Equal("DS-8632NI-I8", nvr.Model);
        Assert.Equal("V4.61.025", nvr.Firmware!.Version);
        Assert.Equal(new DateOnly(2022, 9, 5), nvr.Firmware.BuildDate);
        Assert.Equal("V4.61.025 · 05.09.2022", nvr.Firmware.Text);
        Assert.Equal("V4.61.025 20220905", nvr.Firmware.Key);
        Assert.Equal("NVR", nvr.DeviceType);
    }

    [Theory]
    [InlineData("V5.5.800 build 210628", null, "V5.5.800", "2021-06-28")]
    [InlineData("V5.7.21 build 231213", null, "V5.7.21", "2023-12-13")]
    [InlineData("V4.61.025", "build 220905", "V4.61.025", "2022-09-05")]
    [InlineData("V4.61.025", null, "V4.61.025", null)]
    [InlineData("V1.0 build 991399", null, "V1.0", null)]   // month 13: no date
    [InlineData("V1.0 build 2106281", null, "V1.0", null)]  // 7 digits: no date
    public void ParsesFirmwareVersions(string version, string? released, string expected, string? date)
    {
        var parsed = FirmwareVersion.Parse(version, released)!;
        Assert.Equal(expected, parsed.Version);
        Assert.Equal(date is null ? null : DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), parsed.BuildDate);
        Assert.Null(FirmwareVersion.Parse("  "));
        Assert.Null(FirmwareVersion.Parse(null));
    }

    const string ChannelsXml = """
        <InputProxyChannelList version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema" size="10">
          <InputProxyChannel><id>1</id><name>Giris</name>
            <sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol><ipAddress>10.0.0.5</ipAddress><userName>admin</userName>
              <model>DS-2CD1043G0-IUF</model><serialNumber>SER</serialNumber><firmwareVersion>V5.5.800 build 210628</firmwareVersion>
            </sourceInputPortDescriptor></InputProxyChannel>
          <InputProxyChannel><id>2</id><name>Bahce</name>
            <sourceInputPortDescriptor><proxyProtocol>ONVIF</proxyProtocol><model>X</model><firmwareVersion>1.0</firmwareVersion>
            </sourceInputPortDescriptor></InputProxyChannel>
          <InputProxyChannel><id>3</id><name></name><sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol></sourceInputPortDescriptor></InputProxyChannel>
        </InputProxyChannelList>
        """;

    const string StatusXml = """
        <InputProxyChannelStatusList version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
          <InputProxyChannelStatus><id>1</id><online>true</online></InputProxyChannelStatus>
          <InputProxyChannelStatus><id>2</id><online>true</online></InputProxyChannelStatus>
          <InputProxyChannelStatus><id>3</id><online>false</online></InputProxyChannelStatus>
        </InputProxyChannelStatusList>
        """;

    [Fact]
    public void ParsesCameraFirmwareWithoutKeepingSecrets()
    {
        var cameras = CameraFirmware.ParseList(XDocument.Parse(ChannelsXml), XDocument.Parse(StatusXml));
        Assert.Equal([1, 2, 3], cameras.Select(c => c.Channel));
        Assert.Equal("DS-2CD1043G0-IUF", cameras[0].Model);
        Assert.Equal("V5.5.800", cameras[0].Firmware!.Version);
        Assert.True(cameras[0].Upgradable);
        Assert.False(cameras[1].Upgradable);          // ONVIF
        Assert.False(cameras[2].Upgradable);          // offline
        Assert.Equal("Kamera 3", cameras[2].Label);
        Assert.Null(cameras[2].Model);
        var text = string.Join("|", cameras.Select(c => c.ToString()));
        Assert.DoesNotContain("SER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", text, StringComparison.Ordinal);

        var noStatus = CameraFirmware.ParseList(XDocument.Parse(ChannelsXml), null);
        Assert.All(noStatus, c => Assert.Null(c.Online));
        Assert.All(noStatus, c => Assert.False(c.Upgradable));
        Assert.Equal(["DS-2CD1043G0-IUF", "X", "model bilinmiyor"], CameraFirmware.Models(cameras));
    }

    // ================================================================ online check (reads only)

    sealed class OnlineFake : IConfigTransport
    {
        public Dictionary<string, Func<XDocument>> Answers { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<XDocument> GetConfigAsync(string path, CancellationToken ct = default)
        {
            Calls.Add(path);
            return Answers.TryGetValue(path, out var answer) ? Task.FromResult(answer()) : throw new IsapiException("yok", HttpStatusCode.NotFound);
        }

        public Task<IsapiResponseStatus> PutConfigAsync(string path, XDocument body, CancellationToken ct = default) =>
            throw new InvalidOperationException("The online check never writes.");
    }

    static XDocument X(string xml) => XDocument.Parse(xml);

    [Fact]
    public async Task OnlineCheckOfARecorderWithoutTheFeature()
    {
        var fake = new OnlineFake();
        var result = await OnlineUpgrade.CheckAsync(fake);
        Assert.Equal(OnlineCheckState.Unsupported, result.State);
        Assert.Contains("Hikvision sitesinden dosya indirin", result.Text, StringComparison.Ordinal);
        Assert.True(result.OffersDownloadPage);
        Assert.Equal([MaintenancePaths.OnlineUpgradeCapabilities], fake.Calls);
    }

    [Fact]
    public async Task OnlineCheckWhenTheServerIsUnreachable()
    {
        var fake = new OnlineFake();
        fake.Answers[MaintenancePaths.OnlineUpgradeCapabilities] = () => X("<OnlineUpgradeCap/>");
        fake.Answers[MaintenancePaths.OnlineUpgradeServer] = () => X("<OnlineUpgradeServer><connectStatus>false</connectStatus></OnlineUpgradeServer>");
        Assert.Equal(OnlineCheckState.ServerUnreachable, (await OnlineUpgrade.CheckAsync(fake)).State);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlineCheckAsksTheServerAndReportsANewVersion()
    {
        var fake = new OnlineFake();
        fake.Answers[MaintenancePaths.OnlineUpgradeCapabilities] = () => X("<OnlineUpgradeCap/>");
        fake.Answers[MaintenancePaths.OnlineUpgradeServer] = () => X("<OnlineUpgradeServer><connectStatus>true</connectStatus></OnlineUpgradeServer>");
        fake.Answers["ISAPI/System/onlineUpgrade/version?check=true"] = () =>
            X("<OnlineUpgradeVersion><newVersionAvailable>true</newVersionAvailable><newVersion>V4.62.000</newVersion><changeLog>fix</changeLog></OnlineUpgradeVersion>");
        var result = await OnlineUpgrade.CheckAsync(fake);
        Assert.Equal(OnlineCheckState.NewVersion, result.State);
        Assert.Equal("V4.62.000", result.NewVersion);
        Assert.Equal("fix", result.ChangeLog);
        Assert.Contains("SPVision dosyayı indirmez", result.Text, StringComparison.Ordinal);
        Assert.All(fake.Calls, c => Assert.DoesNotContain("download", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParsesVersionAnswers()
    {
        Assert.Equal(OnlineCheckState.UpToDate, OnlineUpgrade.ParseVersion(X("<OnlineUpgradeVersion><newVersionAvailable>false</newVersionAvailable><newVersion/></OnlineUpgradeVersion>")).State);
        Assert.False(OnlineUpgrade.ParseVersion(X("<OnlineUpgradeVersion><newVersionAvailable>false</newVersionAvailable></OnlineUpgradeVersion>")).OffersDownloadPage);
        Assert.Equal(OnlineCheckState.Unsupported, OnlineUpgrade.ParseVersion(X("<ResponseStatus><statusCode>4</statusCode><subStatusCode>notSupport</subStatusCode></ResponseStatus>")).State);
        Assert.Equal(OnlineCheckState.Failed, OnlineUpgrade.ParseVersion(X("<ResponseStatus><statusCode>6</statusCode><subStatusCode>badParameters</subStatusCode></ResponseStatus>")).State);
        Assert.Equal(OnlineCheckState.Failed, OnlineUpgrade.ParseVersion(X("<Other/>")).State);
        Assert.Equal("ISAPI/System/onlineUpgrade/version?check=false", MaintenancePaths.OnlineUpgradeVersion(askServer: false));
    }

    // ================================================================ recorder upgrade run

    sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            Now += span;
            return Task.CompletedTask;
        }
    }

    sealed class RecorderFake(params SdkUpgradeSnapshot[] states) : IRecorderFirmwareTransport
    {
        int _index;
        public int Started { get; private set; }
        public int Closed { get; private set; }

        public int Start(FirmwareJob job)
        {
            Started++;
            return 7;
        }

        public SdkUpgradeSnapshot Poll(int handle) => states[Math.Min(_index++, states.Length - 1)];

        public void Close(int handle)
        {
            Assert.Equal(7, handle);
            Closed++;
        }
    }

    async Task<FirmwareJob> RecorderJob()
    {
        var (file, _) = await Firmware();
        return await ConfirmedRiskyWrite.Confirm(MaintenanceWrites.RecorderFirmware("NVR", Nvr, file), new Answer("GÜNCELLE"))!.TakeFirmwareJobAsync();
    }

    async Task<FirmwareJob> CameraJob(params int[] channels)
    {
        var (file, _) = await Firmware();
        var request = MaintenanceWrites.CameraFirmware(channels.Select(c => Cam(c)).ToList(), file, true);
        return await ConfirmedRiskyWrite.Confirm(request, new Answer("GÜNCELLE"))!.TakeFirmwareJobAsync();
    }

    sealed class Collect<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    [Fact]
    public async Task RecorderRunReportsProgressUntilDone()
    {
        var job = await RecorderJob();
        var fake = new RecorderFake(new(2, 10, 1, 0), new(2, 60, 1, 0), new(2, 100, 2, 50), new(1, 100, 2, 100));
        var clock = new FakeClock();
        var progress = new Collect<FirmwareProgress>();

        var result = await RecorderFirmwareRun.RunAsync(job, fake, progress, clock.Delay, () => clock.Now);

        Assert.Equal(FirmwareOutcome.Done, result.Outcome);
        Assert.Equal(1, fake.Started);
        Assert.Equal(1, fake.Closed);
        Assert.Equal([0, 10, 60, 100], progress.Items.Select(p => p.Percent));
        Assert.Equal("Dosya gönderiliyor · %10", progress.Items[1].Text);
        Assert.Equal("Sistem yükseltiliyor · %100", progress.Items[3].Text);
    }

    [Theory]
    [InlineData(3, FirmwareOutcome.Failed)]
    [InlineData(5, FirmwareOutcome.Failed)]
    [InlineData(7, FirmwareOutcome.Failed)]
    [InlineData(8, FirmwareOutcome.Failed)]
    [InlineData(4, FirmwareOutcome.Unknown)]
    public async Task RecorderRunMapsFinalStates(int state, FirmwareOutcome outcome)
    {
        var job = await RecorderJob();
        var fake = new RecorderFake(new(2, 30, 1, 0), new(state, 30, 1, 0));
        var clock = new FakeClock();
        var result = await RecorderFirmwareRun.RunAsync(job, fake, null, clock.Delay, () => clock.Now);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(1, fake.Closed);
        if (state == 7)
        {
            Assert.Contains("yanlış model", result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RecorderRunGivesUpOnAStallOrUnreadableState()
    {
        var job = await RecorderJob();
        var clock = new FakeClock();
        var stalled = await RecorderFirmwareRun.RunAsync(job, new RecorderFake(new SdkUpgradeSnapshot(2, 40, 1, 0)), null, clock.Delay, () => clock.Now);
        Assert.Equal(FirmwareOutcome.Unknown, stalled.Outcome);
        Assert.Contains("KESMEYİN", stalled.Text, StringComparison.Ordinal);

        var job2 = await RecorderJob();
        var unreadable = await RecorderFirmwareRun.RunAsync(job2, new RecorderFake(new SdkUpgradeSnapshot(-1, -1, -1, 0)), null, clock.Delay, () => clock.Now);
        Assert.Equal(FirmwareOutcome.Unknown, unreadable.Outcome);
    }

    [Fact]
    public async Task RunnersRefuseTheOtherKindOfJob()
    {
        var cameraJob = await CameraJob(1);
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => RecorderFirmwareRun.RunAsync(cameraJob, new RecorderFake(new SdkUpgradeSnapshot(1, 100, 2, 0))));
        var recorderJob = await RecorderJob();
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() =>
            CameraFirmwareBatch.RunAsync(recorderJob, new Dictionary<int, string?>(), new CameraFake(), stopOnFirstFailure: true));
    }

    // ================================================================ waiting for the return

    [Fact]
    public async Task ReturnDetectsNewVersionSameVersionNoRestartAndNotBack()
    {
        async Task<ReturnOutcome> Wait(params string?[] answers)
        {
            var clock = new FakeClock();
            var i = 0;
            var (outcome, _) = await FirmwareReturn.WaitAsync(_ => Task.FromResult(answers[Math.Min(i++, answers.Length - 1)]), "V1",
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20), null, clock.Delay, () => clock.Now);
            return outcome;
        }

        Assert.Equal(ReturnOutcome.NewVersion, await Wait(null, null, "V2"));
        Assert.Equal(ReturnOutcome.SameVersion, await Wait("V1", null, "V1"));
        Assert.Equal(ReturnOutcome.NoRestart, await Wait("V1"));
        Assert.Equal(ReturnOutcome.NotBack, await Wait((string?)null));
    }

    // ================================================================ camera batch

    sealed class CameraFake : ICameraFirmwareTransport
    {
        int _running;
        public List<int> Order { get; } = [];
        public HashSet<int> Fail { get; } = [];
        public Dictionary<int, string?> After { get; } = [];
        public int MaxConcurrent { get; private set; }

        public async Task<CameraUpgradeResult> UpgradeAsync(FirmwareJob job, int channel, IProgress<int>? percent, CancellationToken ct)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _running));
            Order.Add(channel);
            percent?.Report(50);
            await Task.Yield();
            Interlocked.Decrement(ref _running);
            return Fail.Contains(channel) ? new CameraUpgradeResult(false, "Yazılım dosyası bu kamera modeline ait değil.") : new CameraUpgradeResult(true, "ok");
        }

        public Task<string?> ReadVersionAsync(int channel, CancellationToken ct) =>
            Task.FromResult(After.TryGetValue(channel, out var v) ? v : "V2");
    }

    [Fact]
    public async Task CamerasRunOneAfterAnotherAndStopAtTheFirstFailure()
    {
        var job = await CameraJob(3, 1, 2);
        var fake = new CameraFake { Fail = { 1 } };
        var clock = new FakeClock();
        var updates = new Collect<CameraUpdate>();
        var old = new Dictionary<int, string?> { [1] = "V1", [2] = "V1", [3] = "V1" };

        var results = await CameraFirmwareBatch.RunAsync(job, old, fake, stopOnFirstFailure: true, updates, clock.Delay, () => clock.Now);

        Assert.Equal([3, 1], fake.Order);                // 2 never started
        Assert.Equal(1, fake.MaxConcurrent);
        Assert.Equal([CameraStep.Done, CameraStep.Failed, CameraStep.Skipped], results.Select(r => r.Step));
        Assert.Contains("modeline ait değil", results[1].Text, StringComparison.Ordinal);
        Assert.Contains(updates.Items, u => u is { Channel: 3, Step: CameraStep.Uploading, Percent: 50 });
        Assert.Contains(updates.Items, u => u is { Channel: 3, Step: CameraStep.Restarting });
        Assert.Equal(3, updates.Items.TakeWhile(u => u.Step == CameraStep.Waiting).Count());
    }

    [Fact]
    public async Task CamerasContinueAfterAFailureWhenAskedAndCheckTheNewVersion()
    {
        var job = await CameraJob(1, 2, 3);
        var fake = new CameraFake { Fail = { 1 }, After = { [3] = "V1" } };
        var clock = new FakeClock();
        var old = new Dictionary<int, string?> { [1] = "V1", [2] = "V1", [3] = "V1" };

        var results = await CameraFirmwareBatch.RunAsync(job, old, fake, stopOnFirstFailure: false, null, clock.Delay, () => clock.Now);

        Assert.Equal([1, 2, 3], fake.Order);
        Assert.Equal([CameraStep.Failed, CameraStep.Done, CameraStep.Failed], results.Select(r => r.Step));
        Assert.Equal("Güncellendi · V2", results[1].Text);
        Assert.Contains("sürüm değişmedi", results[2].Text, StringComparison.Ordinal); // never went away, same version
    }

    // ================================================================ web UI address

    [Theory]
    [InlineData(true, 443, "192.168.1.2", "https://192.168.1.2/")]
    [InlineData(true, 8443, "192.168.1.2", "https://192.168.1.2:8443/")]
    [InlineData(false, 80, "192.168.1.2", "http://192.168.1.2/")]
    [InlineData(false, 8080, "nvr.local", "http://nvr.local:8080/")]
    [InlineData(true, 443, "fe80::1", "https://[fe80::1]/")]
    public void WebUiAddressFollowsTheDeviceSetting(bool https, int port, string host, string expected) =>
        Assert.Equal(expected, WebUi.Address(new Device { Host = host, HttpPort = port, UseHttps = https }));

    // ---- helpers ----

    sealed class Answer(string? typed) : IRiskyConfirmation
    {
        public string? Ask(RiskyWriteRequest request) => typed;
    }

    static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString><subStatusCode>ok</subStatusCode></ResponseStatus>",
            Encoding.UTF8, "application/xml"),
    };

    sealed record Sent(string Method, string PathAndQuery, string? Body);

    sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return answer(request);
        }
    }
}
