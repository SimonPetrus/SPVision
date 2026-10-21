using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace SPVision.Core;

/// <summary>How a <see cref="CertificatePin"/> came to be stored.</summary>
public enum CertificatePinSource
{
    /// <summary>Trust on first use: the first HTTPS connection to a device without a pin.</summary>
    FirstUse,

    /// <summary>The user accepted a changed certificate ("Yeni sertifikaya güven").</summary>
    Confirmed,
}

/// <summary>
/// The HTTPS certificate SPVision trusts for one device (trust on first use). Not secret: stored in settings.json as is.
/// The pin is the SHA-256 of the certificate's SubjectPublicKeyInfo (<see cref="SpkiSha256"/>), so a certificate
/// re-issued with the same key (new validity, same key pair) still matches; the whole-certificate hash, subject, issuer
/// and validity are kept for display only. Protects ISAPI (HTTPS) only: the HCNetSDK port (8000) and RTSP do not use TLS.
/// </summary>
public sealed class CertificatePin
{
    /// <summary>SHA-256 of the DER SubjectPublicKeyInfo, 64 upper-case hex characters. The value compared.</summary>
    public string SpkiSha256 { get; init; } = "";

    /// <summary>SHA-256 of the whole DER certificate, 64 upper-case hex characters (display only).</summary>
    public string CertificateSha256 { get; init; } = "";

    public string? Subject { get; init; }

    public string? Issuer { get; init; }

    public DateTimeOffset? NotBefore { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    /// <summary>When SPVision stored it (or, for a certificate just seen, when it was seen).</summary>
    public DateTimeOffset PinnedAt { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<CertificatePinSource>))]
    public CertificatePinSource Source { get; init; }

    /// <summary>The certificate a device presented in the TLS handshake.</summary>
    public static CertificatePin From(X509Certificate certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var owned = certificate is X509Certificate2 ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var cert = owned ?? (X509Certificate2)certificate;
        return new CertificatePin
        {
            SpkiSha256 = CertificateFingerprint.Spki(cert),
            CertificateSha256 = CertificateFingerprint.Whole(cert),
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            NotBefore = new DateTimeOffset(cert.NotBefore),
            NotAfter = new DateTimeOffset(cert.NotAfter),
            PinnedAt = now,
            Source = CertificatePinSource.FirstUse,
        };
    }

    /// <summary>Same public key (the pin): ordinal, case-insensitive hex; an empty hash never matches.</summary>
    public bool SameKey(CertificatePin other) =>
        SpkiSha256.Length > 0 && string.Equals(SpkiSha256, other.SpkiSha256, StringComparison.OrdinalIgnoreCase);

    public CertificatePin With(CertificatePinSource source, DateTimeOffset pinnedAt) => new()
    {
        SpkiSha256 = SpkiSha256,
        CertificateSha256 = CertificateSha256,
        Subject = Subject,
        Issuer = Issuer,
        NotBefore = NotBefore,
        NotAfter = NotAfter,
        PinnedAt = pinnedAt,
        Source = source,
    };
}

/// <summary>SHA-256 fingerprints and their display form ("AB:CD:…").</summary>
public static class CertificateFingerprint
{
    /// <summary>Bytes shown by <see cref="Short"/> (8 bytes = 16 hex digits).</summary>
    public const int ShortBytes = 8;

    public static string Spki(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static string Whole(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>"AB:CD:EF:…" — every byte of the hex string, upper case.</summary>
    public static string Format(string hex) =>
        string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));

    /// <summary>The first <see cref="ShortBytes"/> bytes and an ellipsis: "AB:CD:EF:01:23:45:67:89…".</summary>
    public static string Short(string hex) =>
        hex.Length <= ShortBytes * 2 ? Format(hex) : Format(hex[..(ShortBytes * 2)]) + "…";
}

