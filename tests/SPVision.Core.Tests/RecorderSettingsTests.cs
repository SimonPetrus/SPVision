using System.Globalization;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

// Hand-written, anonymized fixtures shaped like an NVR's ISAPI documents.
public class RecorderSettingsTests
{
    const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    static readonly string[] Days = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    static string Action(int id, string start, string end, string startTime = "00:00:00", string endTime = "00:00:00",
        bool record = true, string mode = "CMR") => $"""
        <ScheduleAction><id>{id}</id>
          <ScheduleActionStartTime><DayOfWeek>{start}</DayOfWeek><TimeOfDay>{startTime}</TimeOfDay></ScheduleActionStartTime>
          <ScheduleActionEndTime><DayOfWeek>{end}</DayOfWeek><TimeOfDay>{endTime}</TimeOfDay></ScheduleActionEndTime>
          <ScheduleDSTEnable>false</ScheduleDSTEnable><Description>nothing</Description>
          <Actions><Record>{(record ? "true" : "false")}</Record><Log>false</Log><SaveImg>false</SaveImg><ActionRecordingMode>{mode}</ActionRecordingMode></Actions>
        </ScheduleAction>
        """;

    static string Week(string mode) => string.Concat(Enumerable.Range(0, 7).Select(i => Action(i + 1, Days[i], Days[(i + 1) % 7], mode: mode)));

    static XDocument Track(string actions, bool enabled = true, int pre = 5, int post = 5) => XDocument.Parse($"""
        <Track version="2.0" xmlns="{Ns}">
          <id>101</id><Channel>101</Channel><Enable>false</Enable>
          <TrackGUID>00000000-0000-0000-0000-000000000001</TrackGUID>
          <DefaultRecordingMode>CMR</DefaultRecordingMode>
          <TrackSchedule><ScheduleBlockList><ScheduleBlock>
            <ScheduleBlockGUID>00000000-0000-0000-0000-000000000002</ScheduleBlockGUID>
            <ScheduleBlockType>www.hikvision.com/racm/schedule/ver10</ScheduleBlockType>
            {actions}
          </ScheduleBlock></ScheduleBlockList></TrackSchedule>
          <CustomExtensionList><CustomExtension>
            <CustomExtensionName>www.hikvision.com/RaCM/trackExt/ver10</CustomExtensionName>
            <enableSchedule>{(enabled ? "true" : "false")}</enableSchedule>
            <SaveAudio>false</SaveAudio>
            <PreRecordTimeSeconds>{pre}</PreRecordTimeSeconds>
            <PostRecordTimeSeconds>{post}</PostRecordTimeSeconds>
          </CustomExtension></CustomExtensionList>
        </Track>
        """);

    static XDocument MotionDoc(bool enabled = true, int level = 60) => XDocument.Parse($"""
        <MotionDetection version="2.0" xmlns="{Ns}">
          <enabled>{(enabled ? "true" : "false")}</enabled>
          <enableHighlight>true</enableHighlight>
          <regionType>grid</regionType>
          <Grid><rowGranularity>18</rowGranularity><columnGranularity>22</columnGranularity></Grid>
          <MotionDetectionLayout><sensitivityLevel>{level}</sensitivityLevel><layout><gridMap>ffff00</gridMap></layout></MotionDetectionLayout>
        </MotionDetection>
        """);

    static readonly XDocument MotionCaps = XDocument.Parse($"""
        <MotionDetection version="2.0" xmlns="{Ns}">
          <enabled opt="true,false">true</enabled>
          <MotionDetectionLayout><sensitivityLevel min="0" max="100" step="20">60</sensitivityLevel></MotionDetectionLayout>
        </MotionDetection>
        """);

    // ---------------------------------------------------------------- ResponseStatus

