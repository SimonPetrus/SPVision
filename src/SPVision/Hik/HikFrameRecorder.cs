using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// Records an SDK stream as a standard MP4 with the decrypted picture. The SDK's own save calls cannot do that
/// (they store the recorder's encrypted PS stream, see <see cref="HikNative"/>), and PlayCtrl exposes no decrypted
/// elementary stream, so the stream is decoded once more: a second PlayCtrl port — software decode, no window — gets
/// the stream header, the key and the same data packets as the display port. Its decode callback delivers YV12
/// pictures, which <see cref="VideoRecorder"/> encodes to H.264 on its own thread.
/// <para>
/// Threads: <see cref="Input"/> runs in the SDK data callback and never takes a lock (a counter lets
/// <see cref="Stop"/> wait until no callback still uses the port before it is freed). The decode callback runs on
/// PlayCtrl's decode thread and only queues the picture (never waits), so <c>PlayM4_Stop</c> can always join it.
/// Start / Stop / speed calls are local PlayCtrl calls; the players make them under their own control lock.
/// </para>
/// </summary>
sealed class HikFrameRecorder
{
    /// <summary>Source buffer of the recording port (as the display ports: 2 MB live, 10 MB playback).</summary>
    const uint LiveBuffer = 4 * 1024 * 1024;
    const uint PlaybackBuffer = 10 * 1024 * 1024;

    readonly VideoRecorder _recorder;
    readonly HikNative.DecodeCallback _decode; // kept alive while PlayCtrl may call it
    int _port = -1;
    int _feeders;

    HikFrameRecorder(string path)
    {
        _recorder = new VideoRecorder(path);
        _decode = OnDecoded;
    }

    public string Path => _recorder.Path;

    /// <summary>
    /// Opens the recording port with the stream header the display port got. <paramref name="fileMode"/>: playback
    /// (paced like the display port, which also runs in file mode). Throws <see cref="HikException"/>.
    /// </summary>
    public static HikFrameRecorder Start(byte[] header, string? key, bool fileMode, string path)
    {
        var recorder = new HikFrameRecorder(path);
        int port = -1;
        if (!HikNative.PlayM4_GetPort(ref port))
        {
            recorder._recorder.Complete();
            throw new HikException(0, "Kayıt için PlayCtrl portu alınamadı");
        }
        try
        {
            if (!string.IsNullOrEmpty(key))
            {
                HikNative.PlayM4_SetSecretKey(port, HikNative.AesKeyType, key, HikNative.AesKeyBits);
            }
            // Software decoding: the decode callback needs pictures in system memory. (Measured 21.10.2026: with the hardware
            // engine the callback still fires but with size 0 — no picture — so downloads / recordings cannot use the GPU.)
            HikNative.PlayM4_SetDecodeEngineEx(port, HikNative.SoftDecodeEngine);
            HikNative.PlayM4_SetStreamOpenMode(port, fileMode ? HikNative.StreamFile : HikNative.StreamRealtime);
            var pinned = GCHandle.Alloc(header, GCHandleType.Pinned);
            try
            {
                if (!HikNative.PlayM4_OpenStream(port, pinned.AddrOfPinnedObject(), (uint)header.Length, fileMode ? PlaybackBuffer : LiveBuffer))
                {
                    throw new HikException(HikNative.PlayM4_GetLastError(port), "Kayıt akışı açılamadı (PlayCtrl)");
                }
            }
            finally
            {
                pinned.Free();
            }
            HikNative.PlayM4_SetDecCBStream(port, HikNative.DecodeVideoOnly);
            if (!HikNative.PlayM4_SetDecCallBackMend(port, recorder._decode, 0) || !HikNative.PlayM4_Play(port, IntPtr.Zero))
            {
                throw new HikException(HikNative.PlayM4_GetLastError(port), "Kayıt kod çözücüsü başlatılamadı (PlayCtrl)");
            }
        }
        catch
        {
            HikNative.PlayM4_Stop(port);
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
            recorder._recorder.Complete();
            throw;
        }
        Volatile.Write(ref recorder._port, port);
        return recorder;
    }

    /// <summary>Same data packet as the display port (SDK data callback thread; lock-free, waits 50 ms at most).</summary>
    public void Input(IntPtr buffer, uint size)
    {
        Interlocked.Increment(ref _feeders);
        try
        {
            int port = Volatile.Read(ref _port);
            if (port < 0)
            {
                return;
            }
            for (int i = 0; i < 25; i++)
            {
                if (HikNative.PlayM4_InputData(port, buffer, size))
                {
                    return;
                }
                Thread.Sleep(2);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _feeders);
        }
    }

    /// <summary>Playback speed / pause, mirrored from the display port so the recording port consumes at the same rate.</summary>
    public void Fast() => WithPort(port => HikNative.PlayM4_Fast(port));

    public void Slow() => WithPort(port => HikNative.PlayM4_Slow(port));

    public void Pause(bool pause) => WithPort(port => HikNative.PlayM4_Pause(port, pause ? 1u : 0u));

    void WithPort(Action<int> action)
    {
        int port = Volatile.Read(ref _port);
        if (port >= 0)
        {
            action(port);
        }
    }

    /// <summary>
    /// Ends the recording: the port stops getting data and is released (PlayCtrl joins its decode thread), then the
    /// encoder finishes in the background. The task ends when the MP4 is finalized and checked. Idempotent.
    /// </summary>
    public Task<RecordingResult> Stop()
    {
        int port = Interlocked.Exchange(ref _port, -1);
        if (port >= 0)
        {
            // A data callback that read the port just before may still be inside PlayM4_InputData: let it leave.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref _feeders) > 0 && wait.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(1);
            }
            HikNative.PlayM4_Stop(port);
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
        }
        return _recorder.Complete();
    }

    void OnDecoded(int port, IntPtr buffer, int size, ref HikNative.FrameInfo info, int user, int reserved)
    {
        try
        {
            if (info.Type == HikNative.FrameTypeYv12)
            {
                // Stamp: 32-bit stream milliseconds; read unsigned so a wrap is a jump, not a negative time.
                _recorder.TryAddYv12(buffer, size, info.Width, info.Height, (uint)info.Stamp, info.FrameRate);
            }
        }
        catch (Exception)
        {
            // Never let an exception reach PlayCtrl's decode thread; a lost picture is counted as dropped.
        }
    }
}
