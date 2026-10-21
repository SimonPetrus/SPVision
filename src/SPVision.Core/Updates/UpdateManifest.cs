using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SPVision.Core.Updates;

/// <summary>The manifest could not be used (Turkish message, safe to show).</summary>
public sealed class UpdateManifestException(string message) : Exception(message);

/// <summary>
/// manifest.json of a release, as published next to the installer and signed (<see cref="UpdateSignature"/>):
/// <code>
/// { "version": "1.2.0", "mandatory": false, "minSupportedVersion": "1.0.0", "notes": "…", "asset": "SPVision-Kurulum-1.2.0.exe",
///   "sha256": "&lt;64 hex&gt;", "size": 123456789, "published": "2026-10-21T12:00:00Z" }
/// </code>
/// <see cref="Parse"/> is strict: size limit, a single JSON object, every field present exactly once with the right type,
/// strict versions, a plain installer file name, 64 hex digits, a bounded size. Unknown fields are ignored so that a later
/// release can add one without breaking older clients (the whole file is signed anyway). Only bytes whose signature
/// verified are parsed by the app.
/// </summary>
public sealed partial class UpdateManifest
{
    /// <summary>Largest manifest.json accepted (it is a few hundred bytes plus the notes).</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Longest release notes text (characters).</summary>
    public const int MaxNotesLength = 8000;

    /// <summary>Largest installer accepted (the self-contained installer is ~100–200 MB).</summary>
    public const long MaxInstallerBytes = 1L << 30;

    public required AppVersion Version { get; init; }

    /// <summary>The publisher marked this release mandatory: older versions must update before they can be used.</summary>
    public required bool Mandatory { get; init; }

    /// <summary>Versions below this one must update (mandatory for them even when <see cref="Mandatory"/> is false).</summary>
    public required AppVersion MinSupportedVersion { get; init; }

    /// <summary>Turkish release notes ("Neler yeni"), plain text.</summary>
    public required string Notes { get; init; }

    /// <summary>File name of the installer in the release (also the name it is saved under).</summary>
    public required string Asset { get; init; }

    /// <summary>SHA-256 of the installer, lower-case hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Installer size in bytes.</summary>
    public required long Size { get; init; }

    public required DateTimeOffset Published { get; init; }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    /// <summary>A plain installer file name: letters, digits, '.', '_', '-', ending in .exe, no "..", at most 104 characters.</summary>
    public static bool IsValidAssetName(string? name) =>
        name is not null && AssetPattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    public static bool IsValidSha256(string? hex) => hex is not null && Sha256Pattern().IsMatch(hex);

    static readonly string[] Fields = ["version", "mandatory", "minSupportedVersion", "notes", "asset", "sha256", "size", "published"];

