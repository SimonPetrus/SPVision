using System.Globalization;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

public class IsapiWritePolicyTests
{
    public static TheoryData<string> PhaseOnePaths()
    {
        var data = new TheoryData<string> { IsapiPaths.DeviceInfo, IsapiPaths.Time };
        foreach (var channel in new[] { 1, 9, 22, 32 })
        {
            data.Add(IsapiPaths.Overlays(channel));
            data.Add(IsapiPaths.Motion(channel));
            data.Add(IsapiPaths.Image(channel));
            data.Add(IsapiPaths.Stream(channel, StreamQuality.Main));
            data.Add(IsapiPaths.Stream(channel, StreamQuality.Sub));
            data.Add(IsapiPaths.RecordTrack(channel));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PhaseOnePaths))]
    public void AllowsEveryPhaseOneWritePath(string path)
    {
        Assert.Null(IsapiWritePolicy.Check(path));
    }

    [Theory]
    [InlineData("ISAPI/System/Network/EZVIZ")]                  // Hik-Connect + stream encryption
    [InlineData("ISAPI/Streaming/encryption")]
    [InlineData("ISAPI/System/Video/inputs/channels/1/encryption")]
    [InlineData("ISAPI/System/HikConnect")]
    [InlineData("ISAPI/System/Hik-Connect")]
    [InlineData("ISAPI/System/platform/1")]
    [InlineData("ISAPI/Security/users/1")]
    [InlineData("ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/System/Users")]
    [InlineData("ISAPI/System/Network/interfaces/1")]
    [InlineData("ISAPI/System/factoryReset")]
    [InlineData("ISAPI/System/reboot")]
    [InlineData("ISAPI/ContentMgmt/Storage/hdd/1/format")]
    [InlineData("ISAPI/System/upgradeStatus")]
    [InlineData("ISAPI/System/activate")]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1")]             // camera record (address + login): rename blocked
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/22")]
    public void DeniesSecurityAndDestructivePaths(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureAllowed(path));
    }

    [Theory]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1/reboot")]       // keyword wins over a near-allowed shape
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1/Network")]
    [InlineData("isapi/system/network/ezviz")]                           // case does not help
    [InlineData("ISAPI/SYSTEM/NETWORK/EZVIZ")]
    [InlineData("ISAPI/system/SECURITY/users")]
    public void DeniedKeywordsIgnoreCase(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
    }

    [Fact]
    public void KeywordsAreCheckedCultureIndependently()
    {
        // In tr-TR "I".ToLower() is "ı": a culture-sensitive comparison would miss "ezviz" / "securıty".
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.NotNull(IsapiWritePolicy.Check("isapi/system/network/ezviz"));
            Assert.NotNull(IsapiWritePolicy.Check("ISAPI/SECURITY/USERS/1"));
            Assert.NotNull(IsapiWritePolicy.Check("ISAPI/System/ACTIVATE"));
            Assert.Null(IsapiWritePolicy.Check("ISAPI/ContentMgmt/InputProxy/channels/1/video/motionDetection"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/ISAPI/System/deviceInfo")]
    [InlineData("ISAPI/System/deviceInfo?format=json")]
    [InlineData("ISAPI/System/deviceInfo#x")]
    [InlineData("ISAPI/System/time/../deviceInfo")]
    [InlineData("ISAPI/System/%74ime")]
    [InlineData("ISAPI//System/time")]
    [InlineData("ISAPI\\System\\time")]
    [InlineData("http://192.0.2.1/ISAPI/System/time")]
    [InlineData("ISAPI/System/deviceinfo")]                               // allowlist is exact case
    [InlineData("ISAPI/System/Video/inputs/channels/1/motionDetection")]  // not used by phase 1
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/0")]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1234")]
    [InlineData("ISAPI/ContentMgmt/StreamingProxy/channels/103")]
    [InlineData("ISAPI/ContentMgmt/record/tracks/102")]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels")]
    public void DeniesEverythingElse(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
    }

    [Theory]
    [InlineData("<InputProxyChannel><id>1</id><password>x</password></InputProxyChannel>")]
    [InlineData("<EZVIZ><enabled>true</enabled><streamEncrypteEnabled>true</streamEncrypteEnabled></EZVIZ>")]
    [InlineData("<X><verificationCode>ABC</verificationCode></X>")]
    public void DeniesBodiesWithSecrets(string xml)
    {
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureBodyAllowed(XDocument.Parse(xml)));
    }

    [Fact]
    public async Task WriterRefusesTheWholeBatchBeforeAnyRequest()
    {
        var transport = new FakeTransport();
        transport.Docs[IsapiPaths.Motion(1)] = XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>");
        var writes = new List<ConfigWrite>
        {
            new(IsapiPaths.Motion(1), "Kamera", [new SetValueEdit("Hareket algılama", "enabled", "false", "Açık", "Kapalı")]),
            new("ISAPI/System/Network/EZVIZ", "Kayıt cihazı", [new SetValueEdit("Hik-Connect", "enabled", "false", "Açık", "Kapalı")]),
        };

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => ConfigWriter.ApplyAsync(transport, writes));
        Assert.Empty(transport.Calls);
    }
}
