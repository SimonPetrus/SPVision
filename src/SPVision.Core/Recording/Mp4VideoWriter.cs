using Flyleaf.FFmpeg;
using static Flyleaf.FFmpeg.Raw;

namespace SPVision.Core;

/// <summary>
/// Encodes decoded YUV 4:2:0 pictures into a standard MP4 (ISO base media, <c>moov</c> written on <see cref="Finish"/>)
/// with the FFmpeg that ships for FlyleafLib. H.264 without B-frames, so every player (Windows Media Player, Movies &amp;
/// TV, VLC) opens it. The encoder is tried in <see cref="DefaultEncoders"/> order: the GPU encoders first (little CPU),
/// then OpenH264 (software, in the LGPL FFmpeg build), Media Foundation, and MPEG-4 Part 2 as the last resort (still
/// a real MP4). FFmpeg must already be loaded (the app does it in <c>Engine.Start</c>; tests call
/// <c>Raw.LoadLibraries</c>). Not thread-safe: one thread writes (see <see cref="VideoRecorder"/>).
/// </summary>
public sealed unsafe class Mp4VideoWriter : IDisposable
{
    public static IReadOnlyList<string> DefaultEncoders { get; } =
        ["h264_nvenc", "h264_qsv", "h264_amf", "libopenh264", "h264_mf", "mpeg4"];

    /// <summary>Millisecond time base: the presentation times come from <see cref="FrameClock"/>.</summary>
    static readonly AVRational MsTimeBase = new() { Num = 1, Den = 1000 };

    AVFormatContext* _format;
    AVCodecContext* _encoder;
    AVStream* _stream;
    AVFrame* _frame;
    AVPacket* _packet;
    SwsContext* _scaler;
    int _scalerWidth;
    int _scalerHeight;
    long _lastPts = long.MinValue;
    long _frameInterval;
    bool _headerWritten;
    bool _finished;

    Mp4VideoWriter(string path, string encoderName)
    {
        Path = path;
        EncoderName = encoderName;
    }

    public string Path { get; }

    public string EncoderName { get; }

    /// <summary>Encoded size (even; a source with an odd size loses its last column / row).</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    public long FramesWritten { get; private set; }

    /// <summary>
    /// Creates the file and its encoder (header written). Throws <see cref="InvalidOperationException"/> with the
    /// FFmpeg reason when this encoder is not available here (no such GPU, missing library…); nothing is left on disk.
    /// </summary>
    public static Mp4VideoWriter Create(string path, int width, int height, int frameRate, string encoderName)
    {
        var writer = new Mp4VideoWriter(path, encoderName);
        try
        {
            writer.Open(width, height, frameRate);
            return writer;
        }
        catch
        {
            writer.Dispose();
            TryDelete(path);
            throw;
        }
    }

    void Open(int width, int height, int frameRate)
    {
        Width = width & ~1;
        Height = height & ~1;
        if (Width < 16 || Height < 16)
        {
            throw new InvalidOperationException($"görüntü boyutu geçersiz ({width}×{height})");
        }
        var fps = frameRate is >= 1 and <= 120 ? frameRate : 25;
        _frameInterval = FrameClock.NominalInterval(fps);

        var codec = avcodec_find_encoder_by_name(EncoderName);
        if (codec is null)
        {
            throw new InvalidOperationException($"{EncoderName} kodlayıcısı yok");
        }

        AVFormatContext* format = null;
        Check(avformat_alloc_output_context2(ref format, null, "mp4", Path), "MP4 yazıcısı");
        _format = format;

        _encoder = avcodec_alloc_context3(codec);
        if (_encoder is null)
        {
            throw new InvalidOperationException("kodlayıcı ayrılamadı");
        }
        _encoder->width = Width;
        _encoder->height = Height;
        _encoder->time_base = MsTimeBase;
        _encoder->framerate = new AVRational { Num = fps, Den = 1 };
        _encoder->pix_fmt = PickPixelFormat(codec);
        _encoder->bit_rate = Bitrate(Width, Height, fps);
        _encoder->gop_size = fps * 2; // a key frame every 2 s: the file can be seeked
        _encoder->max_b_frames = 0;   // no reordering: dts = pts, plays everywhere
        _encoder->flags |= CodecFlags.FrameDuration;
        if ((_format->oformat->flags & FmtFlags.Globalheader) != 0)
        {
            _encoder->flags |= CodecFlags.GlobalHeader; // avcC in the moov, as MP4 players expect
        }

        AVDictionary* options = null;
        try
        {
            switch (EncoderName)
            {
                case "h264_nvenc":
                    av_dict_set(ref options, "preset", "p4", DictWriteFlags.None);
                    break;
                case "libopenh264":
                    av_dict_set(ref options, "allow_skip_frames", "0", DictWriteFlags.None);
                    break;
                case "h264_mf":
                    av_dict_set(ref options, "rate_control", "cbr", DictWriteFlags.None);
                    break;
            }
            Check(avcodec_open2(_encoder, codec, ref options), $"{EncoderName} açılamadı");
        }
        finally
        {
            av_dict_free(ref options);
        }

        _stream = avformat_new_stream(_format, null);
        if (_stream is null)
        {
            throw new InvalidOperationException("MP4 akışı eklenemedi");
        }
        Check(avcodec_parameters_from_context(_stream->codecpar, _encoder), "akış bilgisi");
        _stream->time_base = MsTimeBase;
        _stream->avg_frame_rate = _encoder->framerate;

        Check(avio_open(ref _format->pb, Path, IOFlags.Write), "dosya açılamadı");
        Check(avformat_write_header(_format, (AVDictionary**)null), "MP4 başlığı yazılamadı");
        _headerWritten = true;

        _frame = av_frame_alloc();
        _packet = av_packet_alloc();
        if (_frame is null || _packet is null)
        {
            throw new InvalidOperationException("bellek ayrılamadı");
        }
        _frame->format = (int)_encoder->pix_fmt;
        _frame->width = Width;
        _frame->height = Height;
        Check(av_frame_get_buffer(_frame, 0), "kare belleği");
    }