/// <summary>Turkish texts of the pin (device editor, the "certificate changed" dialog).</summary>
public static class CertificateText
{
    public const string NotVerified = "Sertifika doğrulanmadı — bağlanılmadı";

    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string Date(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("dd.MM.yyyy", Turkish) : "bilinmiyor";

    /// <summary>"21.10.2024 – 19.10.2034".</summary>
    public static string Validity(CertificatePin pin) => $"{Date(pin.NotBefore)} – {Date(pin.NotAfter)}";

    /// <summary>Device editor line.</summary>
    public static string EditorLine(bool useHttps, CertificatePin? pin) => (useHttps, pin) switch
    {
        (false, _) => "Sertifika: HTTPS kapalı (sabitleme yalnızca HTTPS bağlantısında yapılır)",
        (_, null) => "Sertifika parmak izi: henüz kaydedilmedi (ilk HTTPS bağlantısında kaydedilir)",
        (_, { Source: CertificatePinSource.Confirmed } p) => $"Sertifika parmak izi: {CertificateFingerprint.Short(p.SpkiSha256)} (onayınızla kaydedildi, {Date(p.PinnedAt)})",
        (_, { } p) => $"Sertifika parmak izi: {CertificateFingerprint.Short(p.SpkiSha256)} (ilk bağlantıda kaydedildi)",
    };

    /// <summary>Tool tip / details: full key fingerprint, certificate hash, subject, issuer, validity.</summary>
    public static string Details(CertificatePin pin) =>
        $"Ortak anahtar (SPKI) SHA-256: {CertificateFingerprint.Format(pin.SpkiSha256)}\n" +
        $"Sertifika SHA-256: {CertificateFingerprint.Format(pin.CertificateSha256)}\n" +
        $"Konu: {pin.Subject ?? "—"}\nVeren: {pin.Issuer ?? "—"}\nGeçerlilik: {Validity(pin)}\nKaydedildi: {Date(pin.PinnedAt)}";
}

/// <summary>
/// An HTTPS device presented a certificate whose public key differs from the pinned one. The TLS handshake was
/// refused, so no HTTP request (and no Digest credentials) left SPVision. Not a <see cref="IsapiException.ConnectionFailed"/>
/// error: the device editor must not offer "HTTP ile dene" for it.
/// </summary>
public sealed class CertificateChangedException(Device device, CertificatePin? old, CertificatePin presented)
    : IsapiException($"Kayıt cihazının güvenlik sertifikası değişti. {CertificateText.NotVerified}.")
{
    public Device Device { get; } = device;

    /// <summary>The pinned certificate (null if the pin was reset meanwhile).</summary>
    public CertificatePin? Old { get; } = old;

    /// <summary>The certificate the device (or whoever answered) presented.</summary>
    public CertificatePin Presented { get; } = presented;
}

/// <summary>
/// Trust-on-first-use decisions shared by every <see cref="IsapiClient"/> of the app: stores a first pin, replaces it
/// when the user trusts a changed certificate, and asks at most one question at a time. Concurrent requests that hit the
/// same changed certificate wait for the same answer; after "Bağlanma" the same (device, key) fails quietly until the
/// user asks again (<see cref="ForgetDeclined"/>) or resets the pin.
/// </summary>
public sealed class CertificateTrust
{
    /// <summary>The app's instance (<see cref="IsapiClient"/>'s public constructor uses it).</summary>
    public static CertificateTrust Shared { get; } = new();

    readonly object _lock = new();
    readonly Dictionary<(Guid Device, string Key), Task<bool>> _pending = [];
    readonly HashSet<(Guid Device, string Key)> _declined = [];
    readonly SemaphoreSlim _oneQuestion = new(1, 1);
    readonly Func<DateTimeOffset> _clock;

    public CertificateTrust(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.Now);

    /// <summary>
    /// Asks the user (device, stored pin, presented certificate) → true = trust it. Called on any thread, one question
    /// at a time. Null (no UI, tests): a changed certificate is refused without a question.
    /// </summary>
    public Func<Device, CertificatePin?, CertificatePin, Task<bool>>? Prompt { get; set; }

    /// <summary>A device's pin was stored, replaced or reset (any thread): settings should be saved.</summary>
    public event Action<Device>? PinChanged;

