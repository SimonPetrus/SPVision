using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SPVision.Core;

// Settings › Cihaz ayarları › Kameralar: the camera (channel) name through HCNetSDK instead of ISAPI. The ISAPI channel
// record (InputProxy/channels/{n}) holds the camera's address and login and its password is never returned, so it is
// never written. The SDK picture configuration NET_DVR_PICCFG_V40 holds the channel name without any connection data:
// GET → change only sChanName → SET (SdkWritePolicy) → GET again. Pure logic here, unit tested; the SDK calls are in the
// WPF project (Hik/HikChannelConfig.cs).

/// <summary>Layout facts of NET_DVR_PICCFG_V40 (incEn/HCNetSDK.h, SDK V6.1.11.40, x64 default packing).</summary>
public static class PicCfgV40
{
    /// <summary>NET_DVR_GET_PICCFG_V40 (read only).</summary>
    public const uint GetCommand = 6179;

    /// <summary>NET_DVR_SET_PICCFG_V40: the only SDK set command SPVision sends (<see cref="SdkWritePolicy"/>).</summary>
    public const uint SetCommand = 6180;

    /// <summary>
    /// sizeof(NET_DVR_PICCFG_V40) = 77140 bytes: computed from the header and confirmed on the DS-8632NI-I8 (GET returns
    /// exactly 77140 bytes and dwSize 77140 for all 22 channels; a buffer of 77136 is refused with error 17).
    /// </summary>
    public const int Size = 77140;

    /// <summary>BYTE sChanName[NAME_LEN] right after DWORD dwSize.</summary>
    public const int NameOffset = 4;

    /// <summary>NAME_LEN: 32 bytes in the device's character encoding; not necessarily NUL terminated.</summary>
    public const int NameLength = 32;

    /// <summary>dwSize (little endian) of a buffer; -1 when it is too short to hold one.</summary>
    public static int ReadSize(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(buffer) : -1;

    public static ReadOnlySpan<byte> NameField(ReadOnlySpan<byte> buffer) => buffer.Slice(NameOffset, NameLength);

    public static bool InNameField(int index) => index is >= NameOffset and < NameOffset + NameLength;
}

/// <summary>
/// NET_DVR_DEVICEINFO_V40.byCharEncodeType → .NET encoding: 0 unknown, 1 GB2312, 2 GBK, 3 BIG5, 4 Shift_JIS, 5 EUC-KR,
/// 6 UTF-8, 7–21 ISO-8859-1…15 (ISO-8859-(type−6)). The DS-8632NI-I8 reports 6 (UTF-8).
/// </summary>
public static class SdkCharEncoding
{
    public const byte Unknown = 0;
    public const byte Utf8 = 6;