    /// <summary>Bits per second: ~1 bit per 12 pixels per frame (1080p25 ≈ 4.3 Mb/s), 1 to 8 Mb/s.</summary>
    internal static long Bitrate(int width, int height, int fps) =>
        Math.Clamp((long)width * height * fps / 12, 1_000_000, 8_000_000);

    static AVPixelFormat PickPixelFormat(AVCodec* codec)
    {
        void* configs = null;
        int count = 0;
        if (avcodec_get_supported_config(null, codec, AVCodecConfig.PixFormat, 0, ref configs, &count) < 0 || configs is null || count <= 0)
        {
            return AVPixelFormat.YUV420P; // "any"
        }
        var formats = new ReadOnlySpan<AVPixelFormat>(configs, count);
        return formats.Contains(AVPixelFormat.YUV420P) ? AVPixelFormat.YUV420P
            : formats.Contains(AVPixelFormat.NV12) ? AVPixelFormat.NV12
            : formats[0];
    }

    /// <summary>
    /// Adds one picture given as I420 planes (Y, U, V; a YV12 buffer is the same with U and V swapped).
    /// <paramref name="ptsMs"/> must increase (<see cref="FrameClock"/>); a source of another size is scaled.
    /// </summary>
    public void WriteI420(byte* y, int yStride, byte* u, int uStride, byte* v, int vStride, int width, int height, long ptsMs)
    {
        if (_finished || !_headerWritten)
        {
            throw new InvalidOperationException("kayıt kapalı");
        }
        Check(av_frame_make_writable(_frame), "kare belleği");
        if (_encoder->pix_fmt == AVPixelFormat.YUV420P && width >= Width && height >= Height && width - Width < 2 && height - Height < 2)
        {
            av_image_copy_plane((byte*)_frame->data[0], _frame->linesize[0], y, yStride, Width, Height);
            av_image_copy_plane((byte*)_frame->data[1], _frame->linesize[1], u, uStride, Width / 2, Height / 2);
            av_image_copy_plane((byte*)_frame->data[2], _frame->linesize[2], v, vStride, Width / 2, Height / 2);
        }
        else
        {
            if (_scaler is null || _scalerWidth != width || _scalerHeight != height)
            {
                sws_freeContext(_scaler);
                _scaler = sws_getContext(width, height, AVPixelFormat.YUV420P, Width, Height, _encoder->pix_fmt,
                    SwsFlags.Bilinear, null, null, null);
                if (_scaler is null)
                {
                    throw new InvalidOperationException("görüntü dönüştürülemedi");
                }
                _scalerWidth = width;
                _scalerHeight = height;
            }
            var source = stackalloc byte*[4] { y, u, v, null };
            var strides = stackalloc int[4] { yStride, uStride, vStride, 0 };
            sws_scale(_scaler, source, strides, 0, height, (byte**)&_frame->data, (int*)&_frame->linesize);
        }
        var pts = ptsMs > _lastPts ? ptsMs : _lastPts + 1;
        _lastPts = pts;
        _frame->pts = pts;
        _frame->duration = _frameInterval;
        _frame->pict_type = AVPictureType.None;
        Check(avcodec_send_frame(_encoder, _frame), "kare kodlanamadı");
        Drain();
        FramesWritten++;
    }

    void Drain()
    {
        while (true)
        {
            var result = avcodec_receive_packet(_encoder, _packet);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF)
            {
                return;
            }
            Check(result, "kodlayıcı");
            av_packet_rescale_ts(_packet, _encoder->time_base, _stream->time_base);
            _packet->stream_index = _stream->index;
            Check(av_interleaved_write_frame(_format, _packet), "dosyaya yazılamadı"); // takes the packet's data
        }
    }

    /// <summary>Flushes the encoder and writes the MP4 index (<c>moov</c>). The file is complete afterwards.</summary>
    public void Finish()
    {
        if (_finished || !_headerWritten)
        {
            return;
        }
        _finished = true;
        try
        {
            if (avcodec_send_frame(_encoder, null) >= 0)
            {
                Drain();
            }
        }
        finally
        {
            Check(av_write_trailer(_format), "MP4 dizini yazılamadı");
            avio_closep(ref _format->pb);
        }
    }

    public void Dispose()
    {
        if (_headerWritten && !_finished)
        {
            try
            {
                Finish();
            }
            catch (InvalidOperationException)
            {
                // Keep releasing; the caller inspects the file anyway.
            }
        }
        if (_format is not null && _format->pb is not null)
        {
            avio_closep(ref _format->pb);
        }
        sws_freeContext(_scaler);
        _scaler = null;
        if (_frame is not null)
        {
            var frame = _frame;
            av_frame_free(ref frame);
            _frame = null;
        }
        if (_packet is not null)
        {
            var packet = _packet;
            av_packet_free(ref packet);
            _packet = null;
        }
        if (_encoder is not null)
        {
            var encoder = _encoder;
            avcodec_free_context(ref encoder);
            _encoder = null;
        }
        if (_format is not null)
        {
            avformat_free_context(_format);
            _format = null;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{what} ({ErrorText(result)})");
        }
    }

    static string ErrorText(int code)
    {
        var buffer = stackalloc byte[256];
        return av_strerror(code, buffer, 256) == 0
            ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"hata {code}"
            : $"hata {code}";
    }
}