    /// <summary>Questions shown so far (tests: coalescing).</summary>
    public int QuestionsAsked { get; private set; }

    /// <summary>
    /// First successful connection of a device without a pin: stores <paramref name="presented"/>. Returns false when a
    /// pin exists already (another request stored it first); the caller compares against it.
    /// </summary>
    public bool PinFirstUse(Device device, CertificatePin presented)
    {
        lock (_lock)
        {
            if (device.Certificate is not null)
            {
                return false;
            }
            device.Certificate = presented.With(CertificatePinSource.FirstUse, _clock());
        }
        PinChanged?.Invoke(device);
        return true;
    }

    /// <summary>"Sertifikayı sıfırla": forget the pin; the next HTTPS connection pins again.</summary>
    public void Reset(Device device)
    {
        lock (_lock)
        {
            device.Certificate = null;
            _declined.RemoveWhere(k => k.Device == device.Id);
        }
        PinChanged?.Invoke(device);
    }

    /// <summary>The user asks again (e.g. "Bağlantıyı test et"): a declined certificate of this device is asked about again.</summary>
    public void ForgetDeclined(Guid deviceId)
    {
        lock (_lock)
        {
            _declined.RemoveWhere(k => k.Device == deviceId);
        }
    }

    public bool IsDeclined(Guid deviceId, CertificatePin presented)
    {
        lock (_lock)
        {
            return _declined.Contains((deviceId, presented.SpkiSha256.ToUpperInvariant()));
        }
    }

    /// <summary>
    /// A request met a certificate that is not the pinned one. True when it is trusted now (the pin was replaced, by this
    /// call's question or meanwhile): retry the request. False: declined (now or before) or nobody to ask.
    /// </summary>
    public Task<bool> ResolveMismatchAsync(Device device, CertificatePin presented)
    {
        var key = (device.Id, presented.SpkiSha256.ToUpperInvariant());
        TaskCompletionSource<bool> answer;
        Func<Device, CertificatePin?, CertificatePin, Task<bool>> prompt;
        CertificatePin? old;
        var pinnedAgain = false;
        lock (_lock)
        {
            if (device.Certificate is null)
            {
                // Reset meanwhile: this is a first use again.
                device.Certificate = presented.With(CertificatePinSource.FirstUse, _clock());
                pinnedAgain = true;
            }
            else if (device.Certificate.SameKey(presented))
            {
                return Task.FromResult(true); // trusted meanwhile (another request's question)
            }
            else if (_declined.Contains(key))
            {
                return Task.FromResult(false);
            }
            else if (_pending.TryGetValue(key, out var pending))
            {
                return pending;
            }
            else if (Prompt is null)
            {
                return Task.FromResult(false);
            }
            prompt = Prompt!;
            old = device.Certificate;
            answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pinnedAgain)
            {
                _pending[key] = answer.Task;
            }
        }
        if (pinnedAgain)
        {
            PinChanged?.Invoke(device);
            return Task.FromResult(true);
        }
        _ = AskAsync(device, old, presented, prompt, key, answer);
        return answer.Task;
    }

    async Task AskAsync(Device device, CertificatePin? old, CertificatePin presented,
        Func<Device, CertificatePin?, CertificatePin, Task<bool>> prompt, (Guid, string) key, TaskCompletionSource<bool> answer)
    {
        var trusted = false;
        await _oneQuestion.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                QuestionsAsked++;
            }
            try
            {
                trusted = await prompt(device, old, presented).ConfigureAwait(false);
            }
            catch (Exception)
            {
                trusted = false; // a failing dialog never trusts
            }
            lock (_lock)
            {
                if (trusted)
                {
                    device.Certificate = presented.With(CertificatePinSource.Confirmed, _clock());
                    _declined.RemoveWhere(k => k.Device == device.Id);
                }
                else
                {
                    _declined.Add(key);
                }
                _pending.Remove(key);
            }
        }
        finally
        {
            _oneQuestion.Release();
        }
        if (trusted)
        {
            PinChanged?.Invoke(device);
        }
        answer.SetResult(trusted);
    }
}
