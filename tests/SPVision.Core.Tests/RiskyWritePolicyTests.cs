using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Phase 2 write policy: the new ordinary paths (Olaylar), the exact risky entries (reboot, format, network address,
/// users) behind <see cref="ConfirmedRiskyWrite"/>, everything that stays denied, and bypass attempts. No network:
/// requests go to an in-memory handler.
/// </summary>
public class RiskyWritePolicyTests
{
    // ================================================================ ordinary route: new paths

    public static TheoryData<string> PhaseTwoOrdinaryPaths()
    {
        var data = new TheoryData<string>();
        foreach (var channel in new[] { 1, 9, 22, 32, 128 })
        {
            data.Add(IsapiPaths.VideoLoss(channel));
            data.Add(IsapiPaths.Tamper(channel));
            data.Add(IsapiPaths.EventTrigger(EventKinds.VideoLoss.TriggerId(channel)));
            data.Add(IsapiPaths.EventTrigger(EventKinds.Tamper.TriggerId(channel)));
        }
        foreach (var kind in EventKinds.Exceptions)
        {
            data.Add(IsapiPaths.EventTrigger(kind.Key));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PhaseTwoOrdinaryPaths))]
    public void AllowsEveryNewOrdinaryPath(string path)
    {
        Assert.Null(IsapiWritePolicy.Check(path));
    }

    [Theory]
    [InlineData("ISAPI/Event/triggers")]                        // the whole list
    [InlineData("ISAPI/Event/triggers/VMD-1")]                  // motion linkage (record): not offered
    [InlineData("ISAPI/Event/triggers/IO-1")]
    [InlineData("ISAPI/Event/triggers/spareException")]
    [InlineData("ISAPI/Event/triggers/videoloss-0")]
    [InlineData("ISAPI/Event/triggers/tamper-1234")]
    [InlineData("ISAPI/Event/triggers/videoloss")]
    [InlineData("ISAPI/Event/triggers/diskfull/")]              // trailing slash
    [InlineData("ISAPI/event/triggers/diskfull")]               // allowlist is exact case
    [InlineData("ISAPI/Event/triggers/DISKFULL")]
    [InlineData("ISAPI/Event/triggers/diskfull?x=1")]
    [InlineData("ISAPI/Event/triggers/diskfull%2F..%2Fx")]
    [InlineData("ISAPI/Event/triggers/../triggers/diskfull")]
    [InlineData("ISAPI/Event/triggersCap")]
    [InlineData("ISAPI/ContentMgmt/InputProxy/channels/1/video/videoLoss/capabilities")]
    [InlineData("ISAPI/System/Video/inputs/channels/1/videoLoss")]
    public void DeniesNearMissesOfTheNewOrdinaryPaths(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
    }

    public static TheoryData<string> RiskyPaths() =>
    [
        IsapiPaths.Reboot, IsapiPaths.HddFormat(1), IsapiPaths.InterfaceAddress(1), IsapiPaths.Users, IsapiPaths.User(1), IsapiPaths.User(2),
    ];

    [Theory]
    [MemberData(nameof(RiskyPaths))]
    public void RiskyPathsAreNeverOrdinaryWrites(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureAllowed(path));
    }

    // ================================================================ risky route: exact entries

    public static TheoryData<RiskyOperation, string, string> AllowedRisky()
    {
        var data = new TheoryData<RiskyOperation, string, string>
        {
            { RiskyOperation.Reboot, "PUT", "ISAPI/System/reboot" },
            { RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/1/ipAddress" },
            { RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/2/ipAddress" },
            { RiskyOperation.AddUser, "POST", "ISAPI/Security/users" },
            { RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/1" },
            { RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/32" },
            { RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/2" },
            { RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/32" },
        };
        foreach (var disk in new[] { 1, 2, 9, 16, 99 })
        {
            data.Add(RiskyOperation.FormatDisk, "PUT", IsapiPaths.HddFormat(disk));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedRisky))]
    public void AllowsEveryRiskyEntryForItsOperationAndMethodOnly(RiskyOperation operation, string method, string path)
    {
        Assert.Null(IsapiWritePolicy.CheckRisky(operation, method, path));
        foreach (var other in Enum.GetValues<RiskyOperation>().Where(o => o != operation))
        {
            Assert.NotNull(IsapiWritePolicy.CheckRisky(other, method, path));
        }
        foreach (var otherMethod in new[] { "GET", "PUT", "POST", "DELETE", "PATCH", "put", "Put" }.Where(m => m != method))
        {
            Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, otherMethod, path));
        }
    }

    [Theory]
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/1")]          // built-in admin: never
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users")]
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/0")]
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/01")]
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/100")]
    [InlineData(RiskyOperation.DeleteUser, "DELETE", "ISAPI/Security/users/2/../1")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/0")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/1/")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/1?loginPassword=x")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/1#x")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/Users/1")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/security/users/1")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/users/1/permission")]
    [InlineData(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/UserPermission/1")]
    [InlineData(RiskyOperation.AddUser, "POST", "ISAPI/Security/users/")]
    [InlineData(RiskyOperation.AddUser, "POST", "ISAPI/Security/users?security=1")]
    [InlineData(RiskyOperation.AddUser, "POST", "/ISAPI/Security/users")]
    [InlineData(RiskyOperation.AddUser, "POST", "ISAPI//Security/users")]
    [InlineData(RiskyOperation.AddUser, "POST", "ISAPI\\Security\\users")]
    [InlineData(RiskyOperation.AddUser, "POST", "http://192.0.2.1/ISAPI/Security/users")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/system/reboot")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/SYSTEM/REBOOT")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/System/reboot/")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/System/reboot?delay=0")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/System/time/../reboot")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/System%2Freboot")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/System/%72eboot")]
    [InlineData(RiskyOperation.Reboot, "PUT", " ISAPI/System/reboot")]
    [InlineData(RiskyOperation.Reboot, "PUT", "ISAPI/ContentMgmt/InputProxy/channels/1/reboot")]   // camera reboot: not offered
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/0/format")]
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/100/format")]
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/format")]            // all disks at once: never
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/1/format/")]
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/1/Format")]
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/nas/1/format")]
    [InlineData(RiskyOperation.FormatDisk, "PUT", "ISAPI/ContentMgmt/Storage/hdd/1/format?formatType=quick")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/1")]            // whole NIC (MAC, MTU…)
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/0/ipAddress")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/10/ipAddress")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/1/ipaddress")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/Bond/1")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/1/ipAddress/")]
    [InlineData(RiskyOperation.NetworkAddress, "PUT", "ISAPI/System/Network/interfaces/1/ipAddress%3F")]
    public void DeniesRiskyBypassAttempts(RiskyOperation operation, string method, string path)
    {
        Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, path));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyAllowed(operation, method, path));
    }

    /// <summary>Permanently denied on every route and for every risky operation and method.</summary>
    [Theory]
    [InlineData("ISAPI/System/Network/EZVIZ")]
    [InlineData("ISAPI/System/Network/EZVIZ/secretKey")]
    [InlineData("ISAPI/Streaming/encryption")]
    [InlineData("ISAPI/Streaming/channels/101/encrypt")]
    [InlineData("ISAPI/System/HikConnect")]
    [InlineData("ISAPI/System/Hik-Connect")]
    [InlineData("ISAPI/System/Network/cloud")]
    [InlineData("ISAPI/System/platform/1")]
    [InlineData("ISAPI/Security/adminAccesses")]
    [InlineData("ISAPI/Security/adminAccesses/1")]
    [InlineData("ISAPI/Security/serverCertificate/certificate")]
    [InlineData("ISAPI/Security/deviceCertificate")]
    [InlineData("ISAPI/Security/SRTP")]
    [InlineData("ISAPI/Security/TLS")]
    [InlineData("ISAPI/Security/illegalLoginLock")]
    [InlineData("ISAPI/Security/loginLock")]
    [InlineData("ISAPI/Security/securityQuestion")]
    [InlineData("ISAPI/Security/ONVIF/users")]
    [InlineData("ISAPI/System/factoryReset")]
    [InlineData("ISAPI/System/updateFirmware")]
    [InlineData("ISAPI/System/upgradeStatus")]
    [InlineData("ISAPI/System/activate")]
    [InlineData("ISAPI/System/configurationData")]
    [InlineData("ISAPI/System/shutdown")]
    [InlineData("ISAPI/SECURITY/ADMINACCESSES")]
    [InlineData("isapi/system/network/ezviz")]
    public void ForbiddenPathsStayDeniedEverywhere(string path)
    {
        Assert.NotNull(IsapiWritePolicy.Check(path));
        foreach (var operation in Enum.GetValues<RiskyOperation>())
        {
            foreach (var method in new[] { "GET", "PUT", "POST", "DELETE" })
            {
                Assert.NotNull(IsapiWritePolicy.CheckRisky(operation, method, path));
            }
        }
    }

    [Theory]
    [InlineData("ISAPI/Security/users")]
    [InlineData("ISAPI/Security/users/1")]
    [InlineData("ISAPI/System/Network/interfaces/1/ipAddress")]
    [InlineData("ISAPI/System/reboot")]
    [InlineData("ISAPI/ContentMgmt/Storage/hdd/1/format")]
    public void EveryRiskyPathPassesExactlyOneOperationMethodPair(string path)
    {
        var matches = Enum.GetValues<RiskyOperation>()
            .SelectMany(o => new[] { "PUT", "POST", "DELETE", "GET" }.Select(m => (o, m)))
            .Count(p => IsapiWritePolicy.CheckRisky(p.o, p.m, path) is null);
        // users/1: change password (PUT) only — delete of user 1 is impossible; users/{n≥2} adds DELETE.
        Assert.Equal(1, matches);
    }

    [Fact]
    public void RiskyKeywordsAreCheckedCultureIndependently()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.NotNull(IsapiWritePolicy.CheckRisky(RiskyOperation.ChangePassword, "PUT", "ISAPI/Security/ILLEGALLOGINLOCK"));
            Assert.NotNull(IsapiWritePolicy.CheckRisky(RiskyOperation.Reboot, "PUT", "ISAPI/System/ACTIVATE"));
            Assert.NotNull(IsapiWritePolicy.Check("ISAPI/Event/triggers/ILLACCESS"));
            Assert.Null(IsapiWritePolicy.CheckRisky(RiskyOperation.Reboot, "PUT", "ISAPI/System/reboot"));
            Assert.Null(IsapiWritePolicy.Check("ISAPI/Event/triggers/illaccess"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previous;
        }
    }

    // ================================================================ bodies

    [Theory]
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password><userLevel>Operator</userLevel><enabled>false</enabled></User>")]
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password><userLevel>Operator</userLevel><bondIpAddressList><bondIpAddress><ipAddress>1.2.3.4</ipAddress></bondIpAddress></bondIpAddressList></User>")]
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password><userLevel>Operator</userLevel><UserPermission /></User>")]
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password><userLevel>Administrator</userLevel></User>")]  // new admin
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password></User>")]                                      // no level
    [InlineData("<UserList><User><id>2</id></User></UserList>")]
    [InlineData("<User><id>2</id><userName>a</userName><password>x</password><userLevel>Operator</userLevel><verificationCode>A</verificationCode></User>")]
    public void DeniesUserBodiesOutsideTheVocabulary(string xml)
    {
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.AddUser, XDocument.Parse(xml)));
    }

    [Fact]
    public void AllowsAPasswordOnlyInUserBodies()
    {
        var user = XDocument.Parse("<User><id>1</id><userName>admin</userName><password>N3w-pass</password><userLevel>Administrator</userLevel><loginPassword>old</loginPassword></User>");
        IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.ChangePassword, user);
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureBodyAllowed(user));
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.NetworkAddress, user));
    }

    [Theory]
    [InlineData(RiskyOperation.Reboot)]
    [InlineData(RiskyOperation.FormatDisk)]
    [InlineData(RiskyOperation.DeleteUser)]
    public void BodilessOperationsRefuseABody(RiskyOperation operation)
    {
        IsapiWritePolicy.EnsureRiskyBodyAllowed(operation, null);
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(operation, XDocument.Parse("<x/>")));
    }

    [Theory]
    [InlineData("<IPAddress><ipAddress>1.2.3.4</ipAddress><password>x</password></IPAddress>")]
    [InlineData("<IPAddress><ipAddress>1.2.3.4</ipAddress><EZVIZ><enabled>true</enabled></EZVIZ></IPAddress>")]
    [InlineData("<IPAddress><streamEncrypteEnabled>false</streamEncrypteEnabled></IPAddress>")]
    [InlineData("<NetworkInterface><IPAddress><ipAddress>1.2.3.4</ipAddress></IPAddress></NetworkInterface>")]
    public void DeniesNetworkBodiesWithSecretsOrTheWrongDocument(string xml)
    {
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureRiskyBodyAllowed(RiskyOperation.NetworkAddress, XDocument.Parse(xml)));
    }

    [Fact]
    public void OrdinaryBodiesNeverCarryACameraConnectionRecord()
    {
        var record = XDocument.Parse("<InputProxyChannel><id>1</id><name>X</name><sourceInputPortDescriptor><ipAddress>1.2.3.4</ipAddress></sourceInputPortDescriptor></InputProxyChannel>");
        Assert.Throws<IsapiWriteDeniedException>(() => IsapiWritePolicy.EnsureBodyAllowed(record));
    }

    // ================================================================ confirmation token

    sealed class Answer(string? typed) : IRiskyConfirmation
    {
        public int Asked { get; private set; }
        public RiskyWriteRequest? Shown { get; private set; }

        public string? Ask(RiskyWriteRequest request)
        {
            Asked++;
            Shown = request;
            return typed;
        }
    }

    static HddInfo Disk => new(2, "hdd2", "SATA", "ok", 7630885, 0);

    [Fact]
    public void CancelGivesNoToken()
    {
        var answer = new Answer(null);
        Assert.Null(ConfirmedRiskyWrite.Confirm(RiskyWrites.Reboot("NVR"), answer));
        Assert.Equal(1, answer.Asked);
    }

    [Theory]
    [InlineData("BİÇİMLENDİR", true)]
    [InlineData("  BİÇİMLENDİR ", true)]
    [InlineData("biçimlendir", true)]      // Turkish upper-casing: i → İ
    [InlineData("BICIMLENDIR", false)]
    [InlineData("BİÇİMLENDİ", false)]
    [InlineData("EVET", false)]
    [InlineData("", false)]
    public void FormatNeedsTheTypedWord(string typed, bool ok)
    {
        var token = ConfirmedRiskyWrite.Confirm(RiskyWrites.FormatDisk(Disk), new Answer(typed));
        Assert.Equal(ok, token is not null);
    }

    [Fact]
    public void FormatRequestShowsTheDiskAndItsCapacity()
    {
        var request = RiskyWrites.FormatDisk(Disk);
        Assert.Equal("ISAPI/ContentMgmt/Storage/hdd/2/format", request.Path);
        Assert.Equal("PUT", request.Method);
        Assert.Equal(RiskyWrites.FormatWord, request.RequiredWord);
        Assert.Contains(request.Changes, l => l.Contains("hdd2", StringComparison.Ordinal));
        Assert.Contains(request.Changes, l => l.Contains("7,28 TB", StringComparison.Ordinal));
        Assert.Contains("TÜM kayıtlar", request.Warning, StringComparison.Ordinal);
        Assert.False(request.HasBody);
    }

    [Fact]
    public async Task TokenIsUsableOnceAndSendsExactlyTheConfirmedRequest()
    {
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var token = ConfirmedRiskyWrite.Confirm(RiskyWrites.Reboot("NVR"), new Answer(""))!;

        var status = await client.SendRiskyAsync(token);

        Assert.True(status.IsOk);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("PUT", sent.Method);
        Assert.Equal("/ISAPI/System/reboot", sent.Path);
        Assert.Null(sent.Body);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(token));
        Assert.Single(handler.Requests); // the second use sent nothing
    }

    [Fact]
    public async Task ExpiredTokenSendsNothing()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var token = ConfirmedRiskyWrite.Confirm(RiskyWrites.Reboot("NVR"), new Answer(""), () => now)!;
        now += ConfirmedRiskyWrite.Lifetime + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.SendRiskyAsync(token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(RiskyPaths))]
    public async Task OrdinaryPutCannotReachARiskyPath(string path)
    {
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() => client.PutConfigAsync(path, XDocument.Parse("<x/>")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UserBodiesAreSentAsBuiltAndDeleteHasNone()
    {
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("https://192.0.2.1/"), handler);
        var admin = new DeviceUser(1, "admin", "Administrator");
        var operatorUser = new DeviceUser(3, "kapi", "Operator");

        await client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.ChangePassword(admin, "Yeni-Sifre9", "Eski-Sifre1", true), new Answer(""))!);
        await client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.AddUser(4, "izle", UserLevel.Viewer, "Izle-1234", "Eski-Sifre1"), new Answer(""))!);
        await client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.DeleteUser(operatorUser), new Answer(""))!);

        Assert.Equal(["PUT /ISAPI/Security/users/1", "POST /ISAPI/Security/users", "DELETE /ISAPI/Security/users/3"],
            handler.Requests.Select(r => $"{r.Method} {r.Path}").ToList());
        var change = XDocument.Parse(handler.Requests[0].Body!).Root!;
        Assert.Equal(["id", "userName", "password", "userLevel", "loginPassword"], change.Elements().Select(e => e.Name.LocalName).ToList());
        Assert.Equal("Administrator", change.Elements().First(e => e.Name.LocalName == "userLevel").Value); // level unchanged
        var add = XDocument.Parse(handler.Requests[1].Body!).Root!;
        Assert.Equal("4", add.Elements().First(e => e.Name.LocalName == "id").Value);
        Assert.Equal("Viewer", add.Elements().First(e => e.Name.LocalName == "userLevel").Value);
        Assert.Null(handler.Requests[2].Body);
    }

    [Fact]
    public async Task PasswordsAreNeverSentOverPlainHttp()
    {
        var handler = new Recorder(_ => Ok());
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);
        var admin = new DeviceUser(1, "admin", "Administrator");

        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() =>
            client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.ChangePassword(admin, "Yeni-Sifre9", "Eski-Sifre1", true), new Answer(""))!));
        await Assert.ThrowsAsync<IsapiWriteDeniedException>(() =>
            client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.AddUser(2, "izle", UserLevel.Viewer, "Izle-1234", "Eski-Sifre1"), new Answer(""))!));
        Assert.Empty(handler.Requests);

        // Bodies without a password (delete) may use HTTP.
        await client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.DeleteUser(new DeviceUser(2, "izle", "Viewer")), new Answer(""))!);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ConfirmationLinesNeverShowAPassword()
    {
        var admin = new DeviceUser(1, "admin", "Administrator");
        var requests = new[]
        {
            RiskyWrites.ChangePassword(admin, "Yeni-Sifre9", "Eski-Sifre1", true),
            RiskyWrites.AddUser(2, "izle", UserLevel.Viewer, "Izle-1234", "Eski-Sifre1"),
        };
        foreach (var request in requests)
        {
            var shown = string.Join("\n", [request.Title, request.Warning, .. request.Changes]);
            Assert.DoesNotContain("Yeni-Sifre9", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("Izle-1234", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("Eski-Sifre1", shown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AdminCanNeitherBeDeletedNorAddedAsAdministrator()
    {
        Assert.Throws<IsapiWriteDeniedException>(() => RiskyWrites.DeleteUser(new DeviceUser(1, "admin", "Administrator")));
        Assert.Throws<IsapiWriteDeniedException>(() => RiskyWrites.DeleteUser(new DeviceUser(5, "ikinci", "Administrator")));
        Assert.Throws<IsapiWriteDeniedException>(() => RiskyWrites.AddUser(2, "x", UserLevel.Administrator, "Abcdefg1", null));
    }

    [Fact]
    public async Task ResponseStatusOfARiskyWriteIsReturned()
    {
        var handler = new Recorder(_ => Xml(HttpStatusCode.Forbidden,
            "<ResponseStatus><statusCode>4</statusCode><statusString>Invalid Operation</statusString><subStatusCode>notSupport</subStatusCode></ResponseStatus>"));
        using var client = new IsapiClient(new Uri("http://192.0.2.1/"), handler);

        var status = await client.SendRiskyAsync(ConfirmedRiskyWrite.Confirm(RiskyWrites.Reboot("NVR"), new Answer(""))!);

        Assert.False(status.IsOk);
        Assert.Contains("desteklemiyor", status.Describe(), StringComparison.Ordinal);
    }

    // ---- in-memory handler ----

    static HttpResponseMessage Ok() =>
        Xml(HttpStatusCode.OK, "<ResponseStatus><statusCode>1</statusCode><statusString>OK</statusString><subStatusCode>ok</subStatusCode></ResponseStatus>");

    static HttpResponseMessage Xml(HttpStatusCode status, string xml) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    sealed record Sent(string Method, string Path, string? Body);

    sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return answer(request);
        }
    }
}
