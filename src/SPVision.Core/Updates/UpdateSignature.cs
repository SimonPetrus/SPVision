using System.Security.Cryptography;
using System.Text;

namespace SPVision.Core.Updates;

public enum SignatureCheck
{
    Valid,

    /// <summary>Malformed signature file, wrong key or changed manifest.</summary>
    Invalid,

    /// <summary>The app has no usable public key (placeholder still in <see cref="UpdateTrust"/>): never update.</summary>
    KeyNotConfigured,
}

/// <summary>
/// manifest.json.sig: ECDSA P-256 with SHA-256 over the exact bytes of manifest.json. The file holds the signature in
/// IEEE P1363 form (r ‖ s, 64 bytes) as base64 text, optionally followed by whitespace. The release tooling
/// (tools/release/UpdateSigner) signs with the private key; the app verifies with the public key compiled into
/// <see cref="UpdateTrust"/>. Without a valid signature nothing of the manifest is used, so a compromised proxy, release or
/// network cannot make the app install anything.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Largest .sig file accepted (88 base64 characters plus whitespace).</summary>
    public const int MaxFileBytes = 512;

    /// <summary>P1363 signature length for P-256.</summary>
    public const int SignatureBytes = 64;

    const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>True when <paramref name="publicKeyPem"/> is a P-256 "PUBLIC KEY" PEM (not the placeholder).</summary>
    public static bool IsConfigured(string? publicKeyPem)
    {
        using var key = ImportPublicKey(publicKeyPem);
        return key is not null;
    }

    public static SignatureCheck Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signatureFile, string? publicKeyPem)
    {
        using var key = ImportPublicKey(publicKeyPem);
        if (key is null)
        {
            return SignatureCheck.KeyNotConfigured;
        }
        if (manifest.IsEmpty || manifest.Length > UpdateManifest.MaxBytes || DecodeSignatureFile(signatureFile) is not { } signature)
        {
            return SignatureCheck.Invalid;
        }
        return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? SignatureCheck.Valid
            : SignatureCheck.Invalid;
    }

    /// <summary>The 64 signature bytes of a .sig file, or null when it is not base64 of exactly 64 bytes.</summary>
    public static byte[]? DecodeSignatureFile(ReadOnlySpan<byte> signatureFile)
    {
        if (signatureFile.IsEmpty || signatureFile.Length > MaxFileBytes)
        {
            return null;
        }
        foreach (var b in signatureFile)
        {
            if (b > 0x7F)
            {
                return null; // base64 is ASCII
            }
        }
        var text = Encoding.ASCII.GetString(signatureFile).Trim();
        var bytes = new byte[SignatureBytes + 3];
        return Convert.TryFromBase64String(text, bytes, out var written) && written == SignatureBytes
            ? bytes[..SignatureBytes]
            : null;
    }

    /// <summary>The signature file text for <paramref name="signature"/> (P1363, 64 bytes): base64 plus a newline.</summary>
    public static string EncodeSignatureFile(ReadOnlySpan<byte> signature) =>
        signature.Length == SignatureBytes
            ? Convert.ToBase64String(signature) + "\n"
            : throw new ArgumentException("P-256 imzası 64 bayt olmalı.", nameof(signature));

    /// <summary>Signs manifest bytes (release tooling only; the app never holds a private key).</summary>
    public static byte[] Sign(ReadOnlySpan<byte> manifest, ECDsa privateKey)
    {
        EnsureP256(privateKey);
        return privateKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Short fingerprint of a public key (first 16 hex digits of the SHA-256 of its SubjectPublicKeyInfo).</summary>
    public static string Fingerprint(ECDsa key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];

    static ECDsa? ImportPublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || !pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            return null;
        }
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            EnsureP256(key);
            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    static void EnsureP256(ECDsa key)
    {
        if (key.KeySize != 256)
        {
            throw new CryptographicException("Anahtar P-256 değil.");
        }
        var curve = key.ExportParameters(false).Curve;
        var oid = curve.Oid;
        var named = curve.IsNamed && (oid.Value == P256Oid ||
            oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1");
        if (!named)
        {
            throw new CryptographicException("Anahtar P-256 değil.");
        }
    }
}
