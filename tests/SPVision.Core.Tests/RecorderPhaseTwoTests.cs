using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>Olaylar / Ağ / Kullanıcılar parsing and plans. Document shapes as read from a DS-8632NI-I8 (values made up).</summary>
public class RecorderPhaseTwoTests
{
    const string Ns = "http://www.isapi.org/ver20/XMLSchema";

    // ================================================================ Olaylar

    static readonly XDocument TriggersCap = XDocument.Parse($"""
        <EventTriggersCap xmlns="{Ns}">
          <DiskfullTriggerCap><isSupportBeep>true</isSupportBeep><isSupportCenter>true</isSupportCenter><isSupportIO>true</isSupportIO><isSupportEmail>true</isSupportEmail></DiskfullTriggerCap>
          <NicbrokenTriggerCap><isSupportBeep>true</isSupportBeep><isSupportIO>true</isSupportIO><isSupportEmail>true</isSupportEmail></NicbrokenTriggerCap>
          <VideoLossTriggerCap><isSupportCenter>true</isSupportCenter><isSupportBeep>true</isSupportBeep><isSupportEmail>false</isSupportEmail></VideoLossTriggerCap>
          <HideTriggerCap />
        </EventTriggersCap>
        """);

    static XDocument Triggers() => XDocument.Parse($"""
        <EventTriggerList xmlns="{Ns}">
          <EventTrigger><id>VMD-1</id><eventType>VMD</eventType><dynVideoInputChannelID>1</dynVideoInputChannelID>
            <EventTriggerNotificationList><EventTriggerNotification><id>record-1</id><notificationMethod>record</notificationMethod><dynVideoInputID>1</dynVideoInputID></EventTriggerNotification></EventTriggerNotificationList></EventTrigger>
          <EventTrigger><id>videoloss-1</id><eventType>videoloss</eventType><dynVideoInputChannelID>1</dynVideoInputChannelID>
            <EventTriggerNotificationList><EventTriggerNotification><id>beep</id><notificationMethod>beep</notificationMethod></EventTriggerNotification></EventTriggerNotificationList></EventTrigger>
          <EventTrigger><id>videoloss-2</id><eventType>videoloss</eventType><dynVideoInputChannelID>2</dynVideoInputChannelID><EventTriggerNotificationList /></EventTrigger>
          <EventTrigger><id>tamper-1</id><eventType>tamperdetection</eventType><EventTriggerNotificationList /></EventTrigger>
          <EventTrigger><id>tamper-2</id><eventType>tamperdetection</eventType><EventTriggerNotificationList /></EventTrigger>
          <EventTrigger><id>diskfull</id><eventType>diskfull</eventType><EventTriggerNotificationList /></EventTrigger>
          <EventTrigger><id>diskerror</id><eventType>diskerror</eventType>
            <EventTriggerNotificationList><EventTriggerNotification><id>beep</id><notificationMethod>beep</notificationMethod></EventTriggerNotification></EventTriggerNotificationList></EventTrigger>
          <EventTrigger><id>objectsThrownDetection</id><eventType>objectsThrownDetection</eventType><EventTriggerNotificationList /></EventTrigger>
          <EventTrigger><id>objectsThrownDetection</id><eventType>objectsThrownDetection</eventType><EventTriggerNotificationList /></EventTrigger>
        </EventTriggerList>
        """);

    static XDocument Single(XDocument list, string id) =>
        new(list.Root!.Elements().First(e => e.Elements().First(c => c.Name.LocalName == "id").Value == id));

    [Fact]
    public void CapsTellWhichReactionsEachEventOffers()
    {
        var caps = EventTriggerCaps.Parse(TriggersCap);
        var nic = EventKinds.Exceptions.First(k => k.Key == "nicbroken");
        Assert.True(caps.Supports(nic, NotifyMethod.Beep));
        Assert.False(caps.Supports(nic, NotifyMethod.Center)); // the device does not offer it
        Assert.True(caps.Supports(EventKinds.VideoLoss, NotifyMethod.Center));
        Assert.False(caps.Supports(EventKinds.VideoLoss, NotifyMethod.Email)); // explicitly false
        Assert.False(caps.Supports(EventKinds.Tamper, NotifyMethod.Beep));    // block missing
        Assert.False(EventTriggerCaps.None.Supports(EventKinds.VideoLoss, NotifyMethod.Beep));
    }

