using System.Buffers.Binary;
using System.Text;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>
/// Camera rename through NET_DVR_PICCFG_V40: name codec (encodings, 32-byte limit), the byte-diff guard of
/// <see cref="SdkWritePolicy"/> and <see cref="ChannelRenameWriter"/> against an in-memory device. No SDK, no network.
/// </summary>
public class SdkRenameTests
{
    // ================================================================ helpers

    /// <summary>A picture config like the device returns: dwSize, name, and every other byte non-zero and distinct.</summary>
    static byte[] PicCfg(string name, byte encodeType = SdkCharEncoding.Utf8)
    {
        var buffer = new byte[PicCfgV40.Size];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i * 7 + 3);
        }
        BinaryPrimitives.WriteInt32LittleEndian(buffer, PicCfgV40.Size);
        Array.Clear(buffer, PicCfgV40.NameOffset, PicCfgV40.NameLength);
        var bytes = SdkCharEncoding.For(encodeType)?.GetBytes(name) ?? Encoding.ASCII.GetBytes(name);
        bytes.CopyTo(buffer, PicCfgV40.NameOffset);
        return buffer;
    }

    static string? NameOf(byte[] buffer, byte encodeType = SdkCharEncoding.Utf8) =>
        ChannelNameCodec.Decode(PicCfgV40.NameField(buffer), encodeType);

    /// <summary>In-memory recorder: GET copies, SET checks the policy like the real transport, then stores.</summary>
    sealed class FakePicConfig(byte encodeType = SdkCharEncoding.Utf8) : IPicConfigTransport
    {
        public Dictionary<int, byte[]> Channels { get; } = [];
        public List<string> Calls { get; } = [];
        public List<(int Channel, byte[] Current, byte[] Body)> Sets { get; } = [];

        /// <summary>SET answers OK but the device keeps its old name.</summary>
        public bool IgnoreSet { get; set; }

        /// <summary>Changes the device's name between the GET and the SET (someone else renamed it).</summary>
        public Action<int>? AfterGet { get; set; }

        public string? RefuseSet { get; set; }

        public byte EncodeType { get; } = encodeType;

        public int SdkChannel(int channel) => 32 + channel; // StartDChan 33 like the DS-8632NI-I8

        public byte[] GetPicConfig(int sdkChannel)
        {
            Calls.Add($"GET {sdkChannel}");
            var copy = (byte[])(Channels.TryGetValue(sdkChannel, out var b) ? b : throw new SdkConfigException("Kanal numarası hatalı")).Clone();
            AfterGet?.Invoke(sdkChannel);
            return copy;
        }

        public void SetPicConfig(int sdkChannel, byte[] current, byte[] body)
        {
            SdkWritePolicy.EnsureSetAllowed(PicCfgV40.SetCommand, sdkChannel, current, body, EncodeType);
            Calls.Add($"SET {sdkChannel}");
            Sets.Add((sdkChannel, current, body));
            if (RefuseSet is { } refuse)
            {
                throw new SdkConfigException(refuse);
            }
            if (!IgnoreSet)
            {
                Channels[sdkChannel] = (byte[])body.Clone();
            }
        }
    }

    // ================================================================ layout

    [Fact]
    public void LayoutMatchesTheHeaderAndTheDevice()
    {
        // sizeof(NET_DVR_PICCFG_V40) computed from incEn/HCNetSDK.h and returned by the DS-8632NI-I8 for all 22 channels.
        Assert.Equal(77140, PicCfgV40.Size);
        Assert.Equal(4, PicCfgV40.NameOffset);
        Assert.Equal(32, PicCfgV40.NameLength);
        Assert.Equal(6179u, PicCfgV40.GetCommand);
        Assert.Equal(6180u, PicCfgV40.SetCommand);
        Assert.Equal(PicCfgV40.Size, PicCfgV40.ReadSize(PicCfg("DIS KAPI")));
    }

    // ================================================================ codec

    [Theory]
    [InlineData("DIS KAPI")]
    [InlineData("KARSILAMA")]
    [InlineData("ATOLYE 1")]
    public void DecodesTheAsciiNamesOfTheDevice(string name)
    {
        Assert.Equal(name, NameOf(PicCfg(name)));
        Assert.Equal(name, NameOf(PicCfg(name), SdkCharEncoding.Unknown)); // ASCII needs no known encoding
    }

    [Fact]
    public void DecodesAFull32ByteFieldWithoutTerminator()
    {
        var name = new string('A', 32);
        Assert.Equal(name, ChannelNameCodec.Decode(Encoding.ASCII.GetBytes(name), SdkCharEncoding.Utf8));
    }

    [Theory]
    [InlineData("GİRİŞ KAPISI")]
    [InlineData("ÇAY OCAĞI")]
    [InlineData("ÖĞLE ÜSTÜ ışık")]
    public void TurkishNamesRoundTripInUtf8(string name)
    {
        Assert.Null(ChannelNameCodec.Validate(name, SdkCharEncoding.Utf8));
        var field = ChannelNameCodec.Encode(name, SdkCharEncoding.Utf8);
        Assert.Equal(PicCfgV40.NameLength, field.Length);
        Assert.Equal(name, ChannelNameCodec.Decode(field, SdkCharEncoding.Utf8));
        Assert.Equal(Encoding.UTF8.GetBytes(name), field.TakeWhile(b => b != 0).ToArray());
    }

    [Fact]
    public void TurkishNamesUseOneByteInIso88599()
    {
        const byte iso88599 = 15; // 7 + 9 - 1
        var name = "ŞĞİ ışık çöü ŞĞİ ışık çöü ŞĞİ ış"; // 32 characters
        Assert.Equal("ISO-8859-9", SdkCharEncoding.Describe(iso88599));
        Assert.Null(ChannelNameCodec.Validate(name, iso88599));
        Assert.Equal(name, ChannelNameCodec.Decode(ChannelNameCodec.Encode(name, iso88599), iso88599));
        Assert.NotNull(ChannelNameCodec.Validate(name, SdkCharEncoding.Utf8)); // 2 bytes each in UTF-8: too long
    }

    [Fact]
    public void NonAsciiIsRefusedWhenTheEncodingIsUnknownOrCannotHoldIt()
    {
        Assert.Contains("bilinmiyor", ChannelNameCodec.Validate("GİRİŞ", SdkCharEncoding.Unknown)!, StringComparison.Ordinal);
        Assert.NotNull(ChannelNameCodec.Validate("GİRİŞ", 2));   // GBK has no Turkish İ / Ş
        Assert.Null(ChannelNameCodec.Validate("GIRIS", SdkCharEncoding.Unknown));
        Assert.Null(ChannelNameCodec.Decode(Encoding.UTF8.GetBytes("GİRİŞ"), SdkCharEncoding.Unknown)); // not shown as garbage
        Assert.Null(ChannelNameCodec.Decode([0xC4, 0x41], SdkCharEncoding.Utf8));                     // invalid UTF-8
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void AsciiLimitIs32Bytes(int length, bool ok)
    {
        Assert.Equal(ok, ChannelNameCodec.Validate(new string('K', length), SdkCharEncoding.Utf8) is null);
    }

    [Theory]
    [InlineData(16, true)]   // 16 × 2 bytes = 32
    [InlineData(17, false)]  // 34 bytes
    public void TurkishLettersCountTwoBytesInUtf8(int length, bool ok)
    {
        Assert.Equal(ok, ChannelNameCodec.Validate(new string('Ş', length), SdkCharEncoding.Utf8) is null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A\tB")]
    [InlineData("A\u0001")]
    [InlineData("KAPI<1>")]
    [InlineData("A&B")]
    [InlineData("A\"B")]
    [InlineData("A'B")]
    [InlineData("100%")]
    [InlineData("A\\B")]
    public void RefusesEmptyAndUnsafeNames(string name)
    {
        Assert.NotNull(ChannelNameCodec.Validate(name, SdkCharEncoding.Utf8));
        Assert.Throws<SdkWriteDeniedException>(() => ChannelNameCodec.Encode(name, SdkCharEncoding.Utf8));
    }

    [Fact]
    public void NamesAreTrimmed()
    {
        Assert.Equal("DIS KAPI", ChannelNameCodec.Decode(ChannelNameCodec.Encode("  DIS KAPI ", SdkCharEncoding.Utf8), SdkCharEncoding.Utf8));
    }

    [Fact]
    public void WithNameChangesOnlyTheNameField()
    {
        var current = PicCfg("DIS KAPI UZUN BIR AD");
        var body = ChannelNameCodec.WithName(current, "KAPI", SdkCharEncoding.Utf8);
        Assert.Equal(current.Length, body.Length);
        for (var i = 0; i < current.Length; i++)
        {
            if (!PicCfgV40.InNameField(i))
            {
                Assert.Equal(current[i], body[i]);
            }
        }
        Assert.Equal("KAPI", NameOf(body));
        Assert.All(body.Skip(PicCfgV40.NameOffset + 4).Take(PicCfgV40.NameLength - 4), b => Assert.Equal(0, b)); // old tail cleared
        Assert.Equal("DIS KAPI UZUN BIR AD", NameOf(current)); // the read buffer is not touched
    }

    // ================================================================ policy (byte-diff guard)

    [Fact]
    public void AllowsOnlyTheNameChangeWithTheSetPicCfgCommand()
    {
        var current = PicCfg("DIS KAPI");
        var body = ChannelNameCodec.WithName(current, "ON KAPI", SdkCharEncoding.Utf8);
        Assert.Null(SdkWritePolicy.CheckSet(PicCfgV40.SetCommand, 33, current, body, SdkCharEncoding.Utf8));
        Assert.Equal([PicCfgV40.SetCommand], SdkWritePolicy.AllowedSetCommands);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6179u)]  // GET_PICCFG_V40
    [InlineData(1003u)]  // SET_PICCFG_V30
    [InlineData(1001u)]  // SET_DEVICECFG_V30
    [InlineData(1021u)]  // SET_IPPARACFG (IP channel connection data)
    [InlineData(6181u)]
    public void RefusesEveryOtherCommand(uint command)
    {
        var current = PicCfg("DIS KAPI");
        var body = ChannelNameCodec.WithName(current, "ON KAPI", SdkCharEncoding.Utf8);
        Assert.NotNull(SdkWritePolicy.CheckSet(command, 33, current, body, SdkCharEncoding.Utf8));
        Assert.Throws<SdkWriteDeniedException>(() => SdkWritePolicy.EnsureSetAllowed(command, 33, current, body, SdkCharEncoding.Utf8));
    }

    [Theory]
    [InlineData(0)]          // dwSize
    [InlineData(3)]
    [InlineData(36)]         // first byte after the name: dwVideoFormat
    [InlineData(40)]         // struViColor
    [InlineData(1000)]
    [InlineData(77139)]      // last reserved byte
    public void RefusesAnyOtherChangedByte(int index)
    {
        var current = PicCfg("DIS KAPI");
        var body = ChannelNameCodec.WithName(current, "ON KAPI", SdkCharEncoding.Utf8);
        body[index] ^= 0x01;
        Assert.NotNull(SdkWritePolicy.CheckSet(PicCfgV40.SetCommand, 33, current, body, SdkCharEncoding.Utf8));
    }

    [Fact]
    public void RefusesWrongSizesUnchangedNamesAndBadFields()
    {
        var current = PicCfg("DIS KAPI");
        var body = ChannelNameCodec.WithName(current, "ON KAPI", SdkCharEncoding.Utf8);
        string? Check(byte[]? c, byte[]? b, int channel = 33) => SdkWritePolicy.CheckSet(PicCfgV40.SetCommand, channel, c, b, SdkCharEncoding.Utf8);

        Assert.NotNull(Check(current, current));                                 // nothing changed
        Assert.NotNull(Check(null, body));
        Assert.NotNull(Check(current, null));
        Assert.NotNull(Check(current[..^4], body[..^4]));                         // shorter buffers
        Assert.NotNull(Check([.. current, 0], [.. body, 0]));                     // longer buffers
        Assert.NotNull(Check(current, body, channel: 0));

        var badSize = (byte[])current.Clone();
        var badBody = (byte[])body.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badSize, PicCfgV40.Size - 4);
        BinaryPrimitives.WriteInt32LittleEndian(badBody, PicCfgV40.Size - 4);
        Assert.NotNull(Check(badSize, badBody));                                  // dwSize not 77140

        var empty = (byte[])current.Clone();
        Array.Clear(empty, PicCfgV40.NameOffset, PicCfgV40.NameLength);
        Assert.NotNull(Check(current, empty));                                    // empty name

        var trailing = (byte[])body.Clone();
        trailing[PicCfgV40.NameOffset + 20] = (byte)'X';                         // bytes after the terminator
        Assert.NotNull(Check(current, trailing));

        var markup = (byte[])current.Clone();
        Encoding.ASCII.GetBytes("<x>").CopyTo(markup, PicCfgV40.NameOffset);
        Assert.NotNull(Check(current, markup));                                   // a name Validate refuses
    }

    // ================================================================ writer (read-modify-write)

    [Fact]
    public void RenamesWithGetSetGetAndOnlyTheNameBytes()
    {
        var device = new FakePicConfig();
        device.Channels[33] = PicCfg("DIS KAPI");
        device.Channels[34] = PicCfg("KARSILAMA");
        var original = (byte[])device.Channels[33].Clone();

        var report = ChannelRenameWriter.Apply(device, [new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ÖN KAPI")]);

        Assert.True(report.Success);
        Assert.Equal(["DIS KAPI · Kamera adı: DIS KAPI → ÖN KAPI"], report.Applied);
        Assert.Equal(["GET 33", "SET 33", "GET 33"], device.Calls);
        var (channel, current, body) = Assert.Single(device.Sets);
        Assert.Equal(33, channel);
        Assert.Equal(original, current);
        Assert.Equal(Enumerable.Range(0, PicCfgV40.Size).Where(i => !PicCfgV40.InNameField(i)).Select(i => original[i]),
            Enumerable.Range(0, PicCfgV40.Size).Where(i => !PicCfgV40.InNameField(i)).Select(i => body[i]));
        Assert.Equal("ÖN KAPI", NameOf(device.Channels[33]));
        Assert.Equal("KARSILAMA", NameOf(device.Channels[34])); // other channels untouched
    }

    [Fact]
    public void SendsNothingWhenTheDeviceNameChangedMeanwhile()
    {
        var device = new FakePicConfig();
        device.Channels[33] = PicCfg("BASKA AD");

        var report = ChannelRenameWriter.Apply(device, [new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ON KAPI")]);

        Assert.False(report.Success);
        Assert.Contains("değişmiş", report.FailureMessage!, StringComparison.Ordinal);
        Assert.Equal(["GET 33"], device.Calls);
        Assert.Empty(device.Sets);
    }

    [Fact]
    public void AnInvalidNameInTheBatchSendsNothingAtAll()
    {
        var device = new FakePicConfig();
        device.Channels[33] = PicCfg("DIS KAPI");
        device.Channels[34] = PicCfg("KARSILAMA");

        var report = ChannelRenameWriter.Apply(device,
        [
            new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ON KAPI"),
            new ChannelRename(2, "KARSILAMA", "KARSILAMA", new string('Ş', 17)),
        ]);

        Assert.False(report.Success);
        Assert.Empty(device.Calls);
        Assert.Equal(2, report.NotApplied.Count);
    }

    [Fact]
    public void StopsAtTheFirstFailureAndReportsTheRest()
    {
        var device = new FakePicConfig { RefuseSet = "Cihaz değeri kabul etmedi: Parametre hatası" };
        device.Channels[33] = PicCfg("DIS KAPI");
        device.Channels[34] = PicCfg("KARSILAMA");

        var report = ChannelRenameWriter.Apply(device,
        [
            new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ON KAPI"),
            new ChannelRename(2, "KARSILAMA", "KARSILAMA", "GIRIS"),
        ]);

        Assert.False(report.Success);
        Assert.Empty(report.Applied);
        Assert.Equal(2, report.NotApplied.Count);
        Assert.Equal(["GET 33", "SET 33"], device.Calls); // channel 34 was never read or written
        Assert.Single(device.Sets);
    }

    [Fact]
    public void ReportsANameTheDeviceDidNotKeep()
    {
        var device = new FakePicConfig { IgnoreSet = true };
        device.Channels[33] = PicCfg("DIS KAPI");

        var report = ChannelRenameWriter.Apply(device, [new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ON KAPI")]);

        Assert.False(report.Success);
        Assert.Contains("DIS KAPI", report.FailureMessage!, StringComparison.Ordinal);
        Assert.Equal(["GET 33", "SET 33", "GET 33"], device.Calls);
    }

    [Fact]
    public void AFailedReadSendsNothing()
    {
        var device = new FakePicConfig(); // channel 33 missing: GET throws

        var report = ChannelRenameWriter.Apply(device, [new ChannelRename(1, "DIS KAPI", "DIS KAPI", "ON KAPI")]);

        Assert.False(report.Success);
        Assert.Empty(device.Sets);
    }
}
