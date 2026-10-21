using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SPVision.Core.Updates;

// SPVision release tool (see CLAUDE_REHBERI.md, "Uygulama güncelleme"). Never prints a private key.
//
//   keygen   --private-out <pem> --source <UpdateTrust.cs> [--force]
//   manifest --installer <exe> --notes <release-notes/vX.Y.Z.md> --version <X.Y.Z> --out <manifest.json>
//            [--notes-out <body.md>] [--published <ISO 8601>]
//   sign     --manifest <manifest.json> (--key-file <pem> | --key-env <VARIABLE>) [--key-rotation]
//   verify   --manifest <manifest.json> [--sig <manifest.json.sig>] [--installer <exe>] [--key-file <pem> (key rotation)]
//
// "sign" and "verify" check against the public key compiled into this tool from src/SPVision.Core/Updates/UpdateTrust.cs,
// i.e. the key the app will use: a release signed with any other key is refused before it can be published.
// Exception, key rotation (--key-rotation): the one release that carries the NEW public key must be signed with the OLD
// private key (installed copies only trust the old one); the signature is then checked with the old key itself.

try
{
    if (args.Length == 0)
    {
        return Usage();
    }
    var options = Options.Parse(args[1..]);
    return args[0] switch
    {
        "keygen" => KeyGen(options),
        "manifest" => Manifest(options),
        "sign" => Sign(options),
        "verify" => Verify(options),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is UpdateManifestException or ArgumentException or IOException or UnauthorizedAccessException
    or CryptographicException or FormatException or InvalidOperationException)
{
    Console.Error.WriteLine("HATA: " + ex.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Kullanım: UpdateSigner keygen|manifest|sign|verify … (ayrıntı: tools/release/UpdateSigner/Program.cs)");
    return 2;
}

static int KeyGen(Options options)
{
    var privateOut = options.Required("--private-out");
    var source = options.Required("--source");
    var force = options.Flag("--force");
    var text = File.ReadAllText(source);
    if (UpdateTrust.IsConfigured && !force)
    {
        throw new InvalidOperationException(
            "Uygulamada zaten gerçek bir açık anahtar var. Anahtarı değiştirmek kurulu kopyaların güncellenmesini bozar " +
            "(CLAUDE_REHBERI.md › Uygulama güncelleme › Anahtar değiştirme). Bilerek yapıyorsanız -Force kullanın.");
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicPem = key.ExportSubjectPublicKeyInfoPem();
    var updated = UpdateTrust.WithPublicKey(text, publicPem);

    // Private key first, so a failure never leaves a public key without its private key.
    var directory = Path.GetDirectoryName(Path.GetFullPath(privateOut));
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    // Never overwrites: the old private key is still needed to sign the key-rotation release.
    using (var stream = new FileStream(privateOut, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
    {
        writer.Write(key.ExportPkcs8PrivateKeyPem());
        writer.Write('\n');
    }
    File.WriteAllText(source, updated, new UTF8Encoding(false));

    Console.WriteLine($"Gizli anahtar yazıldı: {Path.GetFullPath(privateOut)}");
    Console.WriteLine($"Açık anahtar uygulamaya yazıldı: {Path.GetFullPath(source)}");
    Console.WriteLine($"Anahtar parmak izi: {UpdateSignature.Fingerprint(key)}");
    return 0;
}

static int Manifest(Options options)
{
    var installer = options.Required("--installer");
    var notesPath = options.Required("--notes");
    var version = AppVersion.ParseStrict(options.Required("--version"));
    var output = options.Required("--out");
    var published = options.Value("--published") is { } text
        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
        : DateTimeOffset.UtcNow;

    var notes = ReleaseNotes.Parse(File.ReadAllText(notesPath, Encoding.UTF8));
    if (notes.MinSupportedVersion > version)
    {
        throw new InvalidOperationException($"minSupportedVersion ({notes.MinSupportedVersion}) yayınlanan sürümden ({version}) büyük olamaz.");
    }
    var asset = Path.GetFileName(installer);
    if (!UpdateManifest.IsValidAssetName(asset))
    {
        throw new InvalidOperationException($"Kurulum dosyasının adı uygun değil: {asset}");
    }
    string sha256;
    long size;
    using (var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        size = stream.Length;
        sha256 = UpdateFile.Sha256HexAsync(stream).GetAwaiter().GetResult();
    }
    var manifest = new UpdateManifest
    {
        Version = version,
        Mandatory = notes.Mandatory,
        MinSupportedVersion = notes.MinSupportedVersion,
        Notes = notes.Notes,
        Asset = asset,
        Sha256 = sha256,
        Size = size,
        Published = published,
    };
    var bytes = manifest.ToJson();
    UpdateManifest.Parse(bytes); // what the app will read
    File.WriteAllBytes(output, bytes);
    if (options.Value("--notes-out") is { } notesOut)
    {
        File.WriteAllText(notesOut, notes.Notes + "\n", new UTF8Encoding(false));
    }
    Console.WriteLine($"manifest.json yazıldı: {version} · zorunlu: {(notes.Mandatory ? "evet" : "hayır")} · " +
        $"en düşük desteklenen: {notes.MinSupportedVersion} · {asset} · {size} bayt · sha256 {sha256}");
    return 0;
}

static int Sign(Options options)
{
    var manifestPath = options.Required("--manifest");
    if (!UpdateTrust.IsConfigured)
    {
        throw new InvalidOperationException(UpdateText.KeyNotConfigured + ": önce tools/release/new-signing-key.ps1 çalıştırın.");
    }
    string pem;
    if (options.Value("--key-file") is { } keyFile)
    {
        pem = File.ReadAllText(keyFile);
    }
    else if (options.Value("--key-env") is { } variable)
    {
        pem = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"{variable} ortam değişkeni tanımlı değil — imzasız sürüm yayınlanmaz.");
    }
    else
    {
        throw new ArgumentException("--key-file ya da --key-env gerekli.");
    }

    var manifest = File.ReadAllBytes(manifestPath);
    UpdateManifest.Parse(manifest);
    using var key = ECDsa.Create();
    try
    {
        key.ImportFromPem(pem);
    }
    catch (Exception ex) when (ex is ArgumentException or CryptographicException)
    {
        throw new InvalidOperationException("Gizli anahtar okunamadı (PKCS#8 PEM, P-256 bekleniyor).");
    }
    pem = "";
    var signatureFile = Encoding.ASCII.GetBytes(UpdateSignature.EncodeSignatureFile(UpdateSignature.Sign(manifest, key)));
    var matchesApp = UpdateSignature.Verify(manifest, signatureFile, UpdateTrust.PublicKeyPem) == SignatureCheck.Valid;
    if (options.Flag("--key-rotation"))
    {
        if (matchesApp)
        {
            throw new InvalidOperationException("--key-rotation: bu anahtar zaten uygulamadaki anahtar; anahtar değişimi yok, bayrağı kaldırın.");
        }
        if (UpdateSignature.Verify(manifest, signatureFile, key.ExportSubjectPublicKeyInfoPem()) != SignatureCheck.Valid)
        {
            throw new InvalidOperationException("İmza doğrulanamadı.");
        }
        File.WriteAllBytes(manifestPath + ".sig", signatureFile);
        Console.WriteLine($"ANAHTAR DEĞİŞİMİ: ESKİ anahtarla (parmak izi {UpdateSignature.Fingerprint(key)}) imzalandı; bu sürüm YENİ açık " +
            "anahtarı taşıyor. Sonraki sürümleri yeni anahtarla imzalayın.");
        return 0;
    }
    if (!matchesApp)
    {
        throw new InvalidOperationException(
            $"Bu gizli anahtar uygulamadaki açık anahtarla eşleşmiyor (anahtar parmak izi {UpdateSignature.Fingerprint(key)}). " +
            "Yanlış anahtar dosyası ya da yanlış GitHub secret; imza yazılmadı.");
    }
    File.WriteAllBytes(manifestPath + ".sig", signatureFile);
    Console.WriteLine($"İmzalandı ve uygulamanın açık anahtarıyla doğrulandı: {manifestPath}.sig");
    return 0;
}

static int Verify(Options options)
{
    var manifestPath = options.Required("--manifest");
    var sigPath = options.Value("--sig") ?? manifestPath + ".sig";
    var manifestBytes = File.ReadAllBytes(manifestPath);
    var publicKey = UpdateTrust.PublicKeyPem;
    if (options.Value("--key-file") is { } keyFile)
    {
        // Key rotation: check with the (old) signing key's public half instead of the app's new key.
        using var old = ECDsa.Create();
        old.ImportFromPem(File.ReadAllText(keyFile));
        publicKey = old.ExportSubjectPublicKeyInfoPem();
    }
    var check = UpdateSignature.Verify(manifestBytes, File.ReadAllBytes(sigPath), publicKey);
    if (check != SignatureCheck.Valid)
    {
        throw new InvalidOperationException(check == SignatureCheck.KeyNotConfigured ? UpdateText.KeyNotConfigured : "İmza geçersiz.");
    }
    var manifest = UpdateManifest.Parse(manifestBytes);
    if (options.Value("--installer") is { } installer)
    {
        if (!string.Equals(Path.GetFileName(installer), manifest.Asset, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Kurulum dosyasının adı manifest'tekiyle ({manifest.Asset}) aynı değil.");
        }
        using var stream = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!UpdateFile.MatchesAsync(stream, manifest.Sha256, manifest.Size).GetAwaiter().GetResult())
        {
            throw new InvalidOperationException("Kurulum dosyasının boyutu ya da SHA-256'sı manifest'le uyuşmuyor.");
        }
    }
    Console.WriteLine($"Doğrulandı: {manifest.Version} ({manifest.Asset}, {manifest.Size} bayt, zorunlu: {(manifest.Mandatory ? "evet" : "hayır")}).");
    return 0;
}

sealed class Options
{
    readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Beklenmeyen bağımsız değişken: {args[i]}");
            }
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options._values[args[i]] = hasValue ? args[++i] : null;
        }
        return options;
    }

    public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string Required(string name) => Value(name) ?? throw new ArgumentException($"{name} gerekli.");

    public bool Flag(string name) => _values.ContainsKey(name);
}