    [Fact]
    public void TriggerStatesOverCameras()
    {
        var triggers = EventTriggers.Parse(Triggers());
        Assert.Equal(LinkState.Mixed, triggers.State(EventKinds.VideoLoss, [1, 2], NotifyMethod.Beep));
        Assert.Equal(LinkState.On, triggers.State(EventKinds.VideoLoss, [1], NotifyMethod.Beep));
        Assert.Equal(LinkState.Off, triggers.State(EventKinds.Tamper, [1, 2], NotifyMethod.Beep));
        Assert.Equal(LinkState.Missing, triggers.State(EventKinds.Tamper, [7], NotifyMethod.Beep));
        var diskerror = EventKinds.Exceptions.First(k => k.Key == "diskerror");
        Assert.Equal(LinkState.On, triggers.State(diskerror, [], NotifyMethod.Beep));
        Assert.Equal(LinkState.Off, triggers.State(diskerror, [], NotifyMethod.Email));
        Assert.True(triggers.Has("VMD-1", NotifyMethod.Beep) == false);
        Assert.Null(triggers.Has("nicbroken", NotifyMethod.Beep));
    }

    [Fact]
    public void NotificationEditAddsAndRemovesOnlyItsMethod()
    {
        var vmd = Single(Triggers(), "VMD-1").Root!;
        new NotificationEdit("Hareket", NotifyMethod.Beep, true).Apply(vmd);
        new NotificationEdit("Hareket", NotifyMethod.Beep, true).Apply(vmd); // twice: still one
        var methods = vmd.Descendants().Where(e => e.Name.LocalName == "notificationMethod").Select(e => e.Value).ToList();
        Assert.Equal(["record", "beep"], methods);
        Assert.All(vmd.Descendants(), e => Assert.Equal(Ns, e.Name.NamespaceName)); // added in the document's namespace

        var off = new NotificationEdit("Hareket", NotifyMethod.Beep, false);
        Assert.Equal("Açık", off.Verify(vmd)); // not off yet
        off.Apply(vmd);
        Assert.Null(off.Verify(vmd));
        Assert.Equal(["record"], vmd.Descendants().Where(e => e.Name.LocalName == "notificationMethod").Select(e => e.Value).ToList());
    }

    [Fact]
    public void NotificationEditNeedsTheList()
    {
        var doc = XElement.Parse("<EventTrigger><id>diskfull</id></EventTrigger>");
        Assert.Throws<ConfigEditException>(() => new NotificationEdit("Disk dolu", NotifyMethod.Beep, true).Apply(doc));
    }

    [Fact]
    public async Task LinkagePlanWritesOnlyTriggersThatDiffer()
    {
        var list = Triggers();
        var before = EventTriggers.Parse(list);
        var wanted = new Dictionary<(string, NotifyMethod), bool>
        {
            [("videoloss", NotifyMethod.Beep)] = true,   // camera 1 already on → only camera 2
            [("diskfull", NotifyMethod.Email)] = true,
            [("diskerror", NotifyMethod.Beep)] = true,   // unchanged → nothing
        };
        var cameras = new List<(int, string)> { (1, "DIS KAPI"), (2, "OFIS") };

        var writes = EventPlan.Linkage(before, wanted, cameras);

        Assert.Equal(["ISAPI/Event/triggers/videoloss-2", "ISAPI/Event/triggers/diskfull"], writes.Select(w => w.Path).ToList());
        Assert.Equal(["OFIS · Görüntü kaybı · Sesli uyarı: Kapalı → Açık", "Kayıt cihazı · Disk dolu · E-posta: Kapalı → Açık"],
            writes.SelectMany(w => w.Descriptions).ToList());
        Assert.All(writes, w => Assert.Null(IsapiWritePolicy.Check(w.Path)));

        var transport = new FakeTransport();
        foreach (var id in new[] { "videoloss-2", "diskfull" })
        {
            transport.Docs[IsapiPaths.EventTrigger(id)] = Single(list, id);
        }
        var report = await ConfigWriter.ApplyAsync(transport, writes);
        Assert.True(report.Success, report.FailureMessage);
        var sent = transport.Sent[IsapiPaths.EventTrigger("diskfull")].Root!;
        Assert.Equal("email", sent.Descendants().Single(e => e.Name.LocalName == "notificationMethod").Value);
    }