    [Fact]
    public void ParsesOkResponseStatus()
    {
        var status = IsapiResponseStatus.Parse(XDocument.Parse($"""
            <ResponseStatus version="2.0" xmlns="{Ns}">
              <requestURL>/ISAPI/System/time</requestURL><statusCode>1</statusCode><statusString>OK</statusString><subStatusCode>ok</subStatusCode>
            </ResponseStatus>
            """));

        Assert.NotNull(status);
        Assert.True(status.IsOk);
    }

    [Fact]
    public void FailedResponseStatusHasTurkishText()
    {
        var status = IsapiResponseStatus.Parse(XDocument.Parse($"""
            <ResponseStatus version="2.0" xmlns="{Ns}">
              <statusCode>4</statusCode><statusString>Invalid Operation</statusString><subStatusCode>notSupport</subStatusCode>
            </ResponseStatus>
            """))!;

        Assert.False(status.IsOk);
        Assert.Equal("Geçersiz işlem (notSupport: cihaz bu ayarı desteklemiyor)", status.Describe());
    }

    [Fact]
    public void RebootRequiredIsNotOk()
    {
        var status = IsapiResponseStatus.Parse(XDocument.Parse("<ResponseStatus><statusCode>7</statusCode><subStatusCode>rebootRequired</subStatusCode></ResponseStatus>"))!;

        Assert.False(status.IsOk);
        Assert.StartsWith("Cihazın yeniden başlatılması gerekiyor", status.Describe());
    }

    [Fact]
    public void OtherDocumentsAreNotResponseStatus()
    {
        Assert.Null(IsapiResponseStatus.Parse(MotionDoc()));
        Assert.False(IsapiResponseStatus.FromHttp(System.Net.HttpStatusCode.OK).IsOk);
    }

    // ---------------------------------------------------------------- XML patching

    [Fact]
    public void SetValueEditChangesOnlyThatElement()
    {
        var doc = MotionDoc(enabled: true, level: 60);
        var expected = MotionDoc(enabled: true, level: 80);

        new SetValueEdit("Hassasiyet", MotionSettings.SensitivityPath, "80", "Orta", "Yüksek").Apply(doc.Root!);

        Assert.True(XNode.DeepEquals(expected, doc));
        Assert.Equal(Ns, doc.Root!.Name.NamespaceName);
    }

    [Fact]
    public void SetValueEditDoesNotCreateMissingElements()
    {
        var doc = MotionDoc();

        Assert.Throws<ConfigEditException>(() => new SetValueEdit("x", "Video/videoCodecType", "H.264", "a", "b").Apply(doc.Root!));
    }

    [Fact]
    public void FindIsNamespaceAgnosticAndUsesDirectChildren()
    {
        var doc = MotionDoc();

        Assert.Equal("true", IsapiXml.Get(doc.Root!, "enabled"));
        Assert.Null(IsapiXml.Find(doc.Root!, "sensitivityLevel")); // not a direct child of the root
    }

    // ---------------------------------------------------------------- Diff text

    [Fact]
    public void ChangeTextFormat()
    {
        Assert.Equal("DIS KAPI · Hareket algılama: Açık → Kapalı", ConfigChangeText.Format("DIS KAPI", "Hareket algılama", "Açık", "Kapalı"));
        Assert.Equal("Kayıt cihazı · Cihaz adı: (boş) → NVR", ConfigChangeText.Format("Kayıt cihazı", "Cihaz adı", "", "NVR"));
    }

    [Fact]
    public void WriteDescriptionsListEachEdit()
    {
        var before = MotionSettings.Parse(MotionDoc(true, 60), MotionCaps)!;

        var write = RecorderPlan.Motion(3, "ATOLYE", before, enabled: false, level: MotionLevel.High)!;

        Assert.Equal(IsapiPaths.Motion(3), write.Path);
        Assert.Equal(
            ["ATOLYE · Hareket algılama: Açık → Kapalı", "ATOLYE · Hassasiyet: Orta (60) → Yüksek (80)"],
            write.Descriptions.ToList());
    }

    // ---------------------------------------------------------------- Sensitivity