    /// <summary>Strict (throwing) encoding for <paramref name="type"/>; null when unknown or not available on Windows.</summary>
    public static Encoding? For(byte type)
    {
        var codePage = type switch
        {
            1 => 936,     // GB2312 is a subset of GBK
            2 => 936,
            3 => 950,
            4 => 932,
            5 => 51949,
            Utf8 => 65001,
            >= 7 and <= 21 => (type - 6) switch
            {
                >= 1 and <= 9 => 28590 + (type - 6),
                13 => 28603,
                15 => 28605,
                _ => 0, // ISO-8859-10/11/12/14 have no Windows code page
            },
            _ => 0,
        };
        if (codePage == 0)
        {
            return null;
        }
        if (codePage == 65001)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }
        try
        {
            var provider = CodePagesEncodingProvider.Instance;
            return provider.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                   ?? Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Describe(byte type) => type switch
    {
        Unknown => "bilinmiyor",
        1 => "GB2312",
        2 => "GBK",
        3 => "BIG5",
        4 => "Shift_JIS",
        5 => "EUC-KR",
        Utf8 => "UTF-8",
        >= 7 and <= 21 => string.Create(CultureInfo.InvariantCulture, $"ISO-8859-{type - 6}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"kod {type}"),
    };
}

/// <summary>Decoding, validation and encoding of sChanName.</summary>
public static class ChannelNameCodec
{
    /// <summary>Characters Hikvision's own name fields refuse (and that would need escaping elsewhere).</summary>
    public const string ForbiddenCharacters = "<>&\"'%\\";

    /// <summary>
    /// The name in the 32-byte field (up to the first NUL), or null when it cannot be decoded safely: non-ASCII bytes
    /// with an unknown encoding, or bytes that are invalid in the device's encoding.
    /// </summary>
    public static string? Decode(ReadOnlySpan<byte> field, byte encodeType)
    {
        if (field.Length > PicCfgV40.NameLength)
        {
            field = field[..PicCfgV40.NameLength];
        }
        var end = field.IndexOf((byte)0);
        var bytes = end < 0 ? field : field[..end];
        if (IsAscii(bytes))
        {
            return Encoding.ASCII.GetString(bytes);
        }
        if (SdkCharEncoding.For(encodeType) is not { } encoding)
        {
            return null;
        }
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Null when <paramref name="name"/> (already trimmed by the caller or not) can be written; otherwise why not, in
    /// Turkish. Plain ASCII is identical in every encoding the SDK reports; other characters need a known encoding that
    /// stores them exactly. The limit is 32 bytes in that encoding (Turkish letters take 2 bytes in UTF-8).
    /// </summary>
    public static string? Validate(string? name, byte encodeType)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0)
        {
            return "Kamera adı boş olamaz.";
        }
        if (text.Any(char.IsControl))
        {
            return "Kamera adında kontrol karakteri olamaz.";
        }
        if (text.IndexOfAny(ForbiddenCharacters.ToCharArray()) is var bad and >= 0)
        {
            return $"Kamera adında “{text[bad]}” karakteri kullanılamaz.";
        }
        var bytes = TryEncode(text, encodeType, out var problem);
        if (bytes is null)
        {
            return problem;
        }
        if (bytes.Length > PicCfgV40.NameLength)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Kamera adı çok uzun: {bytes.Length} bayt, en çok {PicCfgV40.NameLength} (Türkçe harfler 2 bayt sayılır).");
        }
        return null;
    }

    /// <summary>The 32-byte field for a valid name: the encoded name, zero padded. Throws when <see cref="Validate"/> fails.</summary>
    public static byte[] Encode(string name, byte encodeType)
    {
        if (Validate(name, encodeType) is { } problem)
        {
            throw new SdkWriteDeniedException(problem);
        }
        var bytes = TryEncode(name.Trim(), encodeType, out _)!;
        var field = new byte[PicCfgV40.NameLength];
        bytes.CopyTo(field, 0);
        return field;
    }

    /// <summary>A copy of <paramref name="current"/> with only the name field replaced (see <see cref="SdkWritePolicy"/>).</summary>
    public static byte[] WithName(byte[] current, string name, byte encodeType)
    {
        if (current.Length != PicCfgV40.Size)
        {
            throw new SdkWriteDeniedException("Cihazdan okunan resim ayarı beklenen boyutta değil.");
        }
        var field = Encode(name, encodeType);
        var body = (byte[])current.Clone();
        field.CopyTo(body, PicCfgV40.NameOffset);
        return body;
    }