    [Fact]
    public void CameraEventPlanUsesTheEnabledFlag()
    {
        var writes = EventPlan.Camera(3, "DEPO", new EventEnable(false), true, new EventEnable(true), true);
        var write = Assert.Single(writes);
        Assert.Equal("ISAPI/ContentMgmt/InputProxy/channels/3/video/videoLoss", write.Path);
        Assert.Equal("DEPO · Görüntü kaybı: Kapalı → Açık", Assert.Single(write.Descriptions));
        Assert.Empty(EventPlan.Camera(3, "DEPO", null, true, null, false)); // not offered → never written
    }

    [Fact]
    public void EventEnableParse()
    {
        Assert.Equal(new EventEnable(false), EventEnable.Parse(XDocument.Parse($"<VideoLoss xmlns=\"{Ns}\"><enabled>false</enabled></VideoLoss>")));
        Assert.Null(EventEnable.Parse(XDocument.Parse("<VideoLoss />")));
    }

    // ================================================================ Ağ

    static readonly XDocument Interfaces = XDocument.Parse($"""
        <NetworkInterfaceList xmlns="{Ns}">
          <NetworkInterface><id>1</id>
            <IPAddress><ipVersion>dual</ipVersion><addressingType>static</addressingType><ipAddress>192.168.1.2</ipAddress><subnetMask>255.255.255.0</subnetMask>
              <ipv6Address>::</ipv6Address><DefaultGateway><ipAddress>192.168.1.1</ipAddress></DefaultGateway>
              <PrimaryDNS><ipAddress>8.8.8.8</ipAddress></PrimaryDNS><SecondaryDNS><ipAddress>0.0.0.0</ipAddress></SecondaryDNS><DNSEnable>false</DNSEnable></IPAddress>
            <Link><MACAddress>00:00:00:00:00:00</MACAddress></Link><defaultConnection>true</defaultConnection></NetworkInterface>
          <NetworkInterface><id>2</id>
            <IPAddress><addressingType>static</addressingType><ipAddress>192.168.254.1</ipAddress><subnetMask>255.255.255.0</subnetMask>
              <DefaultGateway><ipAddress>192.168.254.254</ipAddress></DefaultGateway></IPAddress><defaultConnection>false</defaultConnection></NetworkInterface>
        </NetworkInterfaceList>
        """);

    static NetworkAddress Lan => new(false, "192.168.1.2", "255.255.255.0", "192.168.1.1", "8.8.8.8", "0.0.0.0");

    [Fact]
    public void InterfacesAreParsedAndTheOneSpvisionUsesIsFound()
    {
        var list = NetworkInterfaceInfo.ParseList(Interfaces);
        Assert.Equal(2, list.Count);
        Assert.Equal(Lan, list[0].Address);
        Assert.Equal("", list[1].Address!.Dns1);
        Assert.Equal(2, NetworkInterfaceInfo.ForHost(list, "192.168.254.1")!.Id);
        Assert.Equal(1, NetworkInterfaceInfo.ForHost(list, "nvr.local")!.Id); // default connection
    }

    [Fact]
    public void PortsAreReadButNothingElseOfAdminAccesses()
    {
        var doc = XDocument.Parse($"""
            <AdminAccessProtocolList xmlns="{Ns}">
              <AdminAccessProtocol><id>1</id><enabled>true</enabled><protocol>HTTP</protocol><portNo>80</portNo></AdminAccessProtocol>
              <AdminAccessProtocol><id>2</id><enabled>true</enabled><protocol>RTSP</protocol><portNo>554</portNo></AdminAccessProtocol>
              <AdminAccessProtocol><id>3</id><enabled>true</enabled><protocol>HTTPS</protocol><portNo>443</portNo><TLS1_2Enable>true</TLS1_2Enable></AdminAccessProtocol>
              <AdminAccessProtocol><id>4</id><enabled>true</enabled><protocol>DEV_MANAGE</protocol><portNo>8000</portNo></AdminAccessProtocol>
            </AdminAccessProtocolList>
            """);
        Assert.Equal(new PortSettings(80, 443, 554, 8000), PortSettings.Parse(doc));
        Assert.Equal("—", PortSettings.Text(null));
    }

