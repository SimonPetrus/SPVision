using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Cihaz ayarları › Kameralar: OSD date style, time style and day of week. The documents are the DS-8632NI-I8's answers
/// to read-only GETs (21.10.2026; all 22 cameras: DD-MM-YYYY, 24hour, displayWeek true). Nothing in them is secret.
/// </summary>
public class OverlayStyleTests
{
    const string Channel1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VideoOverlay version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
          <normalizedScreenSize>
            <normalizedScreenWidth>704</normalizedScreenWidth>
            <normalizedScreenHeight>576</normalizedScreenHeight>
          </normalizedScreenSize>
          <attribute>
            <transparent>false</transparent>
            <flashing>false</flashing>
          </attribute>
          <TextOverlayList size="4"></TextOverlayList>
          <DateTimeOverlay>
            <enabled>true</enabled>
            <positionX>0</positionX>
            <positionY>544</positionY>
            <dateStyle>DD-MM-YYYY</dateStyle>
            <timeStyle>24hour</timeStyle>
            <displayWeek>true</displayWeek>
          </DateTimeOverlay>
          <channelNameOverlay>
            <enabled>true</enabled>
            <positionX>512</positionX>
            <positionY>64</positionY>
          </channelNameOverlay>
        </VideoOverlay>
        """;

    const string Capabilities = """
        <?xml version="1.0" encoding="UTF-8"?>
        <VideoOverlay version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
          <normalizedScreenSize>
            <normalizedScreenWidth>704</normalizedScreenWidth>
            <normalizedScreenHeight>576</normalizedScreenHeight>
          </normalizedScreenSize>
          <attribute>
            <transparent opt="true,false">false</transparent>
            <flashing opt="true,false">false</flashing>
          </attribute>
          <TextOverlayList size="4"></TextOverlayList>
          <DateTimeOverlay>
            <enabled opt="true,false">true</enabled>
            <positionX>0</positionX>
            <positionY>544</positionY>
            <dateStyle opt="YYYY-MM-DD,MM-DD-YYYY,DD-MM-YYYY,CHR-YYYY/MM/DD,CHR-MM/DD/YYYY,CHR-DD/MM/YYYY">DD-MM-YYYY</dateStyle>
            <timeStyle opt="24hour,12hour">24hour</timeStyle>
            <displayWeek opt="true,false">true</displayWeek>
          </DateTimeOverlay>
          <channelNameOverlay>
            <enabled opt="true,false">true</enabled>
            <positionX>512</positionX>
            <positionY>64</positionY>
          </channelNameOverlay>
        </VideoOverlay>
        """;

    static XDocument Doc(string xml) => IsapiXml.Parse(xml);

    static readonly DateTime Sample = new(2026, 10, 21);

    [Fact]
    public void ParsesTheRecordersDocument()
    {
        var overlay = OverlaySettings.Parse(Doc(Channel1));
        Assert.Equal(new OverlaySettings(true, true, "DD-MM-YYYY", "24hour", true), overlay);
    }

    [Fact]
    public void DocumentWithoutStylesStillParses()
    {
        var overlay = OverlaySettings.Parse(Doc("<VideoOverlay><DateTimeOverlay><enabled>false</enabled></DateTimeOverlay></VideoOverlay>"));
        Assert.Equal(new OverlaySettings(false, null), overlay);
        Assert.Null(overlay!.DateStyle);
        Assert.Null(overlay.TimeStyle);
        Assert.Null(overlay.DisplayWeek);
    }

    [Fact]
    public void OptionsComeFromTheCapabilitiesInDeviceOrder()
    {
        var options = OverlayOptions.Parse(Doc(Capabilities), OverlaySettings.Parse(Doc(Channel1))!);
        Assert.Equal(["YYYY-MM-DD", "MM-DD-YYYY", "DD-MM-YYYY", "CHR-YYYY/MM/DD", "CHR-MM/DD/YYYY", "CHR-DD/MM/YYYY"], options.DateStyles);
        Assert.Equal(["24hour", "12hour"], options.TimeStyles);
    }

    [Fact]
    public void WithoutCapabilitiesOnlyTheCurrentValueIsOffered()
    {
        var options = OverlayOptions.Parse(null, new OverlaySettings(true, true, "MM-DD-YYYY", "12hour", false));
        Assert.Equal(["MM-DD-YYYY"], options.DateStyles);
        Assert.Equal(["12hour"], options.TimeStyles);

        // A current value missing from the opt list is added (the form must be able to show it).
        var odd = OverlayOptions.Parse(Doc(Capabilities), new OverlaySettings(true, true, "DD.MM.YYYY", "24hour", true));
        Assert.Equal("DD.MM.YYYY", odd.DateStyles[^1]);
        Assert.Equal(7, odd.DateStyles.Count);
    }

    [Theory]
    [InlineData("DD-MM-YYYY", "21-10-2026 (gün-ay-yıl)")]
    [InlineData("MM-DD-YYYY", "10-21-2026 (ay-gün-yıl)")]
    [InlineData("YYYY-MM-DD", "2026-10-21 (yıl-ay-gün)")]
    [InlineData("CHR-YYYY/MM/DD", "Çince karakterli (yıl/ay/gün)")]
    [InlineData("CHR-MM/DD/YYYY", "Çince karakterli (ay/gün/yıl)")]
    [InlineData("CHR-DD/MM/YYYY", "Çince karakterli (gün/ay/yıl)")]
    [InlineData("YYYY/MM/DD", "2026/10/21 (yıl-ay-gün)")]
    [InlineData("WEIRD", "WEIRD")]
    [InlineData("YYYY-MM-DD-DD", "YYYY-MM-DD-DD")]
    public void DateStyleLabels(string style, string text) => Assert.Equal(text, OverlayText.DateStyle(style, Sample));

    [Fact]
    public void ShortAndTimeTexts()
    {
        Assert.Equal("gün-ay-yıl (DD-MM-YYYY)", OverlayText.DateStyleShort("DD-MM-YYYY"));
        Assert.Equal("Çince, yıl/ay/gün (CHR-YYYY/MM/DD)", OverlayText.DateStyleShort("CHR-YYYY/MM/DD"));
        Assert.Equal("WEIRD", OverlayText.DateStyleShort("WEIRD"));
        Assert.Equal("24 saat", OverlayText.TimeStyle("24hour"));
        Assert.Equal("12 saat (ÖÖ/ÖS)", OverlayText.TimeStyle("12hour"));
        Assert.Equal("weird", OverlayText.TimeStyle("weird"));
    }

    static CameraSettings Camera(OverlaySettings overlay) => new("Kamera 1", overlay, null, null, null);

    [Fact]
    public void PlanListsOldAndNew()
    {
        var before = OverlaySettings.Parse(Doc(Channel1))!;
        var after = before with { DateStyle = "YYYY-MM-DD", TimeStyle = "12hour", DisplayWeek = false };

        var write = Assert.Single(RecorderPlan.Camera(1, "Kamera 1", Camera(before), Camera(after)));

        Assert.Equal(IsapiPaths.Overlays(1), write.Path);
        Assert.Null(IsapiWritePolicy.Check(write.Path)); // already on the ordinary allowlist
        Assert.Equal([
            "Kamera 1 · Tarih biçimi: gün-ay-yıl (DD-MM-YYYY) → yıl-ay-gün (YYYY-MM-DD)",
            "Kamera 1 · Saat biçimi: 24 saat → 12 saat (ÖÖ/ÖS)",
            "Kamera 1 · Haftanın günü: Açık → Kapalı",
        ], write.Descriptions);
    }

    [Fact]
    public void NothingChangedNothingWritten()
    {
        var overlay = OverlaySettings.Parse(Doc(Channel1))!;
        Assert.Empty(RecorderPlan.Camera(1, "Kamera 1", Camera(overlay), Camera(overlay with { })));
    }

    [Fact]
    public void UnsupportedStyleIsNeverPlanned()
    {
        var before = new OverlaySettings(true, true);
        var after = before with { DateStyle = "YYYY-MM-DD", TimeStyle = "12hour", DisplayWeek = true };
        Assert.Empty(RecorderPlan.Camera(1, "Kamera 1", Camera(before), Camera(after)));
    }

    [Fact]
    public async Task ReadModifyWriteChangesOnlyTheThreeElements()
    {
        var path = IsapiPaths.Overlays(1);
        var original = Doc(Channel1);
        var device = new FakeTransport();
        device.Docs[path] = new XDocument(original);
        var before = OverlaySettings.Parse(original)!;
        var after = before with { DateStyle = "YYYY-MM-DD", TimeStyle = "12hour", DisplayWeek = false };

        var report = await ConfigWriter.ApplyAsync(device, RecorderPlan.Camera(1, "Kamera 1", Camera(before), Camera(after)));

        Assert.True(report.Success, report.FailureMessage);
        Assert.Equal(["GET " + path, "PUT " + path, "GET " + path], device.Calls);
        var sent = device.Sent[path];
        IsapiWritePolicy.EnsureBodyAllowed(sent); // the body check lets these elements through
        Assert.Equal(original.Root!.Name, sent.Root!.Name); // namespace kept
        Assert.Equal("YYYY-MM-DD", IsapiXml.Get(sent.Root, OverlaySettings.DateStylePath));
        Assert.Equal("12hour", IsapiXml.Get(sent.Root, OverlaySettings.TimeStylePath));
        Assert.Equal("false", IsapiXml.Get(sent.Root, OverlaySettings.DisplayWeekPath));

        // Everything else exactly as read: put the three values back and the documents are identical.
        var restored = new XDocument(sent);
        IsapiXml.Set(restored.Root!, OverlaySettings.DateStylePath, "DD-MM-YYYY");
        IsapiXml.Set(restored.Root!, OverlaySettings.TimeStylePath, "24hour");
        IsapiXml.Set(restored.Root!, OverlaySettings.DisplayWeekPath, "true");
        Assert.True(XNode.DeepEquals(original.Root, restored.Root));

        // And back again (what the owner does by hand after the first try): the device ends where it started.
        var back = await ConfigWriter.ApplyAsync(device, RecorderPlan.Camera(1, "Kamera 1", Camera(after), Camera(before)));
        Assert.True(back.Success, back.FailureMessage);
        Assert.True(XNode.DeepEquals(original.Root, device.Docs[path].Root));
    }

    [Fact]
    public async Task DeviceIgnoringTheStyleIsReported()
    {
        var path = IsapiPaths.Overlays(2);
        var device = new FakeTransport();
        device.Docs[path] = Doc(Channel1);
        device.IgnorePut.Add(path);
        var before = OverlaySettings.Parse(Doc(Channel1))!;

        var report = await ConfigWriter.ApplyAsync(device,
            RecorderPlan.Camera(2, "Kamera 2", Camera(before), Camera(before with { DateStyle = "MM-DD-YYYY" })));

        Assert.False(report.Success);
        Assert.Contains("Tarih biçimi", report.FailureMessage);
        Assert.Contains("DD-MM-YYYY", report.FailureMessage);
    }
}