    static byte[]? TryEncode(string text, byte encodeType, out string? problem)
    {
        problem = null;
        if (text.All(c => c < 0x80))
        {
            return Encoding.ASCII.GetBytes(text);
        }
        if (SdkCharEncoding.For(encodeType) is not { } encoding)
        {
            problem = "Kayıt cihazının karakter kodlaması bilinmiyor: adda yalnızca İngilizce harf, rakam ve boşluk kullanın.";
            return null;
        }
        try
        {
            var bytes = encoding.GetBytes(text);
            if (encoding.GetString(bytes) != text || bytes.Contains((byte)0))
            {
                problem = $"Bu ad cihazın karakter kodlamasıyla ({SdkCharEncoding.Describe(encodeType)}) tam yazılamıyor.";
                return null;
            }
            return bytes;
        }
        catch (Exception e) when (e is EncoderFallbackException or DecoderFallbackException)
        {
            problem = $"Adda cihazın karakter kodlamasının ({SdkCharEncoding.Describe(encodeType)}) yazamadığı bir harf var.";
            return null;
        }
    }

    static bool IsAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>A device-side SDK write that <see cref="SdkWritePolicy"/> refuses (thrown before any SDK call).</summary>
public sealed class SdkWriteDeniedException(string message) : Exception(message);

/// <summary>An SDK configuration call failed (login, GET, SET); the message is Turkish and safe to show.</summary>
public sealed class SdkConfigException(string message) : Exception(message);

/// <summary>
/// The only SDK configuration write SPVision may send: NET_DVR_SET_PICCFG_V40 for one IP channel, where the buffer is the
/// one just read with NET_DVR_GET_PICCFG_V40 and differs from it only inside sChanName. Every other SDK set / remote
/// control command is refused. Checked right before the SDK call (Hik/HikChannelConfig.cs) and in
/// <see cref="ChannelRenameWriter"/>; no network I/O here.
/// </summary>
public static class SdkWritePolicy
{
    /// <summary>The whole SDK set allowlist.</summary>
    public static readonly IReadOnlyList<uint> AllowedSetCommands = [PicCfgV40.SetCommand];

    /// <summary>Null when <paramref name="body"/> may be sent with <paramref name="command"/>; otherwise why not (Turkish).</summary>
    public static string? CheckSet(uint command, int sdkChannel, byte[]? current, byte[]? body, byte encodeType)
    {
        if (!AllowedSetCommands.Contains(command))
        {
            return "Bu SDK ayarı SPVision'dan değiştirilemez.";
        }
        if (sdkChannel <= 0)
        {
            return "Kanal numarası geçersiz.";
        }
        if (current is null || body is null || current.Length != PicCfgV40.Size || body.Length != PicCfgV40.Size)
        {
            return "Resim ayarı beklenen boyutta değil; gönderilmedi.";
        }
        if (PicCfgV40.ReadSize(current) != PicCfgV40.Size || PicCfgV40.ReadSize(body) != PicCfgV40.Size)
        {
            return "Resim ayarının boyut alanı (dwSize) beklenen değerde değil; gönderilmedi.";
        }
        for (var i = 0; i < PicCfgV40.Size; i++)
        {
            if (current[i] != body[i] && !PicCfgV40.InNameField(i))
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"Gönderilecek ayarda kamera adı dışında bir bayt değişmiş ({i}); gönderilmedi.");
            }
        }
        var name = PicCfgV40.NameField(body);
        if (name.SequenceEqual(PicCfgV40.NameField(current)))
        {
            return "Kamera adı değişmemiş; gönderilmedi.";
        }
        var end = name.IndexOf((byte)0);
        if (end >= 0 && name[end..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return "Kamera adı alanında addan sonra artık bayt var; gönderilmedi.";
        }
        if (ChannelNameCodec.Decode(name, encodeType) is not { } decoded || ChannelNameCodec.Validate(decoded, encodeType) is not null ||
            decoded != decoded.Trim())
        {
            return "Kamera adı geçersiz; gönderilmedi.";
        }
        return null;
    }

    public static void EnsureSetAllowed(uint command, int sdkChannel, byte[]? current, byte[]? body, byte encodeType)
    {
        if (CheckSet(command, sdkChannel, current, body, encodeType) is { } reason)
        {
            throw new SdkWriteDeniedException(reason);
        }
    }
}

/// <summary>NET_DVR_GET/SET_PICCFG_V40 of one device (the WPF HikChannelConfig is the real one; tests use a fake).</summary>
public interface IPicConfigTransport
{
    /// <summary>byCharEncodeType of the login.</summary>
    byte EncodeType { get; }