    [Theory]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("192.168.1", false)]
    [InlineData("192.168.1.256", false)]
    [InlineData("192.168.01.2", false)]
    [InlineData("192.168.1.2 ", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("", false)]
    public void HostAddresses(string text, bool ok)
    {
        Assert.Equal(ok, Ipv4.IsHost(text));
    }

    [Theory]
    [InlineData("255.255.255.0", true)]
    [InlineData("255.255.0.0", true)]
    [InlineData("255.255.255.252", true)]
    [InlineData("255.255.255.255", false)]
    [InlineData("255.0.255.0", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("255.255.255.1", false)]
    public void Masks(string text, bool ok)
    {
        Assert.Equal(ok, Ipv4.IsMask(text));
    }

    [Theory]
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.1", "8.8.8.8", null)]
    [InlineData("192.168.1.0", "255.255.255.0", "192.168.1.1", "8.8.8.8", "ağ veya yayın")]
    [InlineData("192.168.1.255", "255.255.255.0", "192.168.1.1", "8.8.8.8", "ağ veya yayın")]
    [InlineData("192.168.2.20", "255.255.255.0", "192.168.1.1", "8.8.8.8", "aynı alt ağda")]
    [InlineData("192.168.1.20", "255.255.255.0", "192.168.1.20", "8.8.8.8", "kendi adresi")]
    [InlineData("192.168.1.300", "255.255.255.0", "192.168.1.1", "8.8.8.8", "IP adresi geçerli değil")]
    [InlineData("192.168.1.20", "255.0.255.0", "192.168.1.1", "8.8.8.8", "maskesi geçerli değil")]
    [InlineData("192.168.1.2", "255.255.255.0", "192.168.1.1", "8.8.8", "Birincil DNS")]
    [InlineData("192.168.1.2", "255.255.255.0", "192.168.1.1", "0.0.0.0", null)]
    public void Validation(string ip, string mask, string gateway, string dns1, string? error)
    {
        var after = Lan with { Ip = ip, Mask = mask, Gateway = gateway, Dns1 = dns1 };
        var message = NetworkPlan.Validate(Lan, after);
        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(error, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnchangedOddValuesDoNotBlockOtherChanges()
    {
        var odd = Lan with { Gateway = "10.0.0.1" }; // the device's own (cross-subnet) gateway
        Assert.Null(NetworkPlan.Validate(odd, odd with { Dns2 = "1.1.1.1" }));
        Assert.NotNull(NetworkPlan.Validate(odd, odd with { Ip = "192.168.1.3" }));
    }

    [Fact]
    public void EditsAndTheNewHost()
    {
        var after = Lan with { Ip = "192.168.1.20", Dns2 = "1.1.1.1" };
        var edits = NetworkPlan.Edits(Lan, after);
        Assert.Equal(["Ağ · IP adresi: 192.168.1.2 → 192.168.1.20", "Ağ · İkincil DNS: 0.0.0.0 → 1.1.1.1"],
            edits.Select(e => ConfigChangeText.Format("Ağ", e.Label, e.OldText, e.NewText)).ToList());
        Assert.Equal("192.168.1.20", NetworkPlan.NewHost("192.168.1.2", Lan, after));
        Assert.Null(NetworkPlan.NewHost("nvr.local", Lan, after));           // SPVision does not use that address
        Assert.Null(NetworkPlan.NewHost("192.168.1.2", Lan, Lan with { Dhcp = true })); // unknown with DHCP

        var dhcp = NetworkPlan.Edits(Lan, Lan with { Dhcp = true, Ip = "1.2.3.4" });
        Assert.Equal("Adres alma", Assert.Single(dhcp).Label); // the rest is the DHCP server's
    }

    [Fact]
    public void NetworkRequestBodyIsTheReadDocumentWithTheEdits()
    {
        var doc = new XDocument(Interfaces.Root!.Elements().First().Elements().First(e => e.Name.LocalName == "IPAddress"));
        var after = Lan with { Ip = "192.168.1.20", Gateway = "192.168.1.254" };

        var request = RiskyWrites.NetworkAddress(1, doc, NetworkPlan.Edits(Lan, after));

        Assert.Equal("ISAPI/System/Network/interfaces/1/ipAddress", request.Path);
        Assert.Equal("PUT", request.Method);
        var body = XDocument.Parse(request.Body!);
        Assert.Equal(after, NetworkAddress.ParseDocument(body));
        Assert.Equal("dual", body.Root!.Elements().First(e => e.Name.LocalName == "ipVersion").Value); // the rest untouched
        Assert.Equal(Lan, NetworkAddress.ParseDocument(doc)); // the read document itself is not changed
        Assert.Equal(["Ağ · IP adresi: 192.168.1.2 → 192.168.1.20", "Ağ · Ağ geçidi: 192.168.1.1 → 192.168.1.254"], request.Changes);
        Assert.Throws<ConfigEditException>(() => RiskyWrites.NetworkAddress(1, doc, []));
    }

    [Fact]
    public void BondState()
    {
        Assert.False(BondInfo.Parse(XDocument.Parse($"<BondList xmlns=\"{Ns}\"><Bond><id>1</id><enabled>false</enabled></Bond></BondList>")).Enabled);
        Assert.True(BondInfo.Parse(XDocument.Parse("<BondList><Bond><enabled>true</enabled></Bond></BondList>")).Enabled);
    }

    // ================================================================ Kullanıcılar

    static readonly XDocument UserList = XDocument.Parse($"""
        <UserList xmlns="{Ns}">
          <User><id>1</id><userName>admin</userName><bondIpAddressList><bondIpAddress><id>1</id><ipAddress>0.0.0.0</ipAddress></bondIpAddress></bondIpAddressList><userLevel>Administrator</userLevel><attribute><inherent>true</inherent></attribute></User>
          <User><id>3</id><userName>kapi</userName><password>should-never-be-read</password><userLevel>Operator</userLevel></User>
          <User><id>2</id><userName>izle</userName><userLevel>Viewer</userLevel></User>
        </UserList>
        """);

    [Fact]
    public void UsersAreListedWithoutPasswords()
    {
        var users = DeviceUser.ParseList(UserList);
        Assert.Equal([new DeviceUser(1, "admin", "Administrator"), new DeviceUser(2, "izle", "Viewer"), new DeviceUser(3, "kapi", "Operator")], users);
        Assert.True(users[0].IsAdmin);
        Assert.False(users[2].IsAdmin);
        Assert.Equal("İzleyici", DeviceUser.LevelText(users[1].Level));
        Assert.DoesNotContain("should-never-be-read", string.Join(",", users.Select(u => u.ToString())), StringComparison.Ordinal);
        Assert.Equal(4, UserRules.NextFreeId(users, 32));
        Assert.Null(UserRules.NextFreeId(users, 3));
        Assert.Equal(32, UserLimits.Parse(XDocument.Parse($"<SecurityCap xmlns=\"{Ns}\"><supportUserNums>32</supportUserNums></SecurityCap>")).MaxUsers);
    }

    [Theory]
    [InlineData("kamera1", null)]
    [InlineData("", "boş")]
    [InlineData("kapi", "zaten var")]
    [InlineData("KAPI", "zaten var")]
    [InlineData("admin", "zaten var")]
    [InlineData("çalışan", "İngilizce")]
    [InlineData("a b", "İngilizce")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", "en çok")]
    public void UserNames(string name, string? error)
    {
        var message = UserRules.CheckName(name, DeviceUser.ParseList(UserList));
        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(error, message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Abcdefg1", "Abcdefg1", null)]
    [InlineData("abcdefgh1", "abcdefgh1", null)]
    [InlineData("abc-defgh", "abc-defgh", null)]
    [InlineData("Abc1", "Abc1", "8–16")]
    [InlineData("Abcdefghijklmnop1", "Abcdefghijklmnop1", "8–16")]
    [InlineData("abcdefgh", "abcdefgh", "en az ikisi")]
    [InlineData("12345678", "12345678", "en az ikisi")]
    [InlineData("Şifre12345", "Şifre12345", "Türkçe")]
    [InlineData("abc defg1", "abc defg1", "Türkçe")]
    [InlineData("xkapi1234", "xkapi1234", "kullanıcı adını")]
    [InlineData("Abcdefg1", "Abcdefg2", "aynı değil")]
    public void Passwords(string password, string repeat, string? error)
    {
        var message = UserRules.CheckPassword("kapi", password, repeat);
        if (error is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(error, message, StringComparison.Ordinal);
        }
    }
}