    [Theory]
    [InlineData(0, MotionLevel.Low)]
    [InlineData(20, MotionLevel.Low)]
    [InlineData(40, MotionLevel.Medium)]
    [InlineData(60, MotionLevel.Medium)]
    [InlineData(80, MotionLevel.High)]
    [InlineData(100, MotionLevel.High)]
    public void SensitivityFromDevice(int value, MotionLevel expected)
    {
        Assert.Equal(expected, MotionSensitivity.FromDevice(value, 0, 100));
    }

    [Theory]
    [InlineData(MotionLevel.Low, 20)]
    [InlineData(MotionLevel.Medium, 60)]
    [InlineData(MotionLevel.High, 80)]
    public void SensitivityToDevice(MotionLevel level, int expected)
    {
        Assert.Equal(expected, MotionSensitivity.ToDevice(level, 0, 100, 20));
    }

    [Theory]
    [InlineData(0, 100, 20)]
    [InlineData(0, 100, 1)]
    [InlineData(1, 6, 1)]
    [InlineData(0, 5, 1)]
    public void SensitivityRoundTrips(int min, int max, int step)
    {
        foreach (var level in Enum.GetValues<MotionLevel>())
        {
            var value = MotionSensitivity.ToDevice(level, min, max, step);
            Assert.InRange(value, min, max);
            Assert.Equal(level, MotionSensitivity.FromDevice(value, min, max));
        }
    }

    [Fact]
    public void MotionPlanSkipsUnchangedValues()
    {
        var before = MotionSettings.Parse(MotionDoc(true, 40), MotionCaps)!; // 40 reads as Orta

        Assert.Null(RecorderPlan.Motion(1, "K", before, enabled: true, level: MotionLevel.Medium));
    }

    // ---------------------------------------------------------------- Record presets

    [Theory]
    [InlineData("CMR", RecordPreset.Continuous)]
    [InlineData("MOTION", RecordPreset.MotionOnly)]
    [InlineData("ALARM", RecordPreset.Custom)]
    public void DetectsWholeWeekPresets(string mode, RecordPreset expected)
    {
        Assert.Equal(expected, RecordSchedule.Detect(Track(Week(mode)).Root!));
    }

    [Fact]
    public void DetectsOffAndCustom()
    {
        Assert.Equal(RecordPreset.Off, RecordSchedule.Detect(Track("").Root!));
        Assert.Equal(RecordPreset.Off, RecordSchedule.Detect(Track(Action(1, "Monday", "Tuesday", record: false)).Root!));
        // Weekdays 08:00-18:00 only.
        var office = string.Concat(Enumerable.Range(0, 5).Select(i => Action(i + 1, Days[i], Days[i], "08:00:00", "18:00:00")));
        Assert.Equal(RecordPreset.Custom, RecordSchedule.Detect(Track(office).Root!));
        // Six days continuous, Sunday by motion.
        var mixed = string.Concat(Enumerable.Range(0, 7).Select(i => Action(i + 1, Days[i], Days[(i + 1) % 7], mode: i == 6 ? "MOTION" : "CMR")));
        Assert.Equal(RecordPreset.Custom, RecordSchedule.Detect(Track(mixed).Root!));
    }

    [Fact]
    public void DetectsFullDaysWrittenAs2400()
    {
        var week = string.Concat(Enumerable.Range(0, 7).Select(i => Action(i + 1, Days[i], Days[i], endTime: "24:00:00", mode: "MOTION")));

        Assert.Equal(RecordPreset.MotionOnly, RecordSchedule.Detect(Track(week).Root!));
    }

