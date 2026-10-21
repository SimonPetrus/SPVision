using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

public class IsapiParsingTests
{
    const string Ns = "http://www.hikvision.com/ver20/XMLSchema";

    [Fact]
    public void ParsesDeviceInfo()
    {
        var doc = XDocument.Parse($"""
            <DeviceInfo version="2.0" xmlns="{Ns}">
              <deviceName>Network Video Recorder</deviceName>
              <model>DS-7616NI-K2</model>
              <serialNumber>DS-7616NI-K21620200101CCRRD12345678WCVU</serialNumber>
              <firmwareVersion>V4.22.005</firmwareVersion>
              <deviceType>NVR</deviceType>
            </DeviceInfo>
            """);

        var info = IsapiClient.ParseDeviceInfo(doc);

        Assert.Equal("Network Video Recorder", info.Name);
        Assert.Equal("DS-7616NI-K2", info.Model);
        Assert.Equal("V4.22.005", info.Firmware);
        Assert.Equal("NVR", info.DeviceType);
    }

    [Fact]
    public void ParsesStreamEncryptionAndVerificationCode()
    {
        var doc = XDocument.Parse("""
            <EZVIZ version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
              <enabled>true</enabled>
              <registerStatus>true</registerStatus>
              <verificationCode>ABCDEF</verificationCode>
              <streamEncrypteEnabled>true</streamEncrypteEnabled>
            </EZVIZ>
            """);

        var info = IsapiClient.ParseStreamEncryption(doc);

        Assert.True(info.Enabled);
        Assert.Equal("ABCDEF", info.VerificationCode);
    }

    [Fact]
    public void ParsesInputProxyChannelsWithStatus()
    {
        var channels = XDocument.Parse($"""
            <InputProxyChannelList xmlns="{Ns}">
              <InputProxyChannel><id>1</id><name>DIS KAPI</name>
                <sourceInputPortDescriptor><ipAddress>192.168.1.64</ipAddress></sourceInputPortDescriptor>
              </InputProxyChannel>
              <InputProxyChannel><id>2</id><name>ATOLYE 3</name></InputProxyChannel>
            </InputProxyChannelList>
            """);
        var status = XDocument.Parse($"""
            <InputProxyChannelStatusList xmlns="{Ns}">
              <InputProxyChannelStatus><id>1</id><online>true</online></InputProxyChannelStatus>
              <InputProxyChannelStatus><id>2</id><online>false</online></InputProxyChannelStatus>
            </InputProxyChannelStatusList>
            """);

        var cameras = IsapiClient.ParseInputProxyChannels(channels);
        IsapiClient.ApplyInputProxyStatus(cameras, status);

        Assert.Collection(cameras,
            c => { Assert.Equal(1, c.Channel); Assert.Equal("DIS KAPI", c.Name); Assert.True(c.Online); },
            c => { Assert.Equal(2, c.Channel); Assert.Equal("ATOLYE 3", c.Name); Assert.False(c.Online); });
    }

    [Fact]
    public void ParsesVideoInputsSkippingDisabled()
    {
        var doc = XDocument.Parse("""
            <VideoInputChannelList xmlns="http://www.isapi.org/ver20/XMLSchema">
              <VideoInputChannel><id>1</id><name>Camera 01</name><videoInputEnabled>true</videoInputEnabled></VideoInputChannel>
              <VideoInputChannel><id>2</id><name>Camera 02</name><videoInputEnabled>false</videoInputEnabled></VideoInputChannel>
            </VideoInputChannelList>
            """);

        var cameras = IsapiClient.ParseVideoInputs(doc);

        Assert.Single(cameras);
        Assert.Equal("Camera 01", cameras[0].Name);
    }

    [Fact]
    public void BuildsSearchRequest()
    {
        var xml = IsapiClient.BuildSearchRequest("ABC", 301,
            new DateTime(2026, 10, 20, 0, 0, 0), new DateTime(2026, 10, 20, 23, 59, 59), 100, 50);

        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("CMSearchDescription", root.Name.LocalName);
        Assert.Equal("301", root.Element("trackList")!.Element("trackID")!.Value);
        Assert.Equal("2026-10-20T00:00:00Z", root.Descendants("startTime").Single().Value);
        Assert.Equal("2026-10-20T23:59:59Z", root.Descendants("endTime").Single().Value);
        Assert.Equal("100", root.Element("searchResultPostion")!.Value);
    }

    [Fact]
    public void ParsesSearchResultAndMoreFlag()
    {
        var doc = XDocument.Parse($"""
            <CMSearchResult xmlns="{Ns}">
              <searchID>ABC</searchID>
              <responseStatus>true</responseStatus>
              <responseStatusStrg>MORE</responseStatusStrg>
              <numOfMatches>2</numOfMatches>
              <matchList>
                <searchMatchItem>
                  <trackID>101</trackID>
                  <timeSpan><startTime>2026-10-20T08:00:00Z</startTime><endTime>2026-10-20T09:00:00Z</endTime></timeSpan>
                  <mediaSegmentDescriptor><playbackURI>rtsp://192.168.1.2/Streaming/tracks/101/?starttime=20261020T080000Z</playbackURI></mediaSegmentDescriptor>
                </searchMatchItem>
                <searchMatchItem>
                  <timeSpan><startTime>2026-10-20T09:00:00Z</startTime><endTime>2026-10-20T09:30:00Z</endTime></timeSpan>
                </searchMatchItem>
              </matchList>
            </CMSearchResult>
            """);

        var (segments, more) = IsapiClient.ParseSearchResult(doc);

        Assert.True(more);
        Assert.Equal(2, segments.Count);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), segments[0].Start);
        Assert.Equal(new DateTime(2026, 10, 20, 9, 30, 0), segments[1].End);
        Assert.StartsWith("rtsp://", segments[0].PlaybackUri);
    }

    [Fact]
    public void ParsesNoMatches()
    {
        var doc = XDocument.Parse($"""
            <CMSearchResult xmlns="{Ns}"><responseStatusStrg>NO MATCHES</responseStatusStrg><numOfMatches>0</numOfMatches></CMSearchResult>
            """);

        var (segments, more) = IsapiClient.ParseSearchResult(doc);

        Assert.Empty(segments);
        Assert.False(more);
    }

    [Theory]
    [InlineData("2026-10-20T08:15:30Z")]
    [InlineData("2026-10-20T08:15:30+03:00")]
    [InlineData("2026-10-20T08:15:30")]
    public void ParsesDeviceTimesAsWallClock(string text) =>
        Assert.Equal(new DateTime(2026, 10, 20, 8, 15, 30), IsapiClient.ParseTime(text));
}