    /// <summary>ISAPI channel (1…) → SDK channel (StartDChan-based on a recorder).</summary>
    int SdkChannel(int channel);

    /// <summary>The 77140-byte NET_DVR_PICCFG_V40 of the channel. Throws <see cref="SdkConfigException"/>.</summary>
    byte[] GetPicConfig(int sdkChannel);

    /// <summary>
    /// NET_DVR_SET_PICCFG_V40 with <paramref name="body"/>; must call <see cref="SdkWritePolicy.EnsureSetAllowed"/> with
    /// <paramref name="current"/> first. Throws <see cref="SdkConfigException"/> when the device refuses.
    /// </summary>
    void SetPicConfig(int sdkChannel, byte[] current, byte[] body);
}

/// <summary>One camera rename: ISAPI channel, the camera label of the confirmation, the name read and the new one.</summary>
public sealed record ChannelRename(int Channel, string Target, string OldName, string NewName)
{
    public const string Label = "Kamera adı";

    public string Description => ConfigChangeText.Format(Target, Label, OldName, NewName);
}

/// <summary>
/// Read-modify-write of channel names, one at a time: GET the picture config, check the name is still the one shown,
/// change only sChanName, SET, GET again and verify. Stops at the first failure. Synchronous (blocking SDK calls): run
/// it off the UI thread.
/// </summary>
public static class ChannelRenameWriter
{
    public static ConfigWriteReport Apply(IPicConfigTransport transport, IReadOnlyList<ChannelRename> renames)
    {
        // Every name is validated before the first SDK call: a batch with a bad name sends nothing at all.
        foreach (var rename in renames)
        {
            if (ChannelNameCodec.Validate(rename.NewName, transport.EncodeType) is { } problem)
            {
                return new ConfigWriteReport([], rename.Description, $"{rename.Target}: {problem}", renames.Select(r => r.Description).ToList());
            }
        }
        var applied = new List<string>();
        for (var i = 0; i < renames.Count; i++)
        {
            var rename = renames[i];
            string? failure;
            try
            {
                failure = ApplyOne(transport, rename);
            }
            catch (Exception e) when (e is SdkConfigException or SdkWriteDeniedException)
            {
                failure = e.Message;
            }
            if (failure is null)
            {
                applied.Add(rename.Description);
                continue;
            }
            return new ConfigWriteReport(applied, rename.Description, failure, renames.Skip(i).Select(r => r.Description).ToList());
        }
        return new ConfigWriteReport(applied, null, null, []);
    }

    static string? ApplyOne(IPicConfigTransport transport, ChannelRename rename)
    {
        var channel = transport.SdkChannel(rename.Channel);
        var current = transport.GetPicConfig(channel);
        var name = ChannelNameCodec.Decode(PicCfgV40.NameField(current), transport.EncodeType);
        if (name != rename.OldName)
        {
            return $"{rename.Target}: cihazdaki ad bu sayfa açıldıktan sonra değişmiş (“{name ?? "okunamadı"}”); gönderilmedi.";
        }
        var body = ChannelNameCodec.WithName(current, rename.NewName, transport.EncodeType);
        SdkWritePolicy.EnsureSetAllowed(PicCfgV40.SetCommand, channel, current, body, transport.EncodeType);
        transport.SetPicConfig(channel, current, body);
        var after = ChannelNameCodec.Decode(PicCfgV40.NameField(transport.GetPicConfig(channel)), transport.EncodeType);
        return after == rename.NewName.Trim()
            ? null
            : $"Cihaz değeri kabul etmedi: {rename.Target} · {ChannelRename.Label} cihazda “{after ?? "okunamadı"}” olarak kaldı.";
    }
}