    [Fact]
    public void ApplyingAPresetKeepsTheRestOfTheTrack()
    {
        var office = string.Concat(Enumerable.Range(0, 5).Select(i => Action(i + 1, Days[i], Days[i], "08:00:00", "18:00:00")));
        var doc = Track(office);
        var reference = Track(Week("MOTION"));

        RecordSchedule.ApplyPreset(doc.Root!, RecordPreset.MotionOnly);

        Assert.Equal(RecordPreset.MotionOnly, RecordSchedule.Detect(doc.Root!));
        Assert.True(XNode.DeepEquals(reference, doc));
        Assert.All(doc.Root!.Descendants(), e => Assert.Equal(Ns, e.Name.NamespaceName));
    }

    [Fact]
    public void OffPresetEmptiesTheSchedule()
    {
        var doc = Track(Week("CMR"));

        RecordSchedule.ApplyPreset(doc.Root!, RecordPreset.Off);

        Assert.Equal(RecordPreset.Off, RecordSchedule.Detect(doc.Root!));
        Assert.Equal("00000000-0000-0000-0000-000000000002", IsapiXml.Get(doc.Root!, RecordSchedule.BlockPath + "/ScheduleBlockGUID"));
    }

    [Fact]
    public void CustomScheduleIsNotOverwrittenUnlessAPresetIsPicked()
    {
        var office = string.Concat(Enumerable.Range(0, 5).Select(i => Action(i + 1, Days[i], Days[i], "08:00:00", "18:00:00")));
        var before = RecordSettings.Parse(Track(office))!;
        Assert.Equal(RecordPreset.Custom, before.Preset);

        Assert.Null(RecorderPlan.Record(1, "K", before, enabled: true, preset: RecordPreset.Custom, preSeconds: 5, postSeconds: 5));
        Assert.Null(RecorderPlan.Record(1, "K", before, enabled: true, preset: null, preSeconds: 5, postSeconds: 5));

        var write = RecorderPlan.Record(1, "K", before, enabled: true, preset: RecordPreset.Continuous, preSeconds: 5, postSeconds: 10)!;
        Assert.Equal(["K · Kayıt modu: Özel program → Sürekli", "K · Olay sonrası kayıt: 5 sn → 10 sn"], write.Descriptions.ToList());
    }

    [Fact]
    public void RecordOptionsIncludeTheCurrentValue()
    {
        Assert.Equal([0, 5, 7, 10, 15, 20, 25, 30], RecordSettings.WithCurrent(RecordSettings.PreOptions, 7));
        Assert.Equal("2 dk", RecorderText.Seconds(120));
    }

    // ---------------------------------------------------------------- Writer

    [Fact]
    public async Task WriterReadsModifiesWritesAndVerifies()
    {
        var transport = new FakeTransport();
        transport.Docs[IsapiPaths.Motion(1)] = MotionDoc(true, 60);
        var before = MotionSettings.Parse(MotionDoc(true, 60), MotionCaps)!;
        var write = RecorderPlan.Motion(1, "K1", before, enabled: false, level: null)!;

        var report = await ConfigWriter.ApplyAsync(transport, [write]);

        Assert.True(report.Success);
        Assert.Equal(["K1 · Hareket algılama: Açık → Kapalı"], report.Applied);
        Assert.Equal([$"GET {IsapiPaths.Motion(1)}", $"PUT {IsapiPaths.Motion(1)}", $"GET {IsapiPaths.Motion(1)}"], transport.Calls);
        // The full document went back with only <enabled> changed.
        Assert.True(XNode.DeepEquals(MotionDoc(false, 60), transport.Sent[IsapiPaths.Motion(1)]));
    }