    public static bool TryParse(ReadOnlySpan<byte> json, out UpdateManifest? manifest, out string? error)
    {
        try
        {
            manifest = Parse(json);
            error = null;
            return true;
        }
        catch (UpdateManifestException ex)
        {
            manifest = null;
            error = ex.Message;
            return false;
        }
    }

    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0)
        {
            throw Bad("boş");
        }
        if (json.Length > MaxBytes)
        {
            throw Bad("beklenenden büyük");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 4,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException)
        {
            throw Bad("JSON değil");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Bad("bir JSON nesnesi değil");
            }
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (Fields.Contains(property.Name, StringComparer.Ordinal) && !values.TryAdd(property.Name, property.Value))
                {
                    throw Bad($"“{property.Name}” iki kez var");
                }
            }

            var version = Version("version");
            var minSupported = Version("minSupportedVersion");
            if (minSupported > version)
            {
                throw Bad("minSupportedVersion, version'dan büyük");
            }
            var notes = Text("notes");
            if (notes.Length > MaxNotesLength)
            {
                throw Bad("notlar çok uzun");
            }
            if (notes.Contains('\0'))
            {
                throw Bad("notlarda geçersiz karakter");
            }
            var asset = Text("asset");
            if (!IsValidAssetName(asset))
            {
                throw Bad("kurulum dosyasının adı geçersiz");
            }
            var sha256 = Text("sha256");
            if (!IsValidSha256(sha256))
            {
                throw Bad("sha256 64 küçük onaltılık rakam değil");
            }
            var size = Get("size", JsonValueKind.Number);
            if (!size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > MaxInstallerBytes)
            {
                throw Bad("boyut geçersiz");
            }
            var publishedText = Text("published");
            if (!DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
            {
                throw Bad("yayın tarihi geçersiz");
            }
            var mandatory = Get("mandatory", JsonValueKind.Undefined);
            if (mandatory.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Bad("“mandatory” true/false değil");
            }

            return new UpdateManifest
            {
                Version = version,
                Mandatory = mandatory.ValueKind == JsonValueKind.True,
                MinSupportedVersion = minSupported,
                Notes = notes.Replace("\r\n", "\n", StringComparison.Ordinal),
                Asset = asset,
                Sha256 = sha256,
                Size = bytes,
                Published = published.ToUniversalTime(),
            };

            JsonElement Get(string name, JsonValueKind kind)
            {
                if (!values.TryGetValue(name, out var value))
                {
                    throw Bad($"“{name}” eksik");
                }
                if (kind != JsonValueKind.Undefined && value.ValueKind != kind)
                {
                    throw Bad($"“{name}” türü yanlış");
                }
                return value;
            }

            string Text(string name) => Get(name, JsonValueKind.String).GetString()!;

            AppVersion Version(string name) =>
                AppVersion.TryParseStrict(Text(name), out var parsed) ? parsed : throw Bad($"“{name}” geçerli bir sürüm değil (1.2.3)");
        }
    }

    /// <summary>
    /// The exact bytes published as manifest.json (UTF-8, no BOM, fixed field order, non-ASCII escaped so the file is
    /// plain ASCII). The release tooling writes, signs and uploads these bytes; the app verifies the signature over them.
    /// </summary>
    public byte[] ToJson()
    {
        if (!IsValidAssetName(Asset) || !IsValidSha256(Sha256) || Size <= 0 || Size > MaxInstallerBytes ||
            MinSupportedVersion > Version || Notes.Length > MaxNotesLength)
        {
            throw new UpdateManifestException("Güncelleme bilgisi yazılamadı: alanlardan biri geçersiz.");
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", Version.ToString());
            writer.WriteBoolean("mandatory", Mandatory);
            writer.WriteString("minSupportedVersion", MinSupportedVersion.ToString());
            writer.WriteString("notes", Notes);
            writer.WriteString("asset", Asset);
            writer.WriteString("sha256", Sha256);
            writer.WriteNumber("size", Size);
            writer.WriteString("published", Published.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    static UpdateManifestException Bad(string what) => new($"Güncelleme bilgisi geçersiz: {what}.");
}

/// <summary>
/// release-notes/vX.Y.Z.md of a release: a small front matter, then the Turkish notes shown in the app.
/// <code>
/// ---
/// mandatory: false
/// minSupportedVersion: 1.0.0
/// ---
/// Notes…
/// </code>
/// <c>mandatory</c> is required; <c>minSupportedVersion</c> is optional (default 0.0.0 = no version is forced). Any other key,
/// a duplicate key or a missing closing line is an error, so a typo cannot silently publish the wrong policy.
/// </summary>
public sealed record ReleaseNotes(bool Mandatory, AppVersion MinSupportedVersion, string Notes)
{
    public static ReleaseNotes Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            throw new UpdateManifestException("Sürüm notları “---” satırıyla başlamalı (mandatory / minSupportedVersion).");
        }
        bool? mandatory = null;
        AppVersion? minSupported = null;
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
            {
                end = i;
                break;
            }
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new UpdateManifestException($"Sürüm notları: anlaşılmayan satır “{line}”.");
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "mandatory" when mandatory is null:
                    mandatory = value switch
                    {
                        "true" => true,
                        "false" => false,
                        _ => throw new UpdateManifestException("Sürüm notları: mandatory yalnız true ya da false olabilir."),
                    };
                    break;
                case "minSupportedVersion" when minSupported is null:
                    minSupported = AppVersion.TryParseStrict(value, out var parsed)
                        ? parsed
                        : throw new UpdateManifestException($"Sürüm notları: minSupportedVersion geçersiz “{value}” (beklenen 1.2.3).");
                    break;
                default:
                    throw new UpdateManifestException($"Sürüm notları: bilinmeyen ya da yinelenen alan “{key}”.");
            }
        }
        if (end < 0)
        {
            throw new UpdateManifestException("Sürüm notları: kapanış “---” satırı yok.");
        }
        if (mandatory is null)
        {
            throw new UpdateManifestException("Sürüm notları: mandatory: true/false satırı eksik.");
        }
        var notes = string.Join('\n', lines[(end + 1)..]).Trim();
        if (notes.Length == 0)
        {
            throw new UpdateManifestException("Sürüm notları boş; kullanıcıya gösterilecek Türkçe notları yazın.");
        }
        if (notes.Length > UpdateManifest.MaxNotesLength)
        {
            throw new UpdateManifestException($"Sürüm notları {UpdateManifest.MaxNotesLength} karakterden uzun.");
        }
        return new ReleaseNotes(mandatory.Value, minSupported ?? AppVersion.Zero, notes);
    }
}

/// <summary>SHA-256 helpers for the downloaded installer.</summary>
public static class UpdateFile
{
    /// <summary>Lower-case hex SHA-256 of the stream from its start (the stream is rewound first; read to the end).</summary>
    public static async Task<string> Sha256HexAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        stream.Position = 0;
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// True when the stream is exactly <paramref name="size"/> bytes long and its SHA-256 equals <paramref name="sha256"/>
    /// (both from the signed manifest). Length is checked first, so a wrong-sized file is never hashed.
    /// </summary>
    public static async Task<bool> MatchesAsync(Stream stream, string sha256, long size, CancellationToken cancellationToken = default)
    {
        if (!UpdateManifest.IsValidSha256(sha256) || stream.Length != size)
        {
            return false;
        }
        var actual = await Sha256HexAsync(stream, cancellationToken).ConfigureAwait(false);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(sha256));
    }
}
