using System.Security.Cryptography;
using System.Text;
using SPVision.Core;
using SPVision.Core.Updates;

namespace SPVision.Core.Tests;

/// <summary>In-app updater: versions, manifest, signature, decision rules, file check, release notes, texts.</summary>
public sealed class UpdateTests
{
    // ---- helpers ----

    static readonly string Hash = new('a', 64);

    static UpdateManifest Manifest(string version = "1.2.0", bool mandatory = false, string min = "1.0.0", long size = 1234,
        string asset = "SPVision-Kurulum-1.2.0.exe", string sha256 = "", string notes = "Hata düzeltmeleri.\n- Oynatma daha hızlı") => new()
        {
            Version = AppVersion.ParseStrict(version),
            Mandatory = mandatory,
            MinSupportedVersion = AppVersion.ParseStrict(min),
            Notes = notes,
            Asset = asset,
            Sha256 = sha256.Length == 0 ? Hash : sha256,
            Size = size,
            Published = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero),
        };

    static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    const string ValidJson = """
        { "version": "1.2.0", "mandatory": false, "minSupportedVersion": "1.0.0", "notes": "Türkçe notlar",
          "asset": "SPVision-Kurulum-1.2.0.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "size": 1234, "published": "2026-10-21T12:00:00Z" }
        """;

    static string Replace(string field, string newValue)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(ValidJson)!.AsObject();
        doc[field] = System.Text.Json.Nodes.JsonNode.Parse(newValue);
        return doc.ToJsonString();
    }

    static string Without(string field)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(ValidJson)!.AsObject();
        doc.Remove(field);
        return doc.ToJsonString();
    }

    static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    static byte[] SignatureFile(byte[] manifest, ECDsa key) =>
        Encoding.ASCII.GetBytes(UpdateSignature.EncodeSignatureFile(UpdateSignature.Sign(manifest, key)));

    // ---- AppVersion ----

    [Theory]
    [InlineData("1.2.10", "1.2.9")]
    [InlineData("1.10.0", "1.9.99")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("0.0.1", "0.0.0")]
    public void VersionsCompareNumerically(string newer, string older)
    {
        Assert.True(AppVersion.ParseStrict(newer) > AppVersion.ParseStrict(older));
        Assert.True(AppVersion.ParseStrict(older) < AppVersion.ParseStrict(newer));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("10.20.300")]
    public void StrictAcceptsPlainVersions(string text)
    {
        Assert.True(AppVersion.TryParseStrict(text, out var version));
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3+abc")]
    [InlineData("01.2.3")]
    [InlineData("1.-2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1..3")]
    [InlineData("1.2.3456789012")]
    [InlineData("١.٢.٣")] // non-ASCII digits
    public void StrictRejectsEverythingElse(string? text) => Assert.False(AppVersion.TryParseStrict(text, out _));

    [Theory]
    [InlineData("1.0.0+32c81ab118bcfc7e2d0e2db9280f1722f9e1254d", "1.0.0")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("1.2.3-rc.2+build.5", "1.2.3")]
    public void LenientIgnoresPrefixAndSuffixes(string text, string expected)
    {
        Assert.True(AppVersion.TryParseLenient(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Fact]
    public void PreReleaseIsIgnoredInComparison()
    {
        Assert.True(AppVersion.TryParseLenient("1.2.3-beta", out var beta));
        Assert.Equal(AppVersion.ParseStrict("1.2.3"), beta);
        Assert.Equal(0, beta.CompareTo(AppVersion.ParseStrict("1.2.3")));
        Assert.False(AppVersion.TryParseLenient("1.2.3-", out _));
    }

    // ---- Manifest ----

    [Fact]
    public void ParsesValidManifest()
    {
        var manifest = UpdateManifest.Parse(Json(ValidJson));
        Assert.Equal("1.2.0", manifest.Version.ToString());
        Assert.False(manifest.Mandatory);
        Assert.Equal("1.0.0", manifest.MinSupportedVersion.ToString());
        Assert.Equal("Türkçe notlar", manifest.Notes);
        Assert.Equal("SPVision-Kurulum-1.2.0.exe", manifest.Asset);
        Assert.Equal(1234, manifest.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), manifest.Published);
    }

    [Fact]
    public void WrittenManifestReadsBackTheSame()
    {
        var written = Manifest(mandatory: true, notes: "Satır 1\nŞ ğ ı İ ö ç \"tırnak\"").ToJson();
        Assert.All(written, b => Assert.True(b < 0x80)); // plain ASCII bytes, signed as they are
        var read = UpdateManifest.Parse(written);
        Assert.Equal("1.2.0", read.Version.ToString());
        Assert.True(read.Mandatory);
        Assert.Equal("Satır 1\nŞ ğ ı İ ö ç \"tırnak\"", read.Notes);
        Assert.Equal(Hash, read.Sha256);
        Assert.Equal(written, Manifest(mandatory: true, notes: "Satır 1\nŞ ğ ı İ ö ç \"tırnak\"").ToJson()); // deterministic
    }

    [Theory]
    [InlineData("version")]
    [InlineData("mandatory")]
    [InlineData("minSupportedVersion")]
    [InlineData("notes")]
    [InlineData("asset")]
    [InlineData("sha256")]
    [InlineData("size")]
    [InlineData("published")]
    public void MissingFieldIsRejected(string field)
    {
        var ex = Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(Without(field))));
        Assert.Contains(field, ex.Message);
    }

    [Theory]
    [InlineData("version", "\"1.2\"")]
    [InlineData("version", "\"v1.2.0\"")]
    [InlineData("version", "\"1.2.0-beta\"")]
    [InlineData("version", "120")]
    [InlineData("mandatory", "\"true\"")]
    [InlineData("mandatory", "1")]
    [InlineData("mandatory", "null")]
    [InlineData("minSupportedVersion", "\"2.0.0\"")] // above version
    [InlineData("notes", "null")]
    [InlineData("notes", "\"a\\u0000b\"")]
    [InlineData("asset", "\"../SPVision.exe\"")]
    [InlineData("asset", "\"..\\\\SPVision.exe\"")]
    [InlineData("asset", "\"sub/SPVision.exe\"")]
    [InlineData("asset", "\"C:\\\\x.exe\"")]
    [InlineData("asset", "\"SPVision.msi\"")]
    [InlineData("asset", "\".exe\"")]
    [InlineData("asset", "\"a..b.exe\"")]
    [InlineData("asset", "\"SPVision Kurulum.exe\"")]
    [InlineData("sha256", "\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    [InlineData("sha256", "\"abc\"")]
    [InlineData("sha256", "\"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg\"")]
    [InlineData("size", "0")]
    [InlineData("size", "-5")]
    [InlineData("size", "1.5")]
    [InlineData("size", "\"1234\"")]
    [InlineData("size", "1073741825")] // 1 GiB + 1
    [InlineData("published", "\"dün\"")]
    [InlineData("published", "20261021")]
    public void BadFieldIsRejected(string field, string value) =>
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(Replace(field, value))));

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("not json")]
    public void NonObjectIsRejected(string text) =>
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(text)));

    [Fact]
    public void DuplicateFieldIsRejected()
    {
        var text = ValidJson.Replace("\"mandatory\": false,", "\"mandatory\": false, \"mandatory\": true,", StringComparison.Ordinal);
        var ex = Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(text)));
        Assert.Contains("iki kez", ex.Message);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreRejected()
    {
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(ValidJson.Replace("\"size\": 1234,", "\"size\": 1234, // c\n", StringComparison.Ordinal))));
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(ValidJson.TrimEnd().TrimEnd('}') + ", }")));
    }

    [Fact]
    public void OversizedManifestIsRejectedBeforeParsing()
    {
        var notes = new string('x', UpdateManifest.MaxBytes);
        var ex = Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(Replace("notes", $"\"{notes}\""))));
        Assert.Contains("büyük", ex.Message);
    }

    [Fact]
    public void TooLongNotesAreRejected()
    {
        var notes = new string('x', UpdateManifest.MaxNotesLength + 1);
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(Replace("notes", $"\"{notes}\""))));
        Assert.Throws<UpdateManifestException>(() => Manifest(notes: notes).ToJson());
    }

    [Fact]
    public void DeeplyNestedJsonIsRejected()
    {
        var nested = string.Concat(Enumerable.Repeat("[", 10)) + string.Concat(Enumerable.Repeat("]", 10));
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Json(ValidJson.TrimEnd().TrimEnd('}') + $", \"extra\": {nested} }}")));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var text = ValidJson.TrimEnd().TrimEnd('}') + ", \"future\": { \"x\": 1 } }";
        Assert.Equal("1.2.0", UpdateManifest.Parse(Json(text)).Version.ToString());
    }

    [Fact]
    public void TryParseReportsTheError()
    {
        Assert.False(UpdateManifest.TryParse(Json("{}"), out var manifest, out var error));
        Assert.Null(manifest);
        Assert.StartsWith("Güncelleme bilgisi geçersiz", error);
        Assert.True(UpdateManifest.TryParse(Json(ValidJson), out manifest, out error));
        Assert.NotNull(manifest);
        Assert.Null(error);
    }

    // ---- Signature ----

    [Fact]
    public void ValidSignatureVerifies()
    {
        using var key = NewKey();
        var manifest = Manifest().ToJson();
        var signature = SignatureFile(manifest, key);
        Assert.Equal(SignatureCheck.Valid, UpdateSignature.Verify(manifest, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void TamperedManifestFails()
    {
        using var key = NewKey();
        var manifest = Manifest().ToJson();
        var signature = SignatureFile(manifest, key);
        var tampered = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(manifest).Replace("\"mandatory\": false", "\"mandatory\": true ", StringComparison.Ordinal));
        Assert.NotEqual(manifest, tampered);
        Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify(tampered, signature, key.ExportSubjectPublicKeyInfoPem()));
        var flipped = (byte[])manifest.Clone();
        flipped[^3] ^= 1;
        Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify(flipped, signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify([.. manifest, (byte)' '], signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void WrongKeyFails()
    {
        using var signer = NewKey();
        using var other = NewKey();
        var manifest = Manifest().ToJson();
        Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify(manifest, SignatureFile(manifest, signer), other.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData(UpdateTrust.Placeholder)]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("-----BEGIN PUBLIC KEY-----\nbm90IGEga2V5\n-----END PUBLIC KEY-----")]
    public void PlaceholderOrBrokenKeyNeverVerifies(string? publicKey)
    {
        using var key = NewKey();
        var manifest = Manifest().ToJson();
        Assert.False(UpdateSignature.IsConfigured(publicKey));
        Assert.Equal(SignatureCheck.KeyNotConfigured, UpdateSignature.Verify(manifest, SignatureFile(manifest, key), publicKey));
    }

    [Fact]
    public void OtherCurvesAndPrivateKeysAreNotAccepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(UpdateSignature.IsConfigured(p384.ExportSubjectPublicKeyInfoPem()));
        using var rsa = RSA.Create(2048);
        Assert.False(UpdateSignature.IsConfigured(rsa.ExportSubjectPublicKeyInfoPem()));
        using var key = NewKey();
        Assert.False(UpdateSignature.IsConfigured(key.ExportPkcs8PrivateKeyPem())); // a private key is not a configured public key
        Assert.True(UpdateSignature.IsConfigured(key.ExportSubjectPublicKeyInfoPem()));
        Assert.Throws<CryptographicException>(() => UpdateSignature.Sign(Manifest().ToJson(), p384));
    }

    [Fact]
    public void MalformedSignatureFilesFail()
    {
        using var key = NewKey();
        var manifest = Manifest().ToJson();
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var der = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        byte[][] bad =
        [
            [],
            Encoding.ASCII.GetBytes("not base64 !!"),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[63])),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[65])),
            Encoding.ASCII.GetBytes(Convert.ToBase64String(der)),               // DER is not the agreed format
            Encoding.ASCII.GetBytes(new string('A', UpdateSignature.MaxFileBytes + 1)),
            [0xEF, 0xBB, 0xBF, .. SignatureFile(manifest, key)],               // BOM / non-ASCII
        ];
        foreach (var signature in bad)
        {
            Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify(manifest, signature, pem));
        }
        Assert.Equal(SignatureCheck.Invalid, UpdateSignature.Verify([], SignatureFile(manifest, key), pem));
    }

    [Fact]
    public void SignatureFileToleratesSurroundingWhitespace()
    {
        using var key = NewKey();
        var manifest = Manifest().ToJson();
        var text = "  " + Encoding.ASCII.GetString(SignatureFile(manifest, key)).Trim() + "\r\n\r\n";
        Assert.Equal(SignatureCheck.Valid, UpdateSignature.Verify(manifest, Encoding.ASCII.GetBytes(text), key.ExportSubjectPublicKeyInfoPem()));
        Assert.EndsWith("\n", UpdateSignature.EncodeSignatureFile(new byte[64]));
        Assert.Throws<ArgumentException>(() => UpdateSignature.EncodeSignatureFile(new byte[70]));
    }

    // ---- UpdateTrust source rewriting (key generator) ----

    static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPVision.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void KeyGeneratorRewritesOnlyTheMarkedBlockOfTheRealSource()
    {
        var source = File.ReadAllText(RepoFile("src", "SPVision.Core", "Updates", "UpdateTrust.cs"));
        using var key = NewKey();
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var updated = UpdateTrust.WithPublicKey(source, pem);

        static int Declarations(string text) =>
            text.Split('\n').Count(l => l.TrimStart().StartsWith("public const string PublicKeyPem", StringComparison.Ordinal));
        Assert.Equal(1, Declarations(source));
        Assert.Equal(1, Declarations(updated));
        Assert.Contains("// BEGIN UPDATE PUBLIC KEY", updated);
        Assert.Contains("// END UPDATE PUBLIC KEY", updated);
        Assert.Contains("-----BEGIN PUBLIC KEY-----", updated);
        var outside = (string text) => text[..text.IndexOf("// BEGIN UPDATE PUBLIC KEY", StringComparison.Ordinal)] +
            text[text.IndexOf("// END UPDATE PUBLIC KEY", StringComparison.Ordinal)..];
        Assert.Equal(outside(source), outside(updated));

        // The raw string literal holds exactly the PEM (C# strips the common indentation).
        var start = updated.IndexOf("\"\"\"", StringComparison.Ordinal) + 3;
        var end = updated.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        var literal = string.Join("\n", updated[start..end].Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0));
        Assert.Equal(pem.Replace("\r", "", StringComparison.Ordinal).Trim(), literal);
        Assert.True(UpdateSignature.IsConfigured(literal));

        using var next = NewKey();
        var again = UpdateTrust.WithPublicKey(updated, next.ExportSubjectPublicKeyInfoPem()); // key rotation rewrites the same block
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(again, "-----BEGIN PUBLIC KEY-----"));
        Assert.Equal(1, Declarations(again));
        Assert.Equal(outside(source), outside(again));
    }

    [Fact]
    public void KeyGeneratorRefusesBadInput()
    {
        using var key = NewKey();
        var pem = key.ExportSubjectPublicKeyInfoPem();
        Assert.Throws<InvalidOperationException>(() => UpdateTrust.WithPublicKey("class X {}", pem));
        Assert.Throws<ArgumentException>(() => UpdateTrust.WithPublicKey(
            "// BEGIN UPDATE PUBLIC KEY\nx\n// END UPDATE PUBLIC KEY", key.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<InvalidOperationException>(() => UpdateTrust.WithPublicKey(
            "// END UPDATE PUBLIC KEY\nx\n// BEGIN UPDATE PUBLIC KEY", pem));
    }

    [Fact]
    public void PlaceholderIsNeverAKey() => Assert.False(UpdateSignature.IsConfigured(UpdateTrust.Placeholder));

    // ---- Decision ----

    static AppVersion V(string text) => AppVersion.ParseStrict(text);

    [Fact]
    public void SameOrOlderReleaseIsNoUpdate()
    {
        Assert.Equal(UpdateKind.None, UpdatePolicy.Decide(V("1.2.0"), Manifest("1.2.0", mandatory: true)));
        Assert.Equal(UpdateKind.None, UpdatePolicy.Decide(V("1.3.0"), Manifest("1.2.0", mandatory: true, min: "1.2.0"))); // no downgrade
    }

    [Fact]
    public void NewerReleaseIsOptionalUnlessMarked()
    {
        Assert.Equal(UpdateKind.Optional, UpdatePolicy.Decide(V("1.1.0"), Manifest("1.2.0")));
        Assert.Equal(UpdateKind.Mandatory, UpdatePolicy.Decide(V("1.1.0"), Manifest("1.2.0", mandatory: true)));
    }

    [Fact]
    public void BelowMinSupportedIsMandatory()
    {
        Assert.Equal(UpdateKind.Mandatory, UpdatePolicy.Decide(V("1.0.9"), Manifest("1.2.0", min: "1.1.0")));
        Assert.Equal(UpdateKind.Optional, UpdatePolicy.Decide(V("1.1.0"), Manifest("1.2.0", min: "1.1.0")));
        Assert.Equal(UpdateKind.Mandatory, UpdatePolicy.Decide(V("1.2.9"), Manifest("1.2.10", min: "1.2.10")));
    }

    [Fact]
    public void FailedCheckWithoutMemoryNeverBlocks()
    {
        var resolution = UpdatePolicy.Resolve(V("1.0.0"), null, null);
        Assert.Equal(UpdateKind.None, resolution.Kind);
        Assert.Null(resolution.Target);
        Assert.Null(resolution.KeepMandatory);
    }

    [Fact]
    public void FreshMandatoryIsRemembered()
    {
        var fresh = Manifest("1.2.0", mandatory: true);
        var resolution = UpdatePolicy.Resolve(V("1.0.0"), fresh, null);
        Assert.Equal(UpdateKind.Mandatory, resolution.Kind);
        Assert.Same(fresh, resolution.Target);
        Assert.Same(fresh, resolution.KeepMandatory);
    }

    [Fact]
    public void RememberedMandatoryIsEnforcedWhenTheCheckFails()
    {
        var remembered = Manifest("1.2.0", mandatory: true);
        var resolution = UpdatePolicy.Resolve(V("1.0.0"), null, remembered);
        Assert.Equal(UpdateKind.Mandatory, resolution.Kind);
        Assert.Same(remembered, resolution.Target);
        Assert.Same(remembered, resolution.KeepMandatory);
    }

    [Fact]
    public void RememberedMandatoryOutlivesALaterOptionalRelease()
    {
        var remembered = Manifest("1.2.0", mandatory: true);
        var newerOptional = Manifest("1.3.0");
        var resolution = UpdatePolicy.Resolve(V("1.0.0"), newerOptional, remembered);
        Assert.Equal(UpdateKind.Mandatory, resolution.Kind);
        Assert.Same(newerOptional, resolution.Target); // installs the newest
        Assert.Same(remembered, resolution.KeepMandatory);

        var olderFresh = Manifest("1.1.0"); // proxy serving an older (signed) manifest
        var stale = UpdatePolicy.Resolve(V("1.0.0"), olderFresh, remembered);
        Assert.Equal(UpdateKind.Mandatory, stale.Kind);
        Assert.Same(remembered, stale.Target);
    }

    [Fact]
    public void RememberedMandatoryEndsOnceInstalled()
    {
        var remembered = Manifest("1.2.0", mandatory: true);
        var resolution = UpdatePolicy.Resolve(V("1.2.0"), null, remembered);
        Assert.Equal(UpdateKind.None, resolution.Kind);
        Assert.Null(resolution.KeepMandatory);

        var optional = UpdatePolicy.Resolve(V("1.2.0"), Manifest("1.3.0"), remembered);
        Assert.Equal(UpdateKind.Optional, optional.Kind);
        Assert.Null(optional.KeepMandatory);
    }

    [Fact]
    public void RememberedOptionalManifestIsNotEnforced()
    {
        var resolution = UpdatePolicy.Resolve(V("1.0.0"), null, Manifest("1.2.0"));
        Assert.Equal(UpdateKind.None, resolution.Kind);
        Assert.Null(resolution.KeepMandatory);
    }

    [Fact]
    public void CheckRunsAtMostEverySixHours()
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdatePolicy.IsCheckDue(null, now));
        Assert.False(UpdatePolicy.IsCheckDue(now.AddHours(-5.9), now));
        Assert.True(UpdatePolicy.IsCheckDue(now.AddHours(-6), now));
        Assert.True(UpdatePolicy.IsCheckDue(now.AddMinutes(5), now)); // clock moved back
        Assert.Equal(TimeSpan.FromSeconds(15), UpdatePolicy.StartupDelay);
    }

    // ---- Endpoint ----

    [Theory]
    [InlineData("https://spvision-update.<hesap>.workers.dev", false)]
    [InlineData("http://spvision-update.ornek.workers.dev", false)]
    [InlineData("https://spvision-update.ornek.workers.dev", true)]
    [InlineData("https://spvision-update.ornek.workers.dev/", true)]
    [InlineData("https://user:pw@spvision-update.ornek.workers.dev", false)]
    [InlineData("https://spvision-update.ornek.workers.dev?x=1", false)]
    [InlineData("spvision-update.ornek.workers.dev", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProxyAddressMustBeConfiguredHttps(string? url, bool configured) =>
        Assert.Equal(configured, UpdateEndpoint.IsConfigured(url));

    [Fact]
    public void EndpointPaths()
    {
        Assert.Equal("https://x.workers.dev/manifest.json", UpdateEndpoint.Manifest("https://x.workers.dev/").ToString());
        Assert.Equal("https://x.workers.dev/manifest.json.sig", UpdateEndpoint.Signature("https://x.workers.dev").ToString());
        Assert.Equal("https://x.workers.dev/download", UpdateEndpoint.Download("https://x.workers.dev").ToString());
        Assert.True(UpdateEndpoint.IsHttps(new Uri("https://objects.githubusercontent.com/a?sig=1")));
        Assert.False(UpdateEndpoint.IsHttps(new Uri("http://objects.githubusercontent.com/a")));
        Assert.False(UpdateEndpoint.IsHttps(new Uri("file:///C:/x.exe")));
    }

    // ---- Installer check ----

    [Fact]
    public async Task InstallerMustMatchSizeAndHash()
    {
        var bytes = Encoding.ASCII.GetBytes("SPVision installer bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes);
        Assert.True(await UpdateFile.MatchesAsync(stream, sha, bytes.Length));
        Assert.Equal(sha, await UpdateFile.Sha256HexAsync(stream));
        Assert.False(await UpdateFile.MatchesAsync(stream, sha, bytes.Length + 1));
        Assert.False(await UpdateFile.MatchesAsync(stream, new string('0', 64), bytes.Length));
        Assert.False(await UpdateFile.MatchesAsync(stream, sha.ToUpperInvariant(), bytes.Length));
        Assert.False(await UpdateFile.MatchesAsync(stream, "", bytes.Length));
        bytes[0] ^= 1;
        using var changed = new MemoryStream(bytes);
        Assert.False(await UpdateFile.MatchesAsync(changed, sha, bytes.Length));
    }

    // ---- Release notes front matter ----

    [Fact]
    public void ReleaseNotesFrontMatter()
    {
        var notes = ReleaseNotes.Parse("---\r\nmandatory: true\r\nminSupportedVersion: 1.1.0\r\n---\r\n\r\n- Yeni: güncelleme\r\n");
        Assert.True(notes.Mandatory);
        Assert.Equal("1.1.0", notes.MinSupportedVersion.ToString());
        Assert.Equal("- Yeni: güncelleme", notes.Notes);

        var plain = ReleaseNotes.Parse("\uFEFF---\nmandatory: false\n---\nNotlar");
        Assert.False(plain.Mandatory);
        Assert.Equal(AppVersion.Zero, plain.MinSupportedVersion);
    }

    [Theory]
    [InlineData("mandatory: false\n---\nx")]                                   // no opening line
    [InlineData("---\nmandatory: false\nx")]                                   // no closing line
    [InlineData("---\nminSupportedVersion: 1.0.0\n---\nx")]                    // mandatory missing
    [InlineData("---\nmandatory: yes\n---\nx")]
    [InlineData("---\nmandatory: false\nmandatory: true\n---\nx")]
    [InlineData("---\nmandatory: false\nmandatroy: true\n---\nx")]             // typo
    [InlineData("---\nmandatory: false\nminSupportedVersion: v1.0\n---\nx")]
    [InlineData("---\nmandatory: false\n---\n   \n")]                          // empty notes
    public void BadReleaseNotesAreRejected(string text) =>
        Assert.Throws<UpdateManifestException>(() => ReleaseNotes.Parse(text));

    // ---- Texts ----

    [Fact]
    public void ProgressTexts()
    {
        Assert.Equal("İndiriliyor · %42 (42,0 / 100,0 MB)", UpdateText.Progress(42L * 1048576, 100L * 1048576));
        Assert.Equal("İndiriliyor · 1,5 MB", UpdateText.Progress(1572864, 0));
        Assert.Equal(100, UpdateText.Percent(200, 100));
        Assert.Equal(0, UpdateText.Percent(5, 0));
        Assert.Equal("Henüz denetlenmedi", UpdateText.LastCheck(null));
        Assert.Equal("Oynatma daha hızlı", UpdateText.FirstLine("\n\n- Oynatma daha hızlı\n- Diğer"));
        Assert.Equal("Güncelleme imza anahtarı ayarlanmamış", UpdateText.KeyNotConfigured);
    }

    // ---- Settings ----

    [Fact]
    public void UpdateStateIsStoredInSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spvision-update-{Guid.NewGuid()}", "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var checkedAt = new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);
            store.Save(new AppSettings
            {
                Update = new UpdateState { LastCheckUtc = checkedAt, MandatoryManifest = "bWFu", MandatorySignature = "c2ln", LatestManifest = "bGF0" },
            });
            var loaded = store.Load().Update;
            Assert.NotNull(loaded);
            Assert.Equal(checkedAt, loaded!.LastCheckUtc);
            Assert.Equal("bWFu", loaded.MandatoryManifest);
            Assert.Equal("c2ln", loaded.MandatorySignature);
            Assert.Equal("bGF0", loaded.LatestManifest);
            Assert.Null(loaded.LatestSignature);
            Assert.Null(new SettingsStore(path + ".none").Load().Update); // older settings: no updater state
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