    [Fact]
    public async Task WriterStopsAtTheFirstFailureAndReportsTheRest()
    {
        var transport = new FakeTransport();
        foreach (var channel in new[] { 1, 2, 3 })
        {
            transport.Docs[IsapiPaths.Motion(channel)] = MotionDoc();
        }
        transport.PutStatus[IsapiPaths.Motion(2)] = new IsapiResponseStatus(6, "Invalid XML Content", "badParameters");
        var before = MotionSettings.Parse(MotionDoc(), MotionCaps)!;
        var writes = new[] { 1, 2, 3 }.Select(c => RecorderPlan.Motion(c, $"K{c}", before, false, null)!).ToList();

        var report = await ConfigWriter.ApplyAsync(transport, writes);

        Assert.False(report.Success);
        Assert.Equal(["K1 · Hareket algılama: Açık → Kapalı"], report.Applied);
        Assert.Equal("K2 · Hareket algılama: Açık → Kapalı", report.FailedChange);
        Assert.Equal("Cihaz değeri kabul etmedi: Geçersiz XML içeriği (badParameters: değer geçersiz)", report.FailureMessage);
        Assert.Equal(["K2 · Hareket algılama: Açık → Kapalı", "K3 · Hareket algılama: Açık → Kapalı"], report.NotApplied);
        Assert.DoesNotContain(transport.Calls, c => c.EndsWith(IsapiPaths.Motion(3), StringComparison.Ordinal));
        Assert.True(XNode.DeepEquals(MotionDoc(), transport.Docs[IsapiPaths.Motion(3)]));
    }

    [Fact]
    public async Task WriterReportsAValueTheDeviceDidNotKeep()
    {
        var transport = new FakeTransport();
        transport.Docs[IsapiPaths.Motion(1)] = MotionDoc(true, 60);
        transport.IgnorePut.Add(IsapiPaths.Motion(1));
        var before = MotionSettings.Parse(MotionDoc(true, 60), MotionCaps)!;

        var report = await ConfigWriter.ApplyAsync(transport, [RecorderPlan.Motion(1, "K1", before, true, MotionLevel.Low)!]);

        Assert.False(report.Success);
        Assert.Equal("Cihaz değeri kabul etmedi: Hassasiyet cihazda “60” olarak kaldı.", report.FailureMessage);
        Assert.Equal(["K1 · Hassasiyet: Orta (60) → Düşük (20)"], report.NotApplied);
    }

