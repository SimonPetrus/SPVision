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
    [InlineData("""<ResponseStatus><statusCode>3</statusCode><statusString>Device Busy</statusString></ResponseStatus>""", "Device Busy")]
    [InlineData("""<ResponseStatus><statusCode>6</statusCode><subStatusCode>badParameters</subStatusCode></ResponseStatus>""", "badParameters")]
    [InlineData("""<CMSearchResult><responseStatus>false</responseStatus><responseStatusStrg>FAILED</responseStatusStrg></CMSearchResult>""", "FAILED")]
    public void RefusedSearchIsAnErrorNotNoRecordings(string xml, string reason)
    {
        // Hand-written: a refusal with HTTP 200 must not be read as "no recordings" (the calendar would keep that answer).
        var e = Assert.Throws<IsapiException>(() => IsapiClient.ParseSearchResult(XDocument.Parse(xml)));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void NoMatchesWithFalseStatusIsStillNoRecordings()
    {
        var doc = XDocument.Parse($"""
            <CMSearchResult xmlns="{Ns}"><responseStatus>false</responseStatus><responseStatusStrg>NO MATCHES</responseStatusStrg></CMSearchResult>
            """);

        Assert.Empty(IsapiClient.ParseSearchResult(doc).Segments);
    }

    [Fact]
    public async Task DaySearchRefusalThrows()
    {
        var handler = new SearchPages($"""<ResponseStatus xmlns="{Ns}"><statusCode>3</statusCode><statusString>Device Busy</statusString></ResponseStatus>""");
        using var client = new IsapiClient(new Uri("http://recorder.test/"), handler);

        await Assert.ThrowsAsync<IsapiException>(() => client.HasRecordingsAsync(1, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20, 23, 59, 59)));
    }

    [Theory]
    [InlineData("2026-10-20T08:15:30Z")]
    [InlineData("2026-10-20T08:15:30+03:00")]
    [InlineData("2026-10-20T08:15:30-05:00")]
    [InlineData("2026-10-20T08:15:30")]
    [InlineData(" 2026-10-20T08:15:30Z ")]
    public void ParsesDeviceTimesAsWallClock(string text) =>
        Assert.Equal(new DateTime(2026, 10, 20, 8, 15, 30), IsapiClient.ParseTime(text));

    [Fact]
    public void UnreadableTimeDropsOnlyThatItem()
    {
        Assert.Equal(default, IsapiClient.ParseTime("yesterday"));

        var doc = XDocument.Parse($"""
            <CMSearchResult xmlns="{Ns}"><responseStatusStrg>OK</responseStatusStrg><matchList>
              <searchMatchItem><timeSpan><startTime>bad</startTime><endTime>2026-10-20T09:00:00Z</endTime></timeSpan></searchMatchItem>
              <searchMatchItem><timeSpan><startTime>2026-10-20T10:00:00Z</startTime><endTime>2026-10-20T11:00:00Z</endTime></timeSpan></searchMatchItem>
            </matchList></CMSearchResult>
            """);

        var (segments, _) = IsapiClient.ParseSearchResult(doc);

        Assert.Single(segments);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0), segments[0].Start);
    }

    /// <summary>Answers ContentMgmt searches with fixed pages and records the requests (no network).</summary>
    sealed class SearchPages(params string[] pages) : HttpMessageHandler
    {
        public List<XDocument> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/ISAPI/ContentMgmt/search", request.RequestUri!.AbsolutePath);
            Requests.Add(XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(pages[Requests.Count - 1]) };
        }
    }

    static string Page(string status, int firstHour, int count) =>
        $"""<CMSearchResult xmlns="{Ns}"><responseStatusStrg>{status}</responseStatusStrg><matchList>""" +
        string.Concat(Enumerable.Range(firstHour, count).Select(h =>
            $"<searchMatchItem><timeSpan><startTime>2026-10-20T{h:00}:00:00Z</startTime><endTime>2026-10-20T{h:00}:30:00Z</endTime></timeSpan></searchMatchItem>")) +
        "</matchList></CMSearchResult>";

    [Fact]
    public async Task SearchContinuesAfterTheResultsActuallyReceived()
    {
        // The recorder returns fewer results per page than asked (here 3, then 2): the next page must start at 3.
        var handler = new SearchPages(Page("MORE", 0, 3), Page("OK", 3, 2));
        using var client = new IsapiClient(new Uri("http://recorder.test/"), handler);

        var segments = await client.SearchRecordingsAsync(1, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20, 23, 59, 59));

        Assert.Equal(5, segments.Count);
        Assert.Equal(Enumerable.Range(0, 5).Select(h => new DateTime(2026, 10, 20, h, 0, 0)), segments.Select(s => s.Start));
        Assert.Equal(new[] { "0", "3" }, handler.Requests.Select(r => r.Root!.Element("searchResultPostion")!.Value));
        Assert.All(handler.Requests, r => Assert.Equal("101", r.Root!.Element("trackList")!.Element("trackID")!.Value));
        Assert.Single(handler.Requests.Select(r => r.Root!.Element("searchID")!.Value).Distinct()); // one search, paged
    }

    [Fact]
    public async Task DaySearchAsksForOneResult()
    {
        var handler = new SearchPages(Page("MORE", 8, 1), $"""<CMSearchResult xmlns="{Ns}"><responseStatusStrg>NO MATCHES</responseStatusStrg></CMSearchResult>""");
        using var client = new IsapiClient(new Uri("http://recorder.test/"), handler);

        Assert.True(await client.HasRecordingsAsync(2, new DateTime(2026, 10, 20), new DateTime(2026, 10, 20, 23, 59, 59)));
        Assert.False(await client.HasRecordingsAsync(2, new DateTime(2026, 10, 21), new DateTime(2026, 10, 21, 23, 59, 59)));
        Assert.All(handler.Requests, r => Assert.Equal("1", r.Root!.Element("maxResults")!.Value));
        Assert.Equal("201", handler.Requests[0].Root!.Element("trackList")!.Element("trackID")!.Value);
    }

    // ---- Month query (calendar dots): record/tracks/{t}/dailyDistribution ----

    static string Distribution(string xmlns, params (int Day, string Record)[] days) =>
        $"<trackDailyDistribution version=\"2.0\"{xmlns}><dayList>" +
        string.Concat(days.Select(d => $"<day><id>{d.Day}</id><dayOfMonth>{d.Day}</dayOfMonth><record>{d.Record}</record><recordType>time</recordType></day>")) +
        "</dayList></trackDailyDistribution>";

    [Fact]
    public void ParsesDailyDistributionWithNamespace()
    {
        var doc = XDocument.Parse(Distribution(" xmlns=\"http://www.isapi.org/ver20/XMLSchema\"", (1, "true"), (2, "false"), (3, "TRUE"), (4, "false")));

        Assert.Equal(new[] { 1, 3 }, IsapiClient.ParseDailyDistribution(doc)!.Order());
    }

    [Fact]
    public void ParsesDailyDistributionWithoutNamespace()
    {
        var doc = XDocument.Parse("""
            <trackDailyDistribution><dayList>
              <day><id>5</id><dayOfMonth>5</dayOfMonth><record>true</record></day>
              <day><id>6</id><record>true</record></day>
              <day><id>7</id><dayOfMonth>7</dayOfMonth><record>false</record></day>
              <day><id>x</id><dayOfMonth>?</dayOfMonth><record>true</record></day>
            </dayList></trackDailyDistribution>
            """);

        Assert.Equal(new[] { 5, 6 }, IsapiClient.ParseDailyDistribution(doc)!.Order()); // 6: id when dayOfMonth is missing
        Assert.Empty(IsapiClient.ParseDailyDistribution(XDocument.Parse(Distribution("")))!); // a month without recordings
    }

    [Fact]
    public void DailyDistributionWithoutDayListIsUnsupported()
    {
        Assert.Null(IsapiClient.ParseDailyDistribution(XDocument.Parse(
            $"""<ResponseStatus xmlns="{Ns}"><statusCode>4</statusCode><statusString>Invalid Operation</statusString><subStatusCode>notSupport</subStatusCode></ResponseStatus>""")));
        Assert.Null(IsapiClient.ParseDailyDistribution(XDocument.Parse("<trackDailyDistribution />")));
    }

    [Fact]
    public void MonthQueryRequest()
    {
        var body = XDocument.Parse(IsapiClient.BuildDailyDistributionRequest(new DateTime(2026, 3, 1)));

        Assert.Equal("trackDailyParam", body.Root!.Name.LocalName);
        Assert.Equal("2026", body.Root.Element("year")!.Value);
        Assert.Equal("3", body.Root.Element("monthOfYear")!.Value);
    }

    /// <summary>Answers the month query with a fixed status and body, and records what was asked (no network).</summary>
    sealed class MonthAnswer(System.Net.HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, XDocument Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, XDocument.Parse(await request.Content!.ReadAsStringAsync(ct))));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task MonthQueryIsOneReadOnlyPostPerCameraAndMonth()
    {
        var handler = new MonthAnswer(System.Net.HttpStatusCode.OK, Distribution(" xmlns=\"http://www.isapi.org/ver20/XMLSchema\"", (20, "true"), (21, "true"), (22, "false")));
        using var client = new IsapiClient(new Uri("http://recorder.test/"), handler);

        var days = await client.GetRecordedDaysAsync(3, new DateTime(2026, 10, 1));

        Assert.Equal(new[] { 20, 21 }, days!.Order());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method); // search semantics, never a PUT
        Assert.Equal("/ISAPI/ContentMgmt/record/tracks/301/dailyDistribution", request.Path);
        Assert.Equal("10", request.Body.Root!.Element("monthOfYear")!.Value);
        // Not a write: the PUT allowlist does not cover it (its record/tracks pattern is the schedule only).
        Assert.NotNull(IsapiWritePolicy.Check("ISAPI/ContentMgmt/record/tracks/301/dailyDistribution"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "")]
    [InlineData(System.Net.HttpStatusCode.NotFound, "")]
    [InlineData(System.Net.HttpStatusCode.OK, "not xml")]
    [InlineData(System.Net.HttpStatusCode.OK, "<ResponseStatus><statusCode>4</statusCode><subStatusCode>notSupport</subStatusCode></ResponseStatus>")]
    public async Task MonthQueryFallsBackToDaySearchesWhenUnsupported(System.Net.HttpStatusCode status, string body)
    {
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new MonthAnswer(status, body));

        Assert.Null(await client.GetRecordedDaysAsync(1, new DateTime(2026, 10, 1)));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    public async Task MonthQueryFailureIsRetriedNotTakenAsUnsupported(System.Net.HttpStatusCode status)
    {
        using var client = new IsapiClient(new Uri("http://recorder.test/"), new MonthAnswer(status, ""));

        var error = await Assert.ThrowsAsync<IsapiException>(() => client.GetRecordedDaysAsync(1, new DateTime(2026, 10, 1)));
        Assert.Equal(status, error.Status);
        Assert.False(IsapiClient.MonthQueryUnsupported(status));
        Assert.True(IsapiClient.MonthQueryUnsupported(System.Net.HttpStatusCode.NotFound));
    }
}
