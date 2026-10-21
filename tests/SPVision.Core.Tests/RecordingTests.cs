using System.Buffers.Binary;
using System.Text;
using Flyleaf.FFmpeg;
using SPVision.Core;
using static Flyleaf.FFmpeg.Raw;

namespace SPVision.Core.Tests;

public class Mp4InspectorTests
{
    static byte[] Box(string type, params byte[][] children)
    {
        var payload = children.SelectMany(c => c).ToArray();
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomavc1"));

    static byte[] Moov(uint samples, string handler = "vide", uint timescale = 1000, uint duration = 8000) =>
        Box("moov",
            Box("mvhd", U32(0), U32(0), U32(0), U32(timescale), U32(duration)),
            Box("trak",
                Box("mdia",
                    Box("hdlr", U32(0), U32(0), Encoding.ASCII.GetBytes(handler)),
                    Box("minf", Box("stbl", Box("stsz", U32(0), U32(0), U32(samples)))))));

    static Mp4Info Inspect(params byte[][] boxes) => Mp4Inspector.Inspect(new MemoryStream(boxes.SelectMany(b => b).ToArray()));

    [Fact]
    public void Complete_file_is_valid_with_duration_and_frames()
    {
        var info = Inspect(Ftyp(), Box("mdat", new byte[100]), Moov(200));
        Assert.True(info.IsValid);
        Assert.Equal(200, info.VideoSamples);
        Assert.Equal(TimeSpan.FromSeconds(8), info.Duration);
        Assert.Null(info.Problem);
    }

    [Fact]
    public void Moov_before_mdat_is_valid_too()
    {
        Assert.True(Inspect(Ftyp(), Moov(5), Box("mdat", new byte[10])).IsValid);
    }

    [Fact]
    public void Empty_file_is_rejected()
    {
        var info = Inspect();
        Assert.False(info.IsValid);
        Assert.Equal("dosya boş", info.Problem);
    }

    [Fact]
    public void Hikvision_stream_file_is_not_an_mp4()
    {
        var imkh = Encoding.ASCII.GetBytes("IMKH").Concat(new byte[60]).ToArray();
        var info = Inspect(imkh);
        Assert.False(info.IsValid);
        Assert.Contains("Hikvision", info.Problem);
    }

    [Fact]
    public void Unfinished_file_without_moov_is_rejected()
    {
        var info = Inspect(Ftyp(), Box("mdat", new byte[100]));
        Assert.False(info.IsValid);
        Assert.Contains("tamamlanmamış", info.Problem);
    }

    [Fact]
    public void Truncated_box_is_rejected()
    {
        var mdat = Box("mdat", new byte[100]);
        var info = Inspect(Ftyp(), mdat[..50]);
        Assert.False(info.IsValid);
        Assert.Contains("yarım", info.Problem);
    }

    [Fact]
    public void Missing_ftyp_audio_only_and_no_samples_are_rejected()
    {
        Assert.False(Inspect(Box("mdat", new byte[10]), Moov(5)).IsValid);
        Assert.False(Inspect(Ftyp(), Box("mdat", new byte[10]), Moov(5, handler: "soun")).IsValid);
        Assert.False(Inspect(Ftyp(), Box("mdat", new byte[10]), Moov(0)).IsValid);
        Assert.False(Inspect(Ftyp(), Box("mdat"), Moov(5)).IsValid);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var info = Mp4Inspector.Inspect(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"));
        Assert.False(info.IsValid);
        Assert.NotNull(info.Problem);
    }
}

public class FrameClockTests
{
    [Fact]
    public void Follows_the_stream_stamps_from_zero()
    {
        var clock = new FrameClock();
        Assert.Equal(0, clock.Next(123_000, 25));
        Assert.Equal(40, clock.Next(123_040, 25));
        Assert.Equal(120, clock.Next(123_120, 25)); // a dropped frame keeps its gap
    }

    [Fact]
    public void Backward_still_or_huge_steps_advance_one_frame()
    {
        var clock = new FrameClock();
        clock.Next(10_000, 25);
        Assert.Equal(40, clock.Next(9_000, 25));        // backwards (stream restarted)
        Assert.Equal(80, clock.Next(9_000, 25));        // same stamp
        Assert.Equal(120, clock.Next(900_000, 25));     // jump (gap in the recording, wrap)
        Assert.Equal(160, clock.Next(900_040, 25));
        Assert.Equal(160 + 100, clock.Next(uint.MaxValue, 10)); // 10 fps nominal step
    }

    [Theory]
    [InlineData(25, 40)]
    [InlineData(0, 40)]
    [InlineData(30, 33)]
    [InlineData(500, 40)]
    [InlineData(1, 1000)]
    public void Nominal_interval(int fps, long expected) => Assert.Equal(expected, FrameClock.NominalInterval(fps));
}

public class RecordingTextTests
{
    [Fact]
    public void Messages()
    {
        var ok = new RecordingResult(@"C:\v\a.mp4", true, 10, TimeSpan.FromSeconds(1), 0, "libopenh264", null);
        var bad = new RecordingResult(@"C:\v\a.mp4", false, 0, TimeSpan.Zero, 0, null, "görüntü gelmedi");
        Assert.Equal(@"Kayıt kaydedildi: C:\v\a.mp4", RecordingText.Finished(ok, clip: false));
        Assert.Equal(@"Klip kaydedildi: C:\v\a.mp4", RecordingText.Finished(ok, clip: true));
        Assert.Equal("Klip kaydedilemedi: görüntü gelmedi", RecordingText.Finished(bad, clip: true));
        Assert.Equal("Kayıt başlatılamadı: x", RecordingText.StartFailed(false, "x"));
    }
}

/// <summary>
/// End-to-end: synthetic YV12 pictures (as PlayCtrl's decode callback delivers them) → <see cref="VideoRecorder"/> →
/// MP4, then the file is opened with FFmpeg's own demuxer and decoder. Needs the FFmpeg DLLs of the repo
/// (<c>ffmpeg\</c>, fetched by tools\get-ffmpeg.ps1); without them the tests pass without doing anything.
/// </summary>
public class VideoRecorderTests
{
    static readonly Lazy<bool> FfmpegLoaded = new(() =>
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SPVision.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        var ffmpeg = dir is null ? null : Path.Combine(dir, "ffmpeg");
        if (ffmpeg is null || !File.Exists(Path.Combine(ffmpeg, "avcodec-63.dll")))
        {
            return false;
        }
        LoadLibraries(ffmpeg, LoadProfile.All);
        av_log_set_level(LogLevel.Quiet);
        return true;
    });

    static byte[] Yv12(int width, int height, int index)
    {
        var data = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                data[y * width + x] = (byte)((x + y + index * 4) & 0xFF);
            }
        }
        data.AsSpan(width * height, width * height / 4).Fill(90);   // V
        data.AsSpan(width * height * 5 / 4).Fill(160);              // U
        return data;
    }

    static string TempFile() => Path.Combine(Path.GetTempPath(), $"spvision-test-{Guid.NewGuid():N}.mp4");

    static async Task<RecordingResult> Record(string path, int frames, int width, int height, IReadOnlyList<string>? encoders = null, int stampStep = 40)
    {
        var recorder = new VideoRecorder(path, encoders, queueCapacity: frames + 1);
        for (int i = 0; i < frames; i++)
        {
            Assert.True(recorder.TryAddYv12(Yv12(width, height, i), width, height, 5_000_000 + i * stampStep, 25));
        }
        return await recorder.Complete().WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Opens the file with FFmpeg: (codec, width, height, decoded frames, duration seconds).</summary>
    static unsafe (string Codec, int Width, int Height, int Decoded, double Seconds) Probe(string path)
    {
        AVFormatContext* format = null;
        AVDictionary* options = null;
        Assert.True(avformat_open_input(ref format, path, null, ref options) >= 0, "FFmpeg could not open the file");
        try
        {
            Assert.True(avformat_find_stream_info(format, (AVDictionary**)null) >= 0);
            Assert.Equal(1u, format->nb_streams);
            var stream = format->streams[0];
            var par = stream->codecpar;
            var codec = avcodec_find_decoder(par->codec_id);
            var context = avcodec_alloc_context3(codec);
            avcodec_parameters_to_context(context, par);
            Assert.True(avcodec_open2(context, codec, (AVDictionary**)null) >= 0);
            var packet = av_packet_alloc();
            var frame = av_frame_alloc();
            int decoded = 0;
            while (av_read_frame(format, packet) >= 0)
            {
                avcodec_send_packet(context, packet);
                av_packet_unref(packet);
                while (avcodec_receive_frame(context, frame) >= 0)
                {
                    decoded++;
                }
            }
            avcodec_send_packet(context, null);
            while (avcodec_receive_frame(context, frame) >= 0)
            {
                decoded++;
            }
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "";
            var seconds = format->duration / 1_000_000.0;
            av_frame_free(ref frame);
            av_packet_free(ref packet);
            avcodec_free_context(ref context);
            return (name, par->width, par->height, decoded, seconds);
        }
        finally
        {
            avformat_close_input(ref format);
        }
    }

    [Fact]
    public async Task Synthetic_frames_become_a_playable_h264_mp4()
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        try
        {
            var result = await Record(path, 50, 320, 240);
            Assert.True(result.Success, result.Error);
            Assert.Equal(50, result.Frames);
            Assert.InRange(result.Duration.TotalSeconds, 1.9, 2.1);
            var probe = Probe(path);
            Assert.Equal("h264", probe.Codec);
            Assert.Equal((320, 240), (probe.Width, probe.Height));
            Assert.Equal(50, probe.Decoded);
            Assert.InRange(probe.Seconds, 1.9, 2.1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("libopenh264", "h264")]
    [InlineData("mpeg4", "mpeg4")]
    public async Task Software_fallback_encoders_write_valid_mp4(string encoder, string codec)
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        try
        {
            var result = await Record(path, 25, 352, 288, ["no_such_encoder", encoder]);
            Assert.True(result.Success, result.Error);
            Assert.Equal(encoder, result.Encoder);
            var probe = Probe(path);
            Assert.Equal(codec, probe.Codec);
            Assert.Equal(25, probe.Decoded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Odd_size_and_irregular_stamps_still_give_a_valid_file()
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        try
        {
            var recorder = new VideoRecorder(path, ["libopenh264"], queueCapacity: 64);
            long[] stamps = [1000, 1040, 1040, 900, 1_000_000, 1_000_040];
            for (int i = 0; i < stamps.Length; i++)
            {
                recorder.TryAddYv12(Yv12(322, 242, i), 322, 242, stamps[i], 25);
            }
            var result = await recorder.Complete().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(result.Success, result.Error);
            var probe = Probe(path);
            Assert.Equal((322, 242), (probe.Width, probe.Height));
            Assert.Equal(stamps.Length, probe.Decoded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task No_frames_leaves_no_file_and_says_why()
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        var result = await new VideoRecorder(path).Complete().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Success);
        Assert.False(File.Exists(path));
        Assert.Contains("kare", result.Error);
    }

    [Fact]
    public async Task Full_queue_drops_instead_of_waiting_and_complete_is_idempotent()
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        try
        {
            var recorder = new VideoRecorder(path, ["libopenh264"], queueCapacity: 1);
            var frame = Yv12(640, 480, 0);
            for (int i = 0; i < 200; i++)
            {
                recorder.TryAddYv12(frame, 640, 480, i * 40, 25); // never blocks
            }
            var first = recorder.Complete();
            Assert.Same(first, recorder.Complete());
            Assert.False(recorder.TryAddYv12(frame, 640, 480, 9_000, 25)); // after completion: refused
            var result = await first.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(result.Success, result.Error);
            Assert.Equal(200, result.Frames + result.DroppedFrames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_add_waits_for_the_encoder_and_never_drops()
    {
        if (!FfmpegLoaded.Value)
        {
            return;
        }
        var path = TempFile();
        try
        {
            var recorder = new VideoRecorder(path, ["libopenh264"], queueCapacity: 1);
            var frame = Yv12(640, 480, 0);
            for (int i = 0; i < 120; i++)
            {
                Assert.True(recorder.AddYv12Waiting(frame, 640, 480, i * 40, 25, CancellationToken.None)); // waits, never drops
            }
            var result = await recorder.Complete().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(result.Success, result.Error);
            Assert.Equal(120, result.Frames);
            Assert.Equal(0, result.DroppedFrames);
            Assert.InRange(result.Duration.TotalSeconds, 4.7, 4.9); // presentation times as given: 120 × 40 ms
            Assert.Null(recorder.WriteError);
            Assert.Equal("libopenh264", recorder.EncoderName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_add_returns_when_cancelled()
    {
        var path = TempFile();
        var recorder = new VideoRecorder(path, ["no-such-encoder"], queueCapacity: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(recorder.AddYv12Waiting(new byte[64 * 64 * 3 / 2], 64, 64, 0, 25, cts.Token));
        await recorder.Complete().WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(path);
    }
}