    [Fact]
    public async Task WriterReportsReadErrorsAsFailures()
    {
        var transport = new FakeTransport(); // nothing stored: GET answers 404
        var before = MotionSettings.Parse(MotionDoc(), MotionCaps)!;

        var report = await ConfigWriter.ApplyAsync(transport, [RecorderPlan.Motion(5, "K5", before, false, null)!]);

        Assert.False(report.Success);
        Assert.Empty(report.Applied);
        Assert.DoesNotContain(transport.Calls, c => c.StartsWith("PUT", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Streams, overlays, image

    static readonly XDocument MainStream = XDocument.Parse($"""
        <StreamingChannel version="2.0" xmlns="{Ns}">
          <id>101</id><channelName>101</channelName><enabled>true</enabled>
          <Video><enabled>true</enabled><videoCodecType>H.265</videoCodecType>
            <videoResolutionWidth>1920</videoResolutionWidth><videoResolutionHeight>1080</videoResolutionHeight>
            <videoQualityControlType>VBR</videoQualityControlType><vbrUpperCap>3072</vbrUpperCap>
            <maxFrameRate>1500</maxFrameRate><GovLength>50</GovLength></Video>
        </StreamingChannel>
        """);

    static readonly XDocument MainStreamCaps = XDocument.Parse($"""
        <StreamingChannel version="2.0" xmlns="{Ns}">
          <Video>
            <videoCodecType opt="H.264,H.265">H.265</videoCodecType>
            <videoResolutionWidth opt="1280,1920,2560">1920</videoResolutionWidth>
            <videoResolutionHeight opt="720,1080,1440">1080</videoResolutionHeight>
            <constantBitRate min="32" max="8192">0</constantBitRate>
            <vbrUpperCap min="32" max="4096">3072</vbrUpperCap>
            <maxFrameRate opt="0,2500,1500,1000,50">1500</maxFrameRate>
          </Video>
        </StreamingChannel>
        """);

    [Fact]
    public void ParsesStreamAndPairedOptions()
    {
        var stream = StreamSettings.Parse(MainStream)!;
        var options = StreamOptions.Parse(MainStreamCaps, stream);

        Assert.Equal(new StreamSettings("H.265", 1920, 1080, 1500, 3072, "Video/vbrUpperCap"), stream);
        Assert.Equal(["H.264", "H.265"], options.Codecs);
        Assert.Equal([(1280, 720), (1920, 1080), (2560, 1440)], options.Resolutions);
        Assert.Equal([0, 2500, 1500, 1000, 50], options.FrameRates);
        Assert.Equal([256, 512, 768, 1024, 1536, 2048, 3072, 4096], options.Bitrates);
    }

    [Fact]
    public void StreamOptionsWithoutCapabilitiesOfferTheCurrentValue()
    {
        var stream = StreamSettings.Parse(MainStream)!;
        var options = StreamOptions.Parse(null, stream);

        Assert.Equal(["H.265"], options.Codecs);
        Assert.Equal([(1920, 1080)], options.Resolutions);
        Assert.Equal([1500], options.FrameRates);
        Assert.Equal([3072], options.Bitrates);
    }

    [Theory]
    [InlineData(1500, "15 fps")]
    [InlineData(0, "Tam kare hızı")]
    [InlineData(50, "1/2 fps")]
    [InlineData(6, "1/16 fps")]
    public void FrameRateText(int value, string expected)
    {
        Assert.Equal(expected, StreamSettings.FrameRateText(value));
    }

    [Fact]
    public void CameraPlanGroupsEditsPerDocument()
    {
        var main = StreamSettings.Parse(MainStream)!;
        var overlay = new OverlaySettings(true, true);
        var color = new ImageColor(50, 50, 50, ColorRange.Default);
        var before = new CameraSettings("DIS KAPI", overlay, color, main, null);
        var after = before with
        {
            Name = "GIRIS",
            Overlay = overlay with { DateTime = false },
            Color = color with { Contrast = 60 },
            Main = main with { Width = 2560, Height = 1440, Codec = "H.264" },
        };

        var writes = RecorderPlan.Camera(1, "DIS KAPI", before, after);

        // The name change is not planned: the IP channel record is never written (camera password, see RecorderPlan.Camera).
        Assert.Equal([IsapiPaths.Overlays(1), IsapiPaths.Image(1), IsapiPaths.Stream(1, StreamQuality.Main)],
            writes.Select(w => w.Path).ToList());
        Assert.Equal(
        [
            "DIS KAPI · Ekranda tarih / saat: Açık → Kapalı",
            "DIS KAPI · Kontrast: 50 → 60",
            "DIS KAPI · Ana akış çözünürlüğü: 1920×1080 → 2560×1440",
            "DIS KAPI · Ana akış sıkıştırma: H.265 → H.264",
        ], writes.SelectMany(w => w.Descriptions).ToList());
        Assert.All(writes, w => Assert.Null(IsapiWritePolicy.Check(w.Path)));
    }

    [Fact]
    public async Task ResolutionEditSetsWidthAndHeightTogether()
    {
        var transport = new FakeTransport();
        var path = IsapiPaths.Stream(1, StreamQuality.Main);
        transport.Docs[path] = new XDocument(MainStream);
        var main = StreamSettings.Parse(MainStream)!;
        var write = RecorderPlan.Camera(1, "K", new CameraSettings(null, null, null, main, null),
            new CameraSettings(null, null, null, main with { Width = 1280, Height = 720 }, null)).Single();

        var report = await ConfigWriter.ApplyAsync(transport, [write]);

        Assert.True(report.Success);
        var sent = transport.Sent[path].Root!;
        Assert.Equal("1280", IsapiXml.Get(sent, StreamSettings.WidthPath));
        Assert.Equal("720", IsapiXml.Get(sent, StreamSettings.HeightPath));
        Assert.Equal("50", IsapiXml.Get(sent, "Video/GovLength"));
    }

    [Fact]
    public void ParsesOverlayAndImage()
    {
        var overlay = OverlaySettings.Parse(XDocument.Parse($"""
            <VideoOverlay xmlns="{Ns}">
              <DateTimeOverlay><enabled>true</enabled><positionX>0</positionX></DateTimeOverlay>
              <channelNameOverlay><enabled>false</enabled></channelNameOverlay>
            </VideoOverlay>
            """));
        var image = ImageColor.Parse(XDocument.Parse($"""
            <ImageChannel xmlns="{Ns}"><id>1</id><Color><brightnessLevel>50</brightnessLevel><contrastLevel>55</contrastLevel><saturationLevel>45</saturationLevel></Color></ImageChannel>
            """), XDocument.Parse($"""
            <ImageChannel xmlns="{Ns}"><Color><brightnessLevel min="0" max="100" def="50">50</brightnessLevel></Color></ImageChannel>
            """));

        Assert.Equal(new OverlaySettings(true, false), overlay);
        Assert.Equal(new ImageColor(50, 55, 45, new ColorRange(0, 100)), image);
        Assert.Null(ImageColor.Parse(XDocument.Parse($"<ImageChannel xmlns=\"{Ns}\"><id>1</id></ImageChannel>"), null));
    }

    // ---------------------------------------------------------------- Time, disks, overview

    [Theory]
    [InlineData("CST-3:00:00", 3, 0)]
    [InlineData("CST+5:00:00", -5, 0)]
    [InlineData("CST-5:30:00", 5, 30)]
    [InlineData("CST-1:00:00DST01:00:00,M3.5.0/02:00:00,M10.5.0/03:00:00", 1, 0)]
    public void ParsesHikvisionTimeZones(string zone, int hours, int minutes)
    {
        Assert.Equal(new TimeSpan(hours, minutes, 0), HikTimeZone.Offset(zone));
    }

    [Fact]
    public void FormatsHikvisionTimeZones()
    {
        Assert.Equal("CST-3:00:00", HikTimeZone.Format(TimeSpan.FromHours(3)));
        Assert.Equal("CST+5:30:00", HikTimeZone.Format(new TimeSpan(-5, -30, 0)));
        Assert.Equal("CST-0:00:00", HikTimeZone.Format(TimeSpan.Zero));
    }

    static XDocument TimeDoc(string mode = "NTP", string zone = "CST-3:00:00") => XDocument.Parse($"""
        <Time version="2.0" xmlns="{Ns}"><timeMode>{mode}</timeMode><localTime>2026-01-02T03:04:05+03:00</localTime><timeZone>{zone}</timeZone></Time>
        """);

    [Fact]
    public void TimeSyncSetsManualModeAndThisPcsTime()
    {
        var now = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));
        var doc = TimeDoc();
        var edit = new TimeSyncEdit(() => now, "x");

        edit.Apply(doc.Root!);

        Assert.Equal("manual", IsapiXml.Get(doc.Root!, "timeMode"));
        Assert.Equal("2026-05-06T07:08:09+03:00", IsapiXml.Get(doc.Root!, "localTime"));
        Assert.Equal("CST-3:00:00", IsapiXml.Get(doc.Root!, "timeZone"));
        Assert.Null(edit.Verify(doc.Root!));
    }

    [Fact]
    public void TimeSyncChangesTheZoneOnlyWhenItDiffers()
    {
        var doc = TimeDoc(zone: "CST-1:00:00DST01:00:00,M3.5.0/02:00:00,M10.5.0/03:00:00");
        var now = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));

