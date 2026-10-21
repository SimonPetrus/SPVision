namespace SPVision.Core.Updates;

/// <summary>
/// The public key that every update manifest must be signed for (<see cref="UpdateSignature"/>). The matching private key
/// never enters the repository: tools/release/new-signing-key.ps1 creates the pair on the owner's PC, keeps the private key
/// in a file of the owner's choosing and rewrites the block between the BEGIN/END markers below (do not edit the markers).
/// While the placeholder is here the updater refuses to update ("Güncelleme imza anahtarı ayarlanmamış").
/// Changing the key: releases signed with a new key are rejected by installed copies that still carry the old one, so
/// first publish a release that carries the new public key but is signed with the OLD key (see CLAUDE_REHBERI.md).
/// </summary>
public static class UpdateTrust
{
    /// <summary>Text of the placeholder; <see cref="UpdateSignature.IsConfigured"/> is false for it.</summary>
    public const string Placeholder = "YER-TUTUCU: tools/release/new-signing-key.ps1 henüz çalıştırılmadı";

    // BEGIN UPDATE PUBLIC KEY (tools/release/new-signing-key.ps1 rewrites this block)
    public const string PublicKeyPem = Placeholder;
    // END UPDATE PUBLIC KEY

    public static bool IsConfigured => UpdateSignature.IsConfigured(PublicKeyPem);

    const string BeginMarker = "// BEGIN UPDATE PUBLIC KEY";
    const string EndMarker = "// END UPDATE PUBLIC KEY";

    /// <summary>
    /// The text of UpdateTrust.cs with the lines between the BEGIN/END markers replaced by a raw string constant holding
    /// <paramref name="publicKeyPem"/> (used by the key generator; keeps the file's newline style and indentation).
    /// </summary>
    public static string WithPublicKey(string sourceText, string publicKeyPem)
    {
        if (!UpdateSignature.IsConfigured(publicKeyPem))
        {
            throw new ArgumentException("Bir P-256 PUBLIC KEY PEM'i bekleniyor.", nameof(publicKeyPem));
        }
        var newline = sourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = sourceText.Split(newline);
        var begins = lines.Select((l, i) => (l, i)).Where(x => x.l.TrimStart().StartsWith(BeginMarker, StringComparison.Ordinal)).ToList();
        var ends = lines.Select((l, i) => (l, i)).Where(x => x.l.TrimStart().StartsWith(EndMarker, StringComparison.Ordinal)).ToList();
        if (begins.Count != 1 || ends.Count != 1 || ends[0].i <= begins[0].i)
        {
            throw new InvalidOperationException("UpdateTrust.cs içinde BEGIN/END UPDATE PUBLIC KEY işaretleri bulunamadı.");
        }
        var begin = begins[0].i;
        var end = ends[0].i;
        var indent = lines[begin][..(lines[begin].Length - lines[begin].TrimStart().Length)];
        var block = new List<string> { indent + "public const string PublicKeyPem = \"\"\"" };
        block.AddRange(publicKeyPem.Replace("\r", "", StringComparison.Ordinal).Trim().Split('\n').Select(l => indent + "    " + l));
        block.Add(indent + "    \"\"\";");
        return string.Join(newline, lines[..(begin + 1)].Concat(block).Concat(lines[end..]));
    }
}
