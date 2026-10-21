using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// HTTPS certificate pinning (trust on first use, security review M1): the real <see cref="IsapiClient"/> against a TLS
/// <see cref="LoopbackServer"/> on 127.0.0.1, each test with its own <see cref="CertificateTrust"/> (never the shared one).
/// </summary>
public class CertificatePinningTests
{
    const string DeviceInfoXml = "<DeviceInfo><deviceName>NVR</deviceName><model>DS-8632NI-I8</model></DeviceInfo>";

    static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(3));

    static X509Certificate2 SelfSigned(RSA key, string cn, int years = 10, int startDaysAgo = 1)
    {
        var request = new CertificateRequest($"CN={cn}, C=CN", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-startDaysAgo), DateTimeOffset.UtcNow.AddYears(years));
        // A TLS server on Windows needs the key in a key container: load it back from PKCS#12.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    static Device Https(LoopbackServer server, CertificatePin? pin = null) => new()
    {
        Host = "127.0.0.1", HttpPort = server.Port, UseHttps = true, UserName = "admin", Password = "s3cret-Pw", Certificate = pin,
    };

    static string Ok(string body = DeviceInfoXml) => LoopbackServer.Answer(200, "OK", body, "Content-Type: application/xml");

    static CertificatePin PinOf(X509Certificate2 cert) => CertificatePin.From(cert, Now);

    // ---- fingerprints ----

    [Fact]
    public void SpkiHashSurvivesReissueWithTheSameKey()
    {
        using var key = RSA.Create(2048);
        using var first = SelfSigned(key, "recorder", years: 10);
        using var reissued = SelfSigned(key, "recorder-renewed", years: 2, startDaysAgo: 0);
        using var other = RSA.Create(2048);
        using var foreign = SelfSigned(other, "recorder");

        var a = PinOf(first);
        var b = PinOf(reissued);
        var c = PinOf(foreign);

        Assert.Equal(a.SpkiSha256, b.SpkiSha256);              // same key: same pin
        Assert.NotEqual(a.CertificateSha256, b.CertificateSha256); // but another certificate
        Assert.True(a.SameKey(b));
        Assert.NotEqual(a.SpkiSha256, c.SpkiSha256);
        Assert.False(a.SameKey(c));
        Assert.Equal(64, a.SpkiSha256.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.PublicKey.ExportSubjectPublicKeyInfo())), a.SpkiSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first.RawData)), a.CertificateSha256);
        Assert.Equal("CN=recorder, C=CN", a.Subject);
        Assert.Equal(a.Subject, a.Issuer);
    }

    [Fact]
    public void PinFromPlainX509CertificateIsTheSame()
    {
        using var key = RSA.Create(2048);
        using var cert = SelfSigned(key, "recorder");
#pragma warning disable SYSLIB0057 // the TLS callback hands an X509Certificate: test that path too
        using var plain = new X509Certificate(cert.RawData);
#pragma warning restore SYSLIB0057
        Assert.Equal(PinOf(cert).SpkiSha256, CertificatePin.From(plain, Now).SpkiSha256);
    }

    [Fact]
    public void EmptyPinNeverMatches()
    {
        Assert.False(new CertificatePin().SameKey(new CertificatePin()));
        Assert.True(new CertificatePin { SpkiSha256 = "ABCD" }.SameKey(new CertificatePin { SpkiSha256 = "abcd" }));
    }

    [Fact]
    public void FingerprintTexts()
    {
        var hex = "2BAB3C0A" + new string('0', 56);
        Assert.Equal("2B:AB:3C:0A:00:00:00:00…", CertificateFingerprint.Short(hex));
        Assert.Equal(32, CertificateFingerprint.Format(hex).Split(':').Length);
        Assert.Equal("AB:CD", CertificateFingerprint.Short("abcd"));

        var pin = new CertificatePin
        {
            SpkiSha256 = hex, CertificateSha256 = hex,
            NotBefore = new DateTimeOffset(2024, 10, 21, 12, 0, 0, TimeSpan.Zero), NotAfter = new DateTimeOffset(2034, 10, 19, 12, 0, 0, TimeSpan.Zero),
        };
        Assert.Equal("21.10.2024 – 19.10.2034", CertificateText.Validity(pin));
        Assert.Equal("Sertifika parmak izi: 2B:AB:3C:0A:00:00:00:00… (ilk bağlantıda kaydedildi)", CertificateText.EditorLine(true, pin));
        Assert.Contains("henüz kaydedilmedi", CertificateText.EditorLine(true, null));
        Assert.Contains("HTTPS kapalı", CertificateText.EditorLine(false, pin));
        Assert.Contains("onayınızla", CertificateText.EditorLine(true, pin.With(CertificatePinSource.Confirmed, Now)));
    }

    [Fact]
    public void PinIsStoredInSettingsAsPlainJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-pin-{Guid.NewGuid():N}.json");
        try
        {
            var pin = new CertificatePin
            {
                SpkiSha256 = new string('A', 64), CertificateSha256 = new string('B', 64), Subject = "CN=x", Issuer = "CN=x",
                NotBefore = Now, NotAfter = Now.AddYears(10), PinnedAt = Now, Source = CertificatePinSource.Confirmed,
            };
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Devices = [new Device { Host = "192.0.2.1", UseHttps = true, Password = "pw", Certificate = pin }] });

            var json = File.ReadAllText(path);
            Assert.Contains(new string('A', 64), json);           // not secret: no DPAPI
            Assert.Contains("\"Confirmed\"", json);               // enum as text
            var loaded = store.Load().Devices.Single().Certificate!;
            Assert.Equal(pin.SpkiSha256, loaded.SpkiSha256);
            Assert.Equal(pin.Source, loaded.Source);
            Assert.Equal(pin.NotAfter, loaded.NotAfter);

            store.Save(new AppSettings { Devices = [new Device { Host = "192.0.2.1" }] });
            Assert.DoesNotContain("Certificate", File.ReadAllText(path)); // null pin is not written
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- the real client over TLS ----

    [Fact]
    public async Task FirstUsePinsTheCertificate()
    {
        using var key = RSA.Create(2048);
        using var cert = SelfSigned(key, "recorder");
        using var server = new LoopbackServer(_ => Ok(), cert);
        var trust = new CertificateTrust(() => Now);
        var changed = 0;
        trust.PinChanged += _ => changed++;
        var device = Https(server);

        using (var client = new IsapiClient(device, null, trust))
        {
            Assert.Equal("NVR", (await client.GetDeviceInfoAsync()).Name);
        }

        Assert.NotNull(device.Certificate);
        Assert.Equal(PinOf(cert).SpkiSha256, device.Certificate!.SpkiSha256);
        Assert.Equal(CertificatePinSource.FirstUse, device.Certificate.Source);
        Assert.Equal(Now, device.Certificate.PinnedAt);
        Assert.Equal(1, changed);

        // A second client (new connection) checks against the stored pin and keeps it.
        using (var again = new IsapiClient(device, null, trust))
        {
            await again.GetDeviceInfoAsync();
        }
        Assert.Equal(1, changed);
        Assert.Equal(2, server.Handshakes);
    }

    [Fact]
    public async Task MatchingPinConnects()
    {
        using var key = RSA.Create(2048);
        using var cert = SelfSigned(key, "recorder");
        using var server = new LoopbackServer(_ => Ok(), cert);
        var trust = new CertificateTrust(() => Now);
        var pin = PinOf(cert);
        var device = Https(server, pin);

        using var client = new IsapiClient(device, null, trust);
        await client.GetDeviceInfoAsync();

        Assert.Same(pin, device.Certificate);
        Assert.Equal(0, trust.QuestionsAsked);
    }

    [Fact]
    public async Task ReissuedCertificateWithSameKeyStillMatches()
    {
        using var key = RSA.Create(2048);
        using var old = SelfSigned(key, "recorder", years: 10);
        using var renewed = SelfSigned(key, "recorder", years: 5, startDaysAgo: 0);
        using var server = new LoopbackServer(_ => Ok(), renewed);
        var trust = new CertificateTrust(() => Now);
        var device = Https(server, PinOf(old));

        using var client = new IsapiClient(device, null, trust);
        await client.GetDeviceInfoAsync();
        Assert.Equal(0, trust.QuestionsAsked);
    }

    [Fact]
    public async Task MismatchFailsBeforeAnyRequestOrCredentials()
    {
        using var pinnedKey = RSA.Create(2048);
        using var pinnedCert = SelfSigned(pinnedKey, "recorder");
        using var otherKey = RSA.Create(2048);
        using var impostor = SelfSigned(otherKey, "recorder");
        // The impostor asks for Digest like a real recorder would: it must not even get the request line.
        using var server = new LoopbackServer(r => r.Headers.ContainsKey("Authorization")
            ? Ok()
            : LoopbackServer.Answer(401, "Unauthorized", "", "WWW-Authenticate: Digest realm=\"nvr\", nonce=\"6b0d1d2e\", qop=\"auth\""), impostor);
        var trust = new CertificateTrust(() => Now); // no Prompt: refused without a question
        var pin = PinOf(pinnedCert);
        var device = Https(server, pin);

        using var client = new IsapiClient(device, null, trust);
        var error = await Assert.ThrowsAsync<CertificateChangedException>(() => client.GetDeviceInfoAsync());

        // No HTTP at all, so no Authorization header. (With TLS 1.3 the server may count its side of the handshake as
        // done before the client checks the certificate and aborts; the client then sends nothing.)
        Assert.Empty(server.Requests);
        Assert.False(error.ConnectionFailed);               // never "HTTP ile dene"
        Assert.False(HttpsPort.OfferHttpFallback(true, client.Answered, error));
        Assert.Same(pin, error.Old);
        Assert.Equal(PinOf(impostor).SpkiSha256, error.Presented.SpkiSha256);
        Assert.Equal("CN=recorder, C=CN", error.Presented.Subject);
        Assert.NotNull(error.Presented.NotAfter);
        Assert.Contains(CertificateText.NotVerified, error.Message);
        Assert.DoesNotContain("s3cret", error.Message);
        Assert.Same(pin, device.Certificate);               // the pin is kept
        Assert.IsAssignableFrom<IsapiException>(error);     // existing error paths show its message
    }

    [Fact]
    public async Task MismatchOnWriteSendsNothing()
    {
        using var pinnedKey = RSA.Create(2048);
        using var pinnedCert = SelfSigned(pinnedKey, "recorder");
        using var otherKey = RSA.Create(2048);
        using var impostor = SelfSigned(otherKey, "recorder");
        using var server = new LoopbackServer(_ => Ok("<ResponseStatus><statusCode>1</statusCode></ResponseStatus>"), impostor);
        var device = Https(server, PinOf(pinnedCert));

        using var client = new IsapiClient(device, null, new CertificateTrust(() => Now));
        await Assert.ThrowsAsync<CertificateChangedException>(() =>
            client.PutConfigAsync(IsapiPaths.Motion(1), System.Xml.Linq.XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>")));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TrustedNewCertificateReplacesThePinAndRetries()
    {
        using var oldKey = RSA.Create(2048);
        using var oldCert = SelfSigned(oldKey, "recorder");
        using var newKey = RSA.Create(2048);
        using var newCert = SelfSigned(newKey, "recorder-after-reset");
        using var server = new LoopbackServer(_ => Ok(), newCert);
        var trust = new CertificateTrust(() => Now);
        CertificatePin? askedOld = null, askedNew = null;
        trust.Prompt = (_, o, n) =>
        {
            askedOld = o;
            askedNew = n;
            return Task.FromResult(true);
        };
        var changed = 0;
        trust.PinChanged += _ => changed++;
        var oldPin = PinOf(oldCert);
        var device = Https(server, oldPin);

        using var client = new IsapiClient(device, null, trust);
        Assert.Equal("NVR", (await client.GetDeviceInfoAsync()).Name);
        Assert.False(client.CertificateRefused);

        Assert.Same(oldPin, askedOld);
        Assert.Equal(PinOf(newCert).SpkiSha256, askedNew!.SpkiSha256);
        Assert.Equal(PinOf(newCert).SpkiSha256, device.Certificate!.SpkiSha256);
        Assert.Equal(CertificatePinSource.Confirmed, device.Certificate.Source);
        Assert.Single(server.Requests);                     // only the retry reached the server
        Assert.Equal(1, trust.QuestionsAsked);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task TrustedRetryResendsTheBody()
    {
        using var oldKey = RSA.Create(2048);
        using var oldCert = SelfSigned(oldKey, "recorder");
        using var newKey = RSA.Create(2048);
        using var newCert = SelfSigned(newKey, "recorder");
        using var server = new LoopbackServer(_ => Ok("<ResponseStatus><statusCode>1</statusCode><subStatusCode>ok</subStatusCode></ResponseStatus>"), newCert);
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => Task.FromResult(true) };
        var device = Https(server, PinOf(oldCert));

        using var client = new IsapiClient(device, null, trust);
        var status = await client.PutConfigAsync(IsapiPaths.Motion(1),
            System.Xml.Linq.XDocument.Parse("<MotionDetection><enabled>true</enabled></MotionDetection>"));

        Assert.True(status.IsOk);
        var request = Assert.Single(server.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Contains("<enabled>true</enabled>", request.Body);
        Assert.StartsWith("application/xml", request.Headers["Content-Type"]);
    }

    [Fact]
    public async Task DeclinedCertificateFailsQuietlyAfterwards()
    {
        using var oldKey = RSA.Create(2048);
        using var oldCert = SelfSigned(oldKey, "recorder");
        using var newKey = RSA.Create(2048);
        using var newCert = SelfSigned(newKey, "recorder");
        using var server = new LoopbackServer(_ => Ok(), newCert);
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => Task.FromResult(false) };
        var oldPin = PinOf(oldCert);
        var device = Https(server, oldPin);

        using (var client = new IsapiClient(device, null, trust))
        {
            await Assert.ThrowsAsync<CertificateChangedException>(() => client.GetDeviceInfoAsync());
        }
        using (var later = new IsapiClient(device, null, trust))
        {
            await Assert.ThrowsAsync<CertificateChangedException>(() => later.GetDeviceInfoAsync());
            Assert.True(later.CertificateRefused);          // pages that show "okunamadı" can say why
            Assert.False(later.Answered);
        }

        Assert.Equal(1, trust.QuestionsAsked);              // asked once, then quiet
        Assert.True(trust.IsDeclined(device.Id, PinOf(newCert)));
        Assert.Same(oldPin, device.Certificate);
        Assert.Empty(server.Requests);

        // "Bağlantıyı test et" asks again.
        trust.ForgetDeclined(device.Id);
        using (var test = new IsapiClient(device, null, trust))
        {
            await Assert.ThrowsAsync<CertificateChangedException>(() => test.GetDeviceInfoAsync());
        }
        Assert.Equal(2, trust.QuestionsAsked);
    }

    [Fact]
    public async Task ResetForgetsThePinAndTheNextConnectionPinsAgain()
    {
        using var oldKey = RSA.Create(2048);
        using var oldCert = SelfSigned(oldKey, "recorder");
        using var newKey = RSA.Create(2048);
        using var newCert = SelfSigned(newKey, "recorder");
        using var server = new LoopbackServer(_ => Ok(), newCert);
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => Task.FromResult(false) };
        var device = Https(server, PinOf(oldCert));
        using (var client = new IsapiClient(device, null, trust))
        {
            await Assert.ThrowsAsync<CertificateChangedException>(() => client.GetDeviceInfoAsync());
        }
        var changed = new List<Device>();
        trust.PinChanged += changed.Add;

        trust.Reset(device);
        Assert.Null(device.Certificate);
        Assert.False(trust.IsDeclined(device.Id, PinOf(newCert)));

        using (var client = new IsapiClient(device, null, trust))
        {
            await client.GetDeviceInfoAsync();
        }
        Assert.Equal(PinOf(newCert).SpkiSha256, device.Certificate!.SpkiSha256);
        Assert.Equal(CertificatePinSource.FirstUse, device.Certificate.Source);
        Assert.Equal([device, device], changed);            // reset + new pin: both saved
        Assert.Equal(1, trust.QuestionsAsked);
    }

    [Fact]
    public async Task ParallelRequestsShareOneQuestion()
    {
        using var oldKey = RSA.Create(2048);
        using var oldCert = SelfSigned(oldKey, "recorder");
        using var newKey = RSA.Create(2048);
        using var newCert = SelfSigned(newKey, "recorder");
        using var server = new LoopbackServer(_ => Ok(), newCert);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => answer.Task };
        var device = Https(server, PinOf(oldCert));

        var clients = Enumerable.Range(0, 4).Select(_ => new IsapiClient(device, null, trust)).ToList();
        try
        {
            var reads = clients.Select(c => c.GetDeviceInfoAsync()).ToList();
            await WaitUntil(() => trust.QuestionsAsked == 1);
            await Task.Delay(100);
            Assert.All(reads, r => Assert.False(r.IsCompleted)); // all wait for the one answer
            answer.SetResult(true);
            await Task.WhenAll(reads);
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
        Assert.Equal(1, trust.QuestionsAsked);
        Assert.Equal(4, server.Requests.Count);
    }

    // ---- the trust gate alone (no network) ----

    static CertificatePin Pin(char c) => new() { SpkiSha256 = new string(c, 64), CertificateSha256 = new string(c, 64) };

    [Fact]
    public async Task SameChangedCertificateIsAskedOnce()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => answer.Task };
        var device = new Device { Certificate = Pin('A') };

        var waits = Enumerable.Range(0, 5).Select(_ => trust.ResolveMismatchAsync(device, Pin('B'))).ToList();
        await WaitUntil(() => trust.QuestionsAsked == 1);
        Assert.All(waits, w => Assert.False(w.IsCompleted));
        answer.SetResult(true);

        Assert.All(await Task.WhenAll(waits), Assert.True);
        Assert.Equal(1, trust.QuestionsAsked);
        Assert.Equal(new string('B', 64), device.Certificate!.SpkiSha256);
        Assert.True(await trust.ResolveMismatchAsync(device, Pin('B')));   // now the pin: no question
        Assert.Equal(1, trust.QuestionsAsked);
    }

    [Fact]
    public async Task OnlyOneQuestionAtATime()
    {
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new List<Device>();
        var trust = new CertificateTrust(() => Now)
        {
            Prompt = (d, _, _) =>
            {
                lock (asked)
                {
                    asked.Add(d);
                }
                return asked.Count == 1 ? first.Task : Task.FromResult(false);
            },
        };
        var one = new Device { Name = "1", Certificate = Pin('A') };
        var two = new Device { Name = "2", Certificate = Pin('C') };

        var a = trust.ResolveMismatchAsync(one, Pin('B'));
        await WaitUntil(() => trust.QuestionsAsked == 1);
        var b = trust.ResolveMismatchAsync(two, Pin('D'));
        await Task.Delay(100);
        Assert.Equal([one], asked);                         // the second device waits for the first dialog
        first.SetResult(false);

        Assert.False(await a);
        Assert.False(await b);
        Assert.Equal([one, two], asked);
    }

    [Fact]
    public async Task NoPromptRefusesAndAFailingPromptNeverTrusts()
    {
        var device = new Device { Certificate = Pin('A') };
        Assert.False(await new CertificateTrust(() => Now).ResolveMismatchAsync(device, Pin('B')));

        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => throw new InvalidOperationException("no window") };
        Assert.False(await trust.ResolveMismatchAsync(device, Pin('B')));
        Assert.Equal(new string('A', 64), device.Certificate!.SpkiSha256);
    }

    [Fact]
    public async Task ResetWhileWaitingPinsAgainWithoutQuestion()
    {
        var trust = new CertificateTrust(() => Now) { Prompt = (_, _, _) => Task.FromResult(false) };
        var device = new Device { Certificate = Pin('A') };
        trust.Reset(device);

        Assert.True(await trust.ResolveMismatchAsync(device, Pin('B')));
        Assert.Equal(new string('B', 64), device.Certificate!.SpkiSha256);
        Assert.Equal(0, trust.QuestionsAsked);
    }

    [Fact]
    public void FirstUseKeepsAnExistingPin()
    {
        var trust = new CertificateTrust(() => Now);
        var device = new Device();
        Assert.True(trust.PinFirstUse(device, Pin('A')));
        Assert.False(trust.PinFirstUse(device, Pin('B')));
        Assert.Equal(new string('A', 64), device.Certificate!.SpkiSha256);
        Assert.Equal(Now, device.Certificate.PinnedAt);
    }

    [Fact]
    public void HttpDevicesAreNotPinned()
    {
        // The pin is an HTTPS property: an HTTP client never stores one (nothing to pin).
        var device = new Device { Host = "192.0.2.1", UseHttps = false };
        using var client = new IsapiClient(device, null, new CertificateTrust(() => Now));
        Assert.Null(device.Certificate);
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }
}
