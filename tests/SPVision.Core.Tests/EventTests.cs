using System.Net;
using System.Text;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Event notifications: the alert stream parser (multipart framing as the DS-8632NI-I8 sends it, written here with
/// documentation addresses), event type mapping, the event center (merging, pop-up throttle, history, filters), the live
/// tile and disk transition watchers, the reconnect back-off and the read-only GET of the stream over loopback.
/// </summary>
public class EventTests
{
    const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    /// <summary>One alert document; empty channel elements like the recorder's heartbeat when <paramref name="channel"/> is null.</summary>
    static string Alert(string type, string state = "active", int? channel = null, string time = "2026-10-22T10:19:15+03:00",
        int? dynChannel = null, string? channelName = null) =>
        $"<EventNotificationAlert version=\"2.0\" xmlns=\"{Ns}\">\r\n<ipAddress>192.0.2.10</ipAddress>\r\n<portNo>443</portNo>\r\n" +
        "<protocolType>HTTP</protocolType>\r\n<macAddress>00:00:5e:00:53:01</macAddress>\r\n" +
        (dynChannel is null ? "<dynChannelID/>\r\n" : $"<dynChannelID>{dynChannel}</dynChannelID>\r\n") +
        (channel is null ? "<channelID/>\r\n" : $"<channelID>{channel}</channelID>\r\n") +
        $"<dateTime>{time}</dateTime>\r\n<activePostCount>0</activePostCount>\r\n<eventType>{type}</eventType>\r\n" +
        $"<eventState>{state}</eventState>\r\n<eventDescription>{type} alarm</eventDescription>\r\n" +
        (channelName is null ? "<channelName/>\r\n" : $"<channelName>{channelName}</channelName>\r\n") +
        "</EventNotificationAlert>";

    /// <summary>A part as the recorder frames it: delimiter, Content-Type, Content-Length, blank line, body, CRLF.</summary>
    static string Part(string body, string contentType = "application/xml; charset=\"UTF-8\"", bool length = true) =>
        $"--boundary\r\nContent-Type: {contentType}\r\n" +
        (length ? $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" : "") + $"\r\n{body}\r\n";

    static IReadOnlyList<AlertEvent> FeedAll(AlertStreamParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    static List<AlertEvent> FeedByteByByte(AlertStreamParser parser, byte[] bytes)
    {
        var found = new List<AlertEvent>();
        foreach (var b in bytes)
        {
            found.AddRange(parser.Feed([b]));
        }
        return found;
    }

    // ---- parser ----

    [Fact]
    public void HeartbeatPartIsReadWithoutWaitingForTheNextBoundary()
    {
        // The recorder's heartbeat: videoloss / inactive without a channel, one part every ~10 s. It must come out as soon as
        // its Content-Length bytes are in (the next boundary arrives 10 s later).
        var parser = new AlertStreamParser("boundary");

        var alert = Assert.Single(FeedAll(parser, Part(Alert("videoloss", "inactive"))));

        Assert.Equal("videoloss", alert.EventType);
        Assert.False(alert.IsActive);
        Assert.Null(alert.Channel);
        Assert.Null(alert.DynChannel);
        Assert.Equal(new DateTime(2026, 10, 22, 10, 19, 15), alert.Time); // the device's wall clock, offset dropped
        Assert.Equal("videoloss alarm", alert.Description);
        Assert.Equal(0, parser.Skipped);
    }

    [Fact]
    public void PartsFedOneByteAtATimeGiveTheSameAlerts()
    {
        var text = Part(Alert("videoloss", "inactive")) + Part(Alert("VMD", channel: 3)) + Part(Alert("linedetection", channel: 5));
        var parser = new AlertStreamParser("boundary");

        var alerts = FeedByteByByte(parser, Encoding.UTF8.GetBytes(text));

        Assert.Equal(["videoloss", "VMD", "linedetection"], alerts.Select(a => a.EventType));
        Assert.Equal([null, 3, 5], alerts.Select(a => a.Channel));
        Assert.True(alerts[1].IsActive);
        Assert.Equal(3, parser.Alerts);
        Assert.Equal(0, parser.Skipped);
    }

    [Fact]
    public void PartWithoutLengthEndsAtItsClosingTag()
    {
        var parser = new AlertStreamParser("boundary");

        var alert = Assert.Single(FeedAll(parser, Part(Alert("VMD", channel: 2), length: false)));

        Assert.Equal(2, alert.Channel);
    }

    [Fact]
    public void PicturePartIsSkippedByItsLengthWithoutBeingBuffered()
    {
        var parser = new AlertStreamParser("boundary");
        var picture = new byte[300_000];
        Random.Shared.NextBytes(picture);
        var head = Encoding.ASCII.GetBytes($"--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: {picture.Length}\r\n\r\n");

        Assert.Empty(parser.Feed(head));
        for (var i = 0; i < picture.Length; i += 4096)
        {
            Assert.Empty(parser.Feed(picture.AsSpan(i, Math.Min(4096, picture.Length - i))));
            Assert.True(parser.Buffered < 8192); // skipped by count, never held
        }
        var alert = Assert.Single(FeedAll(parser, "\r\n" + Part(Alert("linedetection", channel: 4))));

        Assert.Equal("linedetection", alert.EventType);
        Assert.Equal(1, parser.Skipped);
    }

    [Fact]
    public void PictureWithoutLengthIsDroppedUpToTheNextBoundaryWithinTheCap()
    {
        var parser = new AlertStreamParser("boundary");
        Assert.Empty(FeedAll(parser, "--boundary\r\nContent-Type: image/jpeg\r\n\r\n"));
        var filler = Encoding.ASCII.GetBytes(new string('x', 10_000));
        for (var i = 0; i < 50; i++)
        {
            Assert.Empty(parser.Feed(filler));
            Assert.True(parser.Buffered <= AlertStreamParser.MaxPartLength);
        }

        var alert = Assert.Single(FeedAll(parser, "\r\n" + Part(Alert("VMD", channel: 1))));

        Assert.Equal("VMD", alert.EventType);
        Assert.Equal(1, parser.Skipped);
    }

    [Fact]
    public void OversizedXmlPartIsSkipped()
    {
        var parser = new AlertStreamParser("boundary");
        var huge = Alert("VMD", channel: 1).Replace("<channelName/>", "<channelName>" + new string('a', 100_000) + "</channelName>");

        var alerts = FeedAll(parser, Part(huge) + Part(Alert("videoloss", channel: 7)));

        Assert.Equal(7, Assert.Single(alerts).Channel);
        Assert.Equal(1, parser.Skipped);
        Assert.True(parser.Buffered <= AlertStreamParser.MaxPartLength);
    }

    [Fact]
    public void BrokenPartsAreCountedNotThrown()
    {
        var parser = new AlertStreamParser("boundary");
        var dtd = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><EventNotificationAlert><eventType>&e;</eventType></EventNotificationAlert>";

        var alerts = FeedAll(parser,
            Part("<EventNotificationAlert><eventType>VMD") +       // cut short
            Part("not xml at all") +
            Part(dtd) +                                             // DTDs are refused (IsapiXml)
            Part("<ResponseStatus><statusCode>1</statusCode></ResponseStatus>") + // not an alert
            Part("<EventNotificationAlert><eventState>active</eventState></EventNotificationAlert>") + // no type
            Part(Alert("VMD", channel: 9)));

        Assert.Equal(9, Assert.Single(alerts).Channel);
        Assert.Equal(5, parser.Skipped);
    }

    [Fact]
    public void ClosingDelimiterPreambleAndLfOnlyLinesAreHandled()
    {
        var parser = new AlertStreamParser("\"--boundary\""); // quoted, with the dashes: same delimiter
        var lfPart = "--boundary\nContent-Type: application/xml\n\n" + Alert("VMD", channel: 2).Replace("\r\n", "\n") + "\n";

        var alerts = FeedAll(parser, "preamble text\r\n" + Part(Alert("VMD", channel: 1)) + lfPart + "--boundary--\r\n");

        Assert.Equal([1, 2], alerts.Select(a => a.Channel));
        Assert.Equal(0, parser.Skipped);
    }

    [Fact]
    public void PartWithoutHeadersIsRead()
    {
        var parser = new AlertStreamParser("boundary");

        var alert = Assert.Single(FeedAll(parser, "--boundary\r\n" + Alert("fielddetection", channel: 6) + "\r\n"));

        Assert.Equal("fielddetection", alert.EventType);
    }

    [Fact]
    public void WithoutBoundaryDocumentsAreReadBackToBack()
    {
        var parser = new AlertStreamParser(null);
        var bytes = Encoding.UTF8.GetBytes(Alert("VMD", channel: 1) + "\r\n" + Alert("shelteralarm", channel: 2));

        var alerts = FeedByteByByte(parser, bytes);

        Assert.Equal(["VMD", "shelteralarm"], alerts.Select(a => a.EventType));
    }

    [Fact]
    public void WithoutBoundaryAnEndlessDocumentStaysWithinTheCap()
    {
        var parser = new AlertStreamParser(null);
        var filler = Encoding.ASCII.GetBytes("<EventNotificationAlert>" + new string('z', 30_000));
        for (var i = 0; i < 10; i++)
        {
            parser.Feed(filler);
            Assert.True(parser.Buffered <= AlertStreamParser.MaxPartLength + filler.Length);
        }

        var alert = Assert.Single(parser.Feed(Encoding.UTF8.GetBytes("</EventNotificationAlert>" + Alert("VMD", channel: 3))));

        Assert.Equal(3, alert.Channel);
        Assert.Equal(1, parser.Skipped);
    }

    // ---- mapping ----

    [Theory]
    [InlineData("VMD", AlertKind.Motion)]
    [InlineData("vmd", AlertKind.Motion)]
    [InlineData("linedetection", AlertKind.Smart)]
    [InlineData("fielddetection", AlertKind.Smart)]
    [InlineData("regionEntrance", AlertKind.Smart)]
    [InlineData("regionExiting", AlertKind.Smart)]
    [InlineData("shelteralarm", AlertKind.Tamper)]
    [InlineData("tamperdetection", AlertKind.Tamper)]
    [InlineData("videoloss", AlertKind.CameraOffline)]
    [InlineData("IPCDisconnect", AlertKind.CameraOffline)]
    [InlineData("diskfull", AlertKind.Disk)]
    [InlineData("diskerror", AlertKind.Disk)]
    [InlineData("hddError", AlertKind.Disk)]
    public void EventTypesMapToKinds(string type, AlertKind kind) => Assert.Equal(kind, AlertMapping.KindOf(type));

    [Theory]
    [InlineData("IO")]
    [InlineData("nicbroken")]
    [InlineData("ipconflict")]
    [InlineData("illaccess")]
    [InlineData("facedetection")]
    [InlineData("")]
    public void OtherEventTypesAreIgnored(string type) => Assert.Null(AlertMapping.KindOf(type));

    [Theory]
    [InlineData("VMD", AlertKind.Motion, "Hareket algılandı")]
    [InlineData("linedetection", AlertKind.Smart, "Çizgi geçildi")]
    [InlineData("fielddetection", AlertKind.Smart, "Alan ihlali")]
    [InlineData("regionEntrance", AlertKind.Smart, "Alana girildi")]
    [InlineData("regionExiting", AlertKind.Smart, "Alandan çıkıldı")]
    [InlineData("shelteralarm", AlertKind.Tamper, "Görüntü kapatıldı")]
    [InlineData("videoloss", AlertKind.CameraOffline, "Kamera bağlantısı koptu")]
    [InlineData("diskfull", AlertKind.Disk, "Disk sorunu: disk dolu")]
    [InlineData("diskerror", AlertKind.Disk, "Disk sorunu: disk hatası")]
    public void TitlesAreTurkish(string type, AlertKind kind, string title) => Assert.Equal(title, AlertMapping.Title(type, kind));

    static Device Recorder() => new()
    {
        Name = "DS-8632NI-I8",
        Cameras = [new Camera { Channel = 1, Name = "KARSILAMA" }, new Camera { Channel = 2, Name = "DIS KAPI" }],
    };

    static AlertEvent Parsed(string xml) => AlertEvent.Parse(IsapiXml.Parse(xml))!;

    [Fact]
    public void ChannelIsNamedAfterTheDevicesCamera()
    {
        var device = Recorder();
        var now = new DateTime(2026, 10, 22, 10, 52, 0);

        var (candidate, known) = AlertMapping.Candidate(device, Parsed(Alert("VMD", channel: 2, time: "2026-10-22T10:52:14+03:00")), now)!.Value;

        Assert.True(known);
        Assert.Equal(AlertKind.Motion, candidate.Kind);
        Assert.Equal(2, candidate.Channel);
        Assert.Equal("DIS KAPI", candidate.CameraName);
        Assert.Equal(device.Id, candidate.DeviceId);
        Assert.Equal(new DateTime(2026, 10, 22, 10, 52, 14), candidate.Time); // the recorder's time (what playback searches)
        Assert.Equal(EventSource.Recorder, candidate.Source);
    }

    [Fact]
    public void DynamicChannelIsUsedWhenTheChannelIsNotACamera()
    {
        var (candidate, known) = AlertMapping.Candidate(Recorder(), Parsed(Alert("VMD", channel: 33, dynChannel: 1)), DateTime.Now)!.Value;

        Assert.True(known);
        Assert.Equal(1, candidate.Channel);
        Assert.Equal("KARSILAMA", candidate.CameraName);
    }

    [Fact]
    public void UnknownChannelKeepsItsNumberWithoutButtons()
    {
        var (candidate, known) = AlertMapping.Candidate(Recorder(), Parsed(Alert("videoloss", channel: 40)), DateTime.Now)!.Value;
        var (named, _) = AlertMapping.Candidate(Recorder(), Parsed(Alert("videoloss", channel: 41, channelName: "DEPO")), DateTime.Now)!.Value;

        Assert.False(known);
        Assert.Equal("Kanal 40", candidate.CameraName);
        Assert.Equal("DEPO", named.CameraName);
    }

    [Fact]
    public void HeartbeatsAndCameraEventsWithoutChannelGiveNothing()
    {
        Assert.Null(AlertMapping.Candidate(Recorder(), Parsed(Alert("videoloss", "inactive")), DateTime.Now));
        Assert.Null(AlertMapping.Candidate(Recorder(), Parsed(Alert("VMD", "inactive", channel: 1)), DateTime.Now));
        Assert.Null(AlertMapping.Candidate(Recorder(), Parsed(Alert("videoloss")), DateTime.Now)); // active but no channel
        Assert.Null(AlertMapping.Candidate(Recorder(), Parsed(Alert("IO", channel: 1)), DateTime.Now));
    }

    [Fact]
    public void DiskEventsBelongToTheRecorder()
    {
        var now = new DateTime(2026, 10, 22, 9, 12, 0);
        var xml = Alert("diskerror", channel: 1).Replace("<dateTime>2026-10-22T10:19:15+03:00</dateTime>", "<dateTime></dateTime>");

        var (candidate, known) = AlertMapping.Candidate(Recorder(), Parsed(xml), now)!.Value;
        var center = new EventCenter(new EventNotificationSettings());
        center.Add(candidate, now, known);
        var row = center.Events[0];

        Assert.Equal(AlertKind.Disk, candidate.Kind);
        Assert.Null(candidate.Channel);
        Assert.Equal(now, candidate.Time); // no time in the alert: received time
        Assert.Null(row.Camera);
        Assert.Equal("Kayıt cihazı · DS-8632NI-I8", row.Place);
    }

    // ---- event center ----

    static EventCandidate Motion(int channel, DateTime time, Guid? device = null) =>
        new(AlertKind.Motion, device ?? DeviceA, "NVR", channel, $"KAMERA {channel}", "Hareket algılandı", time, EventSource.Recorder);

    static EventCandidate Offline(int channel, DateTime time, EventSource source) =>
        new(AlertKind.CameraOffline, DeviceA, "NVR", channel, $"KAMERA {channel}", EventText.Offline, time, source);

    static readonly Guid DeviceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly DateTime T0 = new(2026, 10, 22, 10, 0, 0);

    static EventCenter AllOn(int interval = 60) => new(new EventNotificationSettings
    {
        Motion = true,
        Smart = true,
        Tamper = true,
        IntervalSeconds = interval,
    });

    [Fact]
    public void DefaultsCollectLostCamerasAndDisksOnly()
    {
        var settings = new EventNotificationSettings();
        var center = new EventCenter(settings);

        Assert.True(settings.CameraOffline && settings.Disk && settings.Popup);
        Assert.False(settings.Motion || settings.Smart || settings.Tamper || settings.Sound);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.Interval);
        Assert.Equal(EventOutcome.Ignored, center.Add(Motion(1, T0), T0));
        Assert.Empty(center.Events);
        Assert.Equal(EventOutcome.Notify, center.Add(Offline(1, T0, EventSource.Recorder), T0));
    }

    [Fact]
    public void IntervalOffTheListReadsAsOneMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), new EventNotificationSettings { IntervalSeconds = 30 }.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5), new EventNotificationSettings { IntervalSeconds = 300 }.Interval);
        Assert.Equal(TimeSpan.FromMinutes(1), new EventNotificationSettings { IntervalSeconds = 1 }.Interval);
        Assert.Equal(TimeSpan.FromMinutes(1), new EventNotificationSettings { IntervalSeconds = -5 }.Interval);
    }

    [Fact]
    public void RepeatsOfAnOngoingEventExtendOneRow()
    {
        var center = AllOn();

        Assert.Equal(EventOutcome.Notify, center.Add(Motion(1, T0), T0));
        for (var s = 1; s <= 30; s++)
        {
            Assert.Equal(EventOutcome.Merged, center.Add(Motion(1, T0.AddSeconds(s)), T0.AddSeconds(s))); // the recorder repeats it every second
        }

        Assert.Single(center.Events);
        Assert.Equal(T0.AddSeconds(30), center.Events[0].LastSeen);
        Assert.Equal(T0, center.Events[0].Time);
    }

    [Fact]
    public void NewOccurrenceWithinTheIntervalIsListedWithoutPopup()
    {
        var center = AllOn(interval: 60);

        Assert.Equal(EventOutcome.Notify, center.Add(Motion(1, T0), T0));
        Assert.Equal(EventOutcome.Added, center.Add(Motion(1, T0.AddSeconds(20)), T0.AddSeconds(20)));   // a new burst
        Assert.Equal(EventOutcome.Notify, center.Add(Motion(2, T0.AddSeconds(21)), T0.AddSeconds(21)));  // another camera
        Assert.Equal(EventOutcome.Notify, center.Add(Offline(1, T0.AddSeconds(22), EventSource.Recorder), T0.AddSeconds(22))); // another kind
        Assert.Equal(EventOutcome.Notify, center.Add(Motion(1, T0.AddSeconds(61)), T0.AddSeconds(61)));  // interval over

        Assert.Equal(5, center.Events.Count);
        Assert.Equal(5, center.Unread);
    }

    [Theory]
    [InlineData(30, 31, EventOutcome.Notify)]
    [InlineData(30, 29, EventOutcome.Added)]
    [InlineData(300, 299, EventOutcome.Added)]
    [InlineData(300, 300, EventOutcome.Notify)]
    public void ChosenIntervalThrottlesPopups(int interval, int after, EventOutcome expected)
    {
        var center = AllOn(interval);
        center.Add(Motion(1, T0), T0);

        Assert.Equal(expected, center.Add(Motion(1, T0.AddSeconds(after)), T0.AddSeconds(after)));
    }

    [Fact]
    public void LostCameraFromRecorderAndTileIsOneEvent()
    {
        var center = AllOn();

        Assert.Equal(EventOutcome.Notify, center.Add(Offline(3, T0, EventSource.LiveTile), T0));
        Assert.Equal(EventOutcome.Merged, center.Add(Offline(3, T0.AddSeconds(4), EventSource.Recorder), T0.AddSeconds(4)));
        Assert.Equal(EventOutcome.Merged, center.Add(Offline(3, T0.AddSeconds(70), EventSource.Recorder), T0.AddSeconds(70))); // still lost
        Assert.Single(center.Events);
        Assert.Equal(EventSource.LiveTile, center.Events[0].Source);

        // Back and lost again later: a new event.
        Assert.Equal(EventOutcome.Notify, center.Add(Offline(3, T0.AddMinutes(5), EventSource.LiveTile), T0.AddMinutes(5)));
        Assert.Equal(2, center.Events.Count);
    }

    [Fact]
    public void DiskProblemFromRecorderAndDiskCheckIsOneEvent()
    {
        var center = AllOn();
        var check = new EventCandidate(AlertKind.Disk, DeviceA, "NVR", null, null, "Disk sorunu: HDD 2 (hata)", T0, EventSource.DiskCheck);
        var recorder = check with { Title = "Disk sorunu: disk hatası", Source = EventSource.Recorder };

        Assert.Equal(EventOutcome.Notify, center.Add(check, T0));
        Assert.Equal(EventOutcome.Merged, center.Add(recorder, T0.AddMinutes(3)));
        Assert.Single(center.Events);
    }

    [Fact]
    public void HistoryKeepsTheNewest200()
    {
        var center = AllOn();
        for (var i = 0; i < 250; i++)
        {
            center.Add(Motion(1 + i % 20, T0.AddMinutes(i)), T0.AddMinutes(i));
        }

        Assert.Equal(EventCenter.Capacity, center.Events.Count);
        Assert.Equal(T0.AddMinutes(249), center.Events[0].Time);  // newest first
        Assert.Equal(T0.AddMinutes(50), center.Events[^1].Time);
        Assert.Equal(200, center.Unread);
        Assert.Equal(250, center.Events.Max(e => e.Id));
    }

    [Fact]
    public void MarkAllReadClearsTheBadge()
    {
        var center = AllOn();
        center.Add(Motion(1, T0), T0);
        center.Add(Offline(2, T0, EventSource.LiveTile), T0);

        Assert.Equal(2, center.Unread);
        Assert.Equal("2", EventText.Badge(center.Unread));
        center.MarkAllRead();

        Assert.Equal(0, center.Unread);
        Assert.Equal("", EventText.Badge(0));
        Assert.Equal("99+", EventText.Badge(150));
        Assert.Equal("99", EventText.Badge(99));
    }

    [Fact]
    public void RemovedDeviceTakesItsRowsAlong()
    {
        var center = AllOn();
        var other = Guid.NewGuid();
        center.Add(Motion(1, T0), T0);
        center.Add(Motion(1, T0, other), T0);

        center.ForgetDevice(DeviceA);

        Assert.Equal(other, Assert.Single(center.Events).DeviceId);
        Assert.Equal(EventOutcome.Notify, center.Add(Motion(1, T0.AddSeconds(30)), T0.AddSeconds(30))); // its throttle went too
    }

    [Fact]
    public void SwitchingAKindOffStopsCollectingIt()
    {
        var center = AllOn();
        center.Settings.Set(AlertKind.Motion, false);

        Assert.Equal(EventOutcome.Ignored, center.Add(Motion(1, T0), T0));
        Assert.False(center.Settings.IsOn(AlertKind.Motion));
        foreach (var kind in Enum.GetValues<AlertKind>())
        {
            center.Settings.Set(kind, false);
        }
        Assert.False(center.Settings.AnyKind);
    }

    [Theory]
    [InlineData(EventFilter.All, AlertKind.Motion, true)]
    [InlineData(EventFilter.All, AlertKind.Disk, true)]
    [InlineData(EventFilter.Motion, AlertKind.Motion, true)]
    [InlineData(EventFilter.Motion, AlertKind.Smart, true)]
    [InlineData(EventFilter.Motion, AlertKind.Tamper, true)]
    [InlineData(EventFilter.Motion, AlertKind.CameraOffline, false)]
    [InlineData(EventFilter.Connection, AlertKind.CameraOffline, true)]
    [InlineData(EventFilter.Connection, AlertKind.Disk, false)]
    [InlineData(EventFilter.Disk, AlertKind.Disk, true)]
    [InlineData(EventFilter.Disk, AlertKind.Motion, false)]
    public void FilterSegments(EventFilter filter, AlertKind kind, bool shown) => Assert.Equal(shown, EventCenter.Matches(kind, filter));

    [Fact]
    public void RowTextsAndCameraReference()
    {
        var center = AllOn();
        center.Add(Motion(2, new DateTime(2026, 10, 22, 10, 52, 14)), T0);
        center.Add(Offline(40, T0, EventSource.Recorder), T0, knownCamera: false);

        var motion = center.Events[1];
        Assert.Equal(new CameraRef(DeviceA, 2), motion.Camera);
        Assert.Equal("KAMERA 2 · 10:52:14", EventText.PlaceAndTime(motion));
        Assert.Null(center.Events[0].Camera); // unknown channel: no buttons
    }

    [Fact]
    public void NotificationSettingsAreSavedAndOldFilesGetDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ \"Devices\": [] }"); // settings.json from before this feature
            var store = new SettingsStore(path);
            var old = store.Load();
            Assert.True(old.Notifications.CameraOffline && old.Notifications.Disk && old.Notifications.Popup);
            Assert.False(old.Notifications.Motion);

            old.Notifications.Motion = true;
            old.Notifications.Sound = true;
            old.Notifications.Disk = false;
            old.Notifications.IntervalSeconds = 300;
            store.Save(old);
            var loaded = store.Load().Notifications;

            Assert.True(loaded.Motion && loaded.Sound && loaded.CameraOffline);
            Assert.False(loaded.Disk);
            Assert.Equal(300, loaded.IntervalSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- live tiles and disks ----

    static (CameraRef, TileDropWatch.TileCondition) Tile(int channel, TileDropWatch.TileCondition condition) =>
        (new CameraRef(DeviceA, channel), condition);

    [Fact]
    public void TileThatWasPlayingAndLostItsStreamIsADrop()
    {
        var watch = new TileDropWatch();
        var P = TileDropWatch.TileCondition.Playing;
        var L = TileDropWatch.TileCondition.Lost;
        var C = TileDropWatch.TileCondition.Connecting;

        Assert.Empty(watch.Update([Tile(1, C), Tile(2, C)]));        // opening: never played
        Assert.Empty(watch.Update([Tile(1, L), Tile(2, P)]));        // camera 1 failed to open: not a drop
        Assert.Empty(watch.Update([Tile(1, C), Tile(2, P)]));
        var dropped = watch.Update([Tile(1, C), Tile(2, L)]);
        Assert.Equal(new CameraRef(DeviceA, 2), Assert.Single(dropped));
        Assert.Empty(watch.Update([Tile(1, C), Tile(2, L)]));        // still retrying: once only
        Assert.Empty(watch.Update([Tile(1, C), Tile(2, P)]));        // back
        Assert.Single(watch.Update([Tile(1, C), Tile(2, L)]));       // lost again: a new drop
    }

    [Fact]
    public void ReopenLeavingTheScreenOrStoppingIsNotADrop()
    {
        var watch = new TileDropWatch();
        var P = TileDropWatch.TileCondition.Playing;
        var L = TileDropWatch.TileCondition.Lost;
        var C = TileDropWatch.TileCondition.Connecting;
        var O = TileDropWatch.TileCondition.Other;

        watch.Update([Tile(1, P), Tile(2, P), Tile(3, P)]);
        Assert.Empty(watch.Update([Tile(1, C), Tile(2, P), Tile(3, P)])); // quality / decoder switch: reopening
        Assert.Empty(watch.Update([Tile(1, P), Tile(3, O)]));             // camera 2 paged away, camera 3 stopped
        Assert.Empty(watch.Update([Tile(1, P), Tile(2, L), Tile(3, L)])); // neither was playing since

        Assert.Empty(watch.Update([Tile(1, C)]));                         // reopening…
        Assert.Single(watch.Update([Tile(1, L)]));                        // …and it fails: the camera was playing, a drop
    }

    [Fact]
    public void DiskEventOnlyWhenARecorderGoesFromHealthyToAProblem()
    {
        var watch = new DiskEventWatch();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        IReadOnlyList<DiskProblem> bad = [new DiskProblem(a, "NVR", "HDD 2", "Hata")];

        Assert.Empty(watch.Update(a, []));          // healthy
        Assert.Single(watch.Update(a, bad));        // → problem
        Assert.Empty(watch.Update(a, bad));         // every 5 minutes after: not again
        Assert.Empty(watch.Update(a, []));          // repaired
        Assert.Single(watch.Update(a, bad));        // broken again
        Assert.Single(watch.Update(b, [new DiskProblem(b, "NVR 2", "", DiskWatch.NoDisk)])); // first read already a problem
        watch.Forget(a);
        Assert.Single(watch.Update(a, bad));        // removed and added again
    }

    [Fact]
    public void DiskTitles()
    {
        var a = Guid.NewGuid();

        Assert.Equal("Disk sorunu: HDD 2 (hata)", EventText.DiskTitle([new DiskProblem(a, "NVR", "HDD 2", "Hata")]));
        Assert.Equal("Disk sorunu: Disk yok", EventText.DiskTitle([new DiskProblem(a, "NVR", "", DiskWatch.NoDisk)]));
        Assert.Equal("Disk sorunu: HDD 1 (smart hatası) +1", EventText.DiskTitle(
            [new DiskProblem(a, "NVR", "HDD 1", "SMART hatası"), new DiskProblem(a, "NVR", "HDD 3", "Çevrimdışı")]));
        Assert.Empty(new DiskWatch().ProblemsFor(a)); // never read
    }

    [Fact]
    public void ReconnectBackoffGrowsAndCaps()
    {
        Assert.Equal([5, 10, 30, 60, 120, 120], Enumerable.Range(0, 6).Select(i => AlertBackoff.Delay(i).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(5), AlertBackoff.Delay(-1));
        Assert.Equal(TimeSpan.FromMinutes(1), AlertBackoff.StableAfter);
    }

    // ---- the read-only GET (loopback) ----

    static Device Loopback(LoopbackServer server) =>
        new() { Host = "127.0.0.1", HttpPort = server.Port, UserName = "admin", Password = "s3cret-Pw" };

    [Fact]
    public async Task AlertStreamIsADigestGetAndItsPartsAreRead()
    {
        var body = Part(Alert("videoloss", "inactive")) + Part(Alert("VMD", channel: 2));
        using var server = new LoopbackServer(r => r.Headers.ContainsKey("Authorization")
            ? LoopbackServer.Answer(200, "OK", body, "Content-Type: multipart/mixed; boundary=boundary")
            : LoopbackServer.Answer(401, "Unauthorized", "", "WWW-Authenticate: Digest realm=\"nvr\", nonce=\"6b0d1d2e\", qop=\"auth\""));
        using var client = new IsapiClient(Loopback(server));

        using var stream = await client.OpenAlertStreamAsync();
        var parser = new AlertStreamParser(stream.Boundary);
        var alerts = new List<AlertEvent>();
        var buffer = new byte[1024];
        int n;
        while ((n = await stream.Body.ReadAsync(buffer)) > 0)
        {
            alerts.AddRange(parser.Feed(buffer.AsSpan(0, n)));
        }

        Assert.Equal("boundary", stream.Boundary);
        Assert.Equal("multipart/mixed", stream.ContentType);
        Assert.Equal(["videoloss", "VMD"], alerts.Select(a => a.EventType));
        Assert.All(server.Requests, r => Assert.Equal("GET", r.Method)); // read only: a subscription, nothing configured
        Assert.All(server.Requests, r => Assert.Equal("/ISAPI/Event/notification/alertStream", r.Path));
        Assert.StartsWith("Digest ", server.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task AlertStreamRefusalAndRedirectThrow()
    {
        using var refused = new LoopbackServer(_ => LoopbackServer.Answer(403, "Forbidden", "<ResponseStatus/>"));
        using var moved = new LoopbackServer(_ => LoopbackServer.Answer(302, "Found", "", "Location: /elsewhere"));

        var error = await Assert.ThrowsAsync<IsapiException>(() => new IsapiClient(Loopback(refused)).OpenAlertStreamAsync());
        var redirect = await Assert.ThrowsAsync<IsapiException>(() => new IsapiClient(Loopback(moved)).OpenAlertStreamAsync());

        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.Contains("yönlendirme izlenmez", redirect.Message);
        Assert.Single(moved.Requests); // not followed
    }
}
