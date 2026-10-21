using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// One SDK live view: NET_DVR_RealPlay_V40 data callback fed into PlayCtrl
/// (GetPort → SetSecretKey → OpenStream → hardware decoder → Play on an HWND),
/// following the official 2-Preview-NVRCsharpDemo / ClientDemo patterns.
/// Callbacks arrive on SDK threads; UI code must dispatch.
/// <para>
/// Single renderer (21.10.2026): RealPlay gets <b>no</b> window. With <c>hPlayWnd</c> set the SDK opened its own PlayCtrl
/// port and decoded + drew every stream a second time into the same window (probe: <c>NET_DVR_GetRealPlayerIndex</c> ≥ 0 on
/// all 22 tiles; 22 sub streams: 1.1 GB private, 234 threads → 0.8 GB, 191 threads without it). Only our port draws.
/// </para>
/// <para>
/// Locking rule: the data callback never takes <see cref="_lock"/>. The SDK holds its own stream lock while it
/// calls <see cref="OnRealData"/>, and calls such as NET_DVR_SaveRealData_V30 / NET_DVR_StopRealPlay wait for
/// that lock; with the callback waiting for ours in turn, StartSave froze the UI for good. <see cref="_lock"/>
/// only serializes the control calls (start, stop, save, snapshot, motion boxes), so a handle or port is never
/// released while another thread uses it. Those calls block: call them off the UI thread. The state flags are
/// read without the lock.
/// </para>
/// </summary>
public sealed class HikLivePlayer : IDisposable
{
    readonly object _lock = new();

    HikNative.DataCallback? _callback;
    volatile int _realHandle = -1;
    int _port = -1;             // set by the data callback (stream header), read lock-free
    byte[]? _header;            // the stream header PlayCtrl was opened with (a recording port needs it too)
    HikFrameRecorder? _recorder; // MP4 recording in progress; read lock-free by the data callback
    Task<RecordingResult>? _lastRecording; // finalization of the recording a Stop ended (Dispose waits for it)
    volatile bool _showMotionBoxes = true;
    volatile int _decoder;      // 0 unknown, 1 GPU, 2 CPU (engine of the display port, set when it opens)
    volatile bool _started;
    volatile bool _disposed;

    /// <summary>
    /// Constructed on the UI thread and owned by the tile; the (blocking) login session
    /// is passed to <see cref="Start"/> so construction never blocks the UI.
    /// </summary>
    public HikLivePlayer()
    {
        HikSdk.ExceptionRaised += OnSdkException;
    }

    /// <summary>Raised on SDK threads. Carries a user-readable Turkish message.</summary>
    public event Action<string>? Error;

    /// <summary>Raised once on the first successfully rendered frame (SDK thread).</summary>
    public event Action? Started;

    /// <summary>Draw the recorder's motion / analysis boxes; read when the PlayCtrl port opens.</summary>
    public bool ShowMotionBoxes { get => _showMotionBoxes; set => _showMotionBoxes = value; }

    public bool IsRunning => _realHandle >= 0;
    public bool IsStarted => _started;
    public bool IsSaving => Volatile.Read(ref _recorder) is not null;
    public bool IsDisposed => _disposed;

    /// <summary>The display port decodes on the GPU (true) or the CPU (false); null before the stream opens.</summary>
    public bool? HardwareDecoding => _decoder switch { 1 => true, 2 => false, _ => null };

    /// <summary>PlayCtrl display buffer of a live port in frames (the SDK demo's 15 only adds memory and latency).</summary>
    const uint DisplayFrames = 6;

    /// <summary>userId: HCNetSDK login id; channel: ISAPI channel id (1-based); hWnd: window PlayCtrl draws into.</summary>
    public void Start(int userId, int channel, StreamQuality quality, IntPtr hWnd, string? streamKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); // Dispose may have stopped us just before
            _started = false;
            _callback = OnRealData;
            streamKeyPending = string.IsNullOrEmpty(streamKey) ? null : streamKey;
            windowHandlePending = hWnd;