        new TimeSyncEdit(() => now, "x").Apply(doc.Root!);

        Assert.Equal("CST-3:00:00", IsapiXml.Get(doc.Root!, "timeZone"));
    }

    [Fact]
    public void TimeSyncVerifyFailsWhenTheDeviceKeptItsTime()
    {
        var now = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.FromHours(3));

        Assert.Equal("02.01.2026 03:04:05 · NTP", new TimeSyncEdit(() => now, "x").Verify(TimeDoc().Root!));
    }

    [Fact]
    public void SystemPlan()
    {
        var time = DeviceTime.Parse(TimeDoc());
        var writes = RecorderPlan.System("NVR", "Mağaza NVR", time, syncTime: true, () => DateTimeOffset.Now);

        Assert.Equal([IsapiPaths.DeviceInfo, IsapiPaths.Time], writes.Select(w => w.Path).ToList());
        Assert.Equal(
            ["Kayıt cihazı · Cihaz adı: NVR → Mağaza NVR", "Kayıt cihazı · Tarih ve saat: 02.01.2026 03:04:05 · NTP → bilgisayar saati · elle"],
            writes.SelectMany(w => w.Descriptions).ToList());
        Assert.Empty(RecorderPlan.System("NVR", "NVR", time, syncTime: false, () => DateTimeOffset.Now));
    }

    [Fact]
    public void ParsesDisksAndSummary()
    {
        var disks = HddInfo.ParseList(XDocument.Parse($"""
            <hddList xmlns="{Ns}">
              <hdd><id>1</id><hddName>hdd1</hddName><hddType>SATA</hddType><status>ok</status><capacity>7630885</capacity><freeSpace>0</freeSpace></hdd>
              <hdd><id>2</id><hddName>hdd2</hddName><hddType>SATA</hddType><status>idle</status><capacity>7630885</capacity><freeSpace>1024</freeSpace></hdd>
            </hddList>
            """));

        Assert.Equal(2, disks.Count);
        Assert.Equal("Beklemede", disks[1].StatusText);
        var summary = DiskSummary.Of(disks);
        Assert.Equal("Normal", summary.Status);
        Assert.Equal("14,55 TB", RecorderText.Size(summary.CapacityMb));
        Assert.Equal("1,0 GB", RecorderText.Size(summary.FreeMb));
        Assert.Equal("hdd1: Hata", DiskSummary.Of([disks[0] with { Status = "error" }]).Status);
    }

    [Fact]
    public void ParsesOverviewParts()
    {
        Assert.Equal(32, ChannelUsage.ParseTotal(XDocument.Parse($"""
            <InputProxyChannelListCap xmlns="{Ns}"><InputProxyChannel><id min="1" max="32"></id></InputProxyChannel></InputProxyChannelListCap>
            """)));
        Assert.Equal("22 / 32 kullanımda", new ChannelUsage(22, 32).Text);

        var security = SecurityStatus.Parse(XDocument.Parse($"""
            <EZVIZ xmlns="{Ns}"><enabled>true</enabled><verificationCode>ABCDEF</verificationCode><streamEncrypteEnabled>true</streamEncrypteEnabled></EZVIZ>
            """));
        Assert.Equal(new SecurityStatus(true, true), security);

        var ntp = NtpInfo.Parse(XDocument.Parse($"""
            <NTPServerList xmlns="{Ns}"><NTPServer><id>1</id><addressingFormatType>hostname</addressingFormatType><hostName>ntp.example.org</hostName><portNo>123</portNo><synchronizeInterval>60</synchronizeInterval></NTPServer></NTPServerList>
            """));
        Assert.Equal("Açık · ntp.example.org · 60 dk'da bir", ntp.Describe(DeviceTime.Parse(TimeDoc())));
        Assert.Equal("Kapalı · saat elle ayarlı", ntp.Describe(DeviceTime.Parse(TimeDoc(mode: "manual"))));
    }

    [Fact]
    public async Task ReaderSeparatesUnsupportedFromUnreadable()
    {
        var transport = new FakeTransport();

        var missing = await RecorderReader.TryAsync(transport, IsapiPaths.Image(1), d => ImageColor.Parse(d, null));

        Assert.Equal(ReadState.Unsupported, missing.State); // 404 from the fake
        Assert.Equal("okunamadı", RecorderText.Unreadable);
        _ = CultureInfo.InvariantCulture;
    }
}