            var preview = new HikNative.PreviewInfo
            {
                Channel = channel,
                StreamType = quality == StreamQuality.Main ? 0u : 1u, // 0 = main, 1 = sub
                LinkMode = 0,       // TCP
                PlayWnd = IntPtr.Zero, // no SDK renderer: only our PlayCtrl port draws into hWnd (single renderer)
                Blocked = 0,
                PassbackRecord = 0,
                PreviewMode = 0,    // normal preview
                StreamId = new byte[HikNative.StreamIdLen],
                Res = new byte[212],
            };

            // The callback may already run (stream header) before this returns; it needs no lock.
            int handle = HikNative.NET_DVR_RealPlay_V40(userId, ref preview, _callback, IntPtr.Zero);
            if (handle < 0)
            {
                _callback = null;
                throw HikException.LastError($"Canlı görüntü açılamadı (kanal {channel})");
            }
            _realHandle = handle;
        }
    }

    /// <summary>Ends the recording and the stream. Blocking (NET_DVR_StopRealPlay): call it off the UI thread.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            int handle = _realHandle;
            _realHandle = -1;
            _started = false;
            _decoder = 0;
            // A recording ends with its stream (reconnect, quality switch, dispose): finalized, never cut off.
            if (StopRecorder() is { } finishing)
            {
                _lastRecording = finishing;
            }
            if (handle >= 0)
            {
                HikNative.NET_DVR_StopRealPlay(handle); // the SDK stops calling back
            }
            // Read after StopRealPlay: the callback may have opened the port while we were stopping.
            int port = Interlocked.Exchange(ref _port, -1);
            if (port >= 0)
            {
                HikNative.PlayM4_Stop(port);
                HikNative.PlayM4_CloseStream(port);
                HikNative.PlayM4_FreePort(port);
            }
            _callback = null; // only now: the SDK no longer calls it
            _header = null;
        }
    }

    /// <summary>Switches the motion / analysis boxes on the running stream (no-op before the port opens). Blocking.</summary>
    public void SetMotionBoxes(bool show)
    {
        ShowMotionBoxes = show;
        lock (_lock)
        {
            int port = Volatile.Read(ref _port);
            if (!_disposed && port >= 0)
            {
                HikRender.SetMotionBoxes(port, -1, show); // the SDK draws nothing itself (no window)
            }
        }
    }

    /// <summary>
    /// Starts an MP4 recording of the decrypted picture (<see cref="HikFrameRecorder"/>: a second PlayCtrl port decodes
    /// the same data, H.264 is encoded off this thread). False when no stream runs yet. Takes the control lock.
    /// </summary>
    public bool StartSave(string path)
    {
        lock (_lock)
        {
            if (_disposed || _realHandle < 0 || Volatile.Read(ref _header) is not { } header) return false;
            if (Volatile.Read(ref _recorder) is not null) return true;
            Volatile.Write(ref _recorder, HikFrameRecorder.Start(header, streamKeyPending, fileMode: false, path));
            return true;
        }
    }

    /// <summary>
    /// Ends the recording (the stream keeps running). The task ends when the MP4 is finalized and checked; null
    /// when nothing was recording. Takes the control lock; PlayCtrl calls only (no network).
    /// </summary>
    public Task<RecordingResult>? StopSave()
    {
        lock (_lock)
        {
            // A Stop (reopen) may have ended it already: then its finalization is handed over instead.
            return StopRecorder() ?? Interlocked.Exchange(ref _lastRecording, null);
        }
    }

    Task<RecordingResult>? StopRecorder() => Interlocked.Exchange(ref _recorder, null)?.Stop();

    /// <summary>Grabs the currently displayed frame via PlayM4_GetJPEG; null when not playing. Blocking.</summary>
    public string? Snapshot(string path)
    {
        lock (_lock)
        {
            int port = Volatile.Read(ref _port);
            if (_disposed || port < 0 || !_started) return null;
            const uint maxJpeg = 8 * 1024 * 1024;
            var buffer = Marshal.AllocHGlobal((nint)maxJpeg);
            try
            {
                uint size = 0;
                if (!HikNative.PlayM4_GetJPEG(port, buffer, maxJpeg, ref size) || size == 0 || size > maxJpeg)
                    return null;
                var data = new byte[size];
                Marshal.Copy(buffer, data, 0, (int)size);
                System.IO.File.WriteAllBytes(path, data);
                return path;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    void OnRealData(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user)
    {
        try
        {
            // No lock anywhere in here (see the class remarks): the SDK holds its stream lock while calling us.
            // One SDK thread delivers a handle's data in order, so the header setup cannot race the data path;
            // Stop releases the port only after NET_DVR_StopRealPlay, when this callback is done.
            int port = Volatile.Read(ref _port);

            if (dataType == HikNative.SysHead)
            {
                if (port >= 0) return; // already set up

                if (!HikNative.PlayM4_GetPort(ref port))
                    throw HikException.LastError("PlayCtrl portu alınamadı");
                Volatile.Write(ref _port, port); // published at once, so Stop frees it even if the setup fails

                // Encrypted streams: key must be registered before OpenStream (ClientDemo DlgPlayRemoteTime.cpp).
                if (!string.IsNullOrEmpty(streamKeyPending))
                    HikNative.PlayM4_SetSecretKey(port, HikNative.AesKeyType, streamKeyPending, HikNative.AesKeyBits);

                HikNative.PlayM4_SetStreamOpenMode(port, HikNative.StreamRealtime);
                if (!HikNative.PlayM4_OpenStream(port, buffer, size, 2 * 1024 * 1024))
                    throw HikException.LastError("PlayCtrl akışı açamadı");
                // GPU decode: only accepted between OpenStream and Play (before OpenStream: order error, CPU decode).
                _decoder = HikDecode.TryHardware(port) ? 1 : 2;
                var header = new byte[size];
                Marshal.Copy(buffer, header, 0, (int)size);
                Volatile.Write(ref _header, header);
                HikNative.PlayM4_SetDisplayBuf(port, DisplayFrames);
                HikNative.PlayM4_SetOverlayMode(port, false, 0);
                if (!ShowMotionBoxes)
                {
                    // PlayCtrl draws them by default; only switch off (keeps today's behaviour when shown).
                    HikRender.SetMotionBoxes(port, -1, false);
                }

                if (!HikNative.PlayM4_Play(port, windowHandlePending))
                    throw HikException.LastError("PlayCtrl oynatmayı başlatamadı");
                _started = true;
                Started?.Invoke();
                return;
            }

            if (dataType == HikNative.StreamData && port >= 0)
            {
                // The demo retries while the decoder buffer is full.
                for (int i = 0; i < 50; i++)
                {
                    if (HikNative.PlayM4_InputData(port, buffer, size)) break;
                    Thread.Sleep(2);
                }
                Volatile.Read(ref _recorder)?.Input(buffer, size);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    void OnSdkException(uint type, int userId, int handle)
    {
        if (type == HikNative.ExceptionPreview && handle == _realHandle)
        {
            Error?.Invoke("Bağlantı koptu (önizleme)");
        }
    }

    // Start parameters needed inside the callback; set right before RealPlay_V40.
    string? streamKeyPending;
    IntPtr windowHandlePending;

    /// <summary>
    /// Stops the stream and finalizes a recording still running (waits for its MP4: 5 s by default,
    /// or the given time). Blocking: off the UI thread.
    /// </summary>
    public void Dispose() => Dispose(TimeSpan.FromSeconds(5));

    public void Dispose(TimeSpan recordingWait)
    {
        _disposed = true;
        HikSdk.ExceptionRaised -= OnSdkException;
        Stop();
        try
        {
            Volatile.Read(ref _lastRecording)?.Wait(recordingWait);
        }
        catch (AggregateException)
        {
        }
    }
}
