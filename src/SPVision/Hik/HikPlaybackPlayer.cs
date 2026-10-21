using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// One SDK playback session: NET_DVR_PlayBackByTime_V40 with the data callback fed into
/// PlayCtrl (same SYSHEAD pipeline as live view). Timeline position is the absolute time the stream carries for the
/// frame on screen (PlayM4_GetSystemTime on our port = the time the camera OSD shows); NET_DVR_PLAYGETTIME is only the
/// fallback (the SDK's estimate from the requested start: it did not see a recorder that started 49 min early, and it
/// fails without a play window). Seeks re-issue PlayBackByTime_V40 (PLAYSETTIME had no visible effect on the DS-8632NI-I8).
/// Speed and pause are "requested state" (<see cref="RequestedSpeedSteps"/>, <see cref="RequestedPaused"/>)
/// that <see cref="SyncControls"/> applies idempotently: the recorder side with NET_DVR_PLAYFAST /
/// NET_DVR_PLAYSLOW (one speed step each) and NET_DVR_PLAYPAUSE / NET_DVR_PLAYRESTART, the local
/// PlayCtrl port with the matching PlayM4_Fast / PlayM4_Slow / PlayM4_Pause so it consumes the data at
/// the same rate. A fresh open always starts at normal speed, so the steps are counted from there.
/// Callbacks arrive on SDK threads; UI code must dispatch. Control calls block: call them off the UI thread.
/// <para>
/// Locking rule (as in <see cref="HikLivePlayer"/>): the data callback never takes <see cref="_lock"/>, so the
/// control calls may hold it across SDK / PlayCtrl calls that wait for the SDK's data thread (save, stop,
/// speed). The lock keeps a handle or port from being released while another thread uses it; the state flags
/// are read without it, so the UI thread never waits for the recorder.
/// </para>
/// </summary>
public sealed class HikPlaybackPlayer : IDisposable
{
    readonly object _lock = new();

    HikNative.DataCallback? _callback;
    volatile int _playHandle = -1;
    int _port = -1;             // set by the data callback (stream header), read lock-free
    byte[]? _header;            // the stream header PlayCtrl was opened with (a clip port needs it too)
    HikFrameRecorder? _recorder; // MP4 clip in progress; read lock-free by the data callback
    Task<RecordingResult>? _lastRecording; // finalization of the clip a Stop ended (Dispose waits for it)
    int _timePackets;           // data packets since the open, while the first frame time is not known yet
    volatile bool _timeKnown;
    volatile bool _paused;
    int _appliedSteps;          // speed steps applied to the current handle (0 = normal)
    int _sdkPortTries;          // data packets left to look for the SDK's own port of this handle (motion boxes off)
    volatile bool _showMotionBoxes = true;
    volatile bool _started;
    volatile bool _disposed;

    /// <summary>Data packets after the stream header during which the SDK's own playback port is looked for.</summary>
    const int SdkPortTries = 200;

    public HikPlaybackPlayer()
    {
        HikSdk.ExceptionRaised += OnSdkException;
    }

    /// <summary>Raised on SDK threads. Carries a user-readable Turkish message.</summary>
    public event Action<string>? Error;

    /// <summary>Raised once when the first frame renders (SDK thread).</summary>
    public event Action? Started;

    /// <summary>Raised when the device reports end-of-recording (SDK thread).</summary>
    public event Action? Ended;

    /// <summary>
    /// Raised once per open, as soon as PlayCtrl knows the absolute time of the first decoded frame (SDK thread).
    /// The tile compares it with the requested time: the recorder sometimes starts at the beginning of the recording
    /// file instead (e.g. 23:11 for a request of 00:00 when the file began at 23:10).
    /// </summary>
    public event Action<DateTime>? FirstFrameTime;

    public bool IsRunning => _playHandle >= 0;
    public bool IsStarted => _started;

    /// <summary>
    /// Draw the recorder's motion / analysis boxes; read when the PlayCtrl port opens (every seek is a new handle
    /// and a new port, which start with PlayCtrl's default: drawn).
    /// </summary>
    public bool ShowMotionBoxes { get => _showMotionBoxes; set => _showMotionBoxes = value; }

    public bool IsSaving => Volatile.Read(ref _recorder) is not null;
    public bool IsPaused => _paused;

    /// <summary>Wanted speed relative to normal: +n = n × PLAYFAST (2^n ×), −n = n × PLAYSLOW (2^−n ×).</summary>
    public int RequestedSpeedSteps { get; set; }

    public bool RequestedPaused { get; set; }
    public bool IsDisposed => _disposed;

    /// <summary>Opens playback for an absolute time range; hWnd is the window PlayCtrl draws into.</summary>
    public void Start(int userId, int channel, DateTime begin, DateTime end, IntPtr hWnd, string? streamKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); // Dispose may have stopped us just before
            _started = false;
            _paused = false;
            _appliedSteps = 0;
            _sdkPortTries = 0;
            _timePackets = 0;
            _timeKnown = false;
            _callback = OnPlayData;
            streamKeyPending = string.IsNullOrEmpty(streamKey) ? null : streamKey;
            windowHandlePending = hWnd.ToInt32();

            var vod = new HikNative.VodPara
            {
                Size = (uint)Marshal.SizeOf<HikNative.VodPara>(),
                IdInfo = new HikNative.StreamInfo
                {
                    Size = (uint)Marshal.SizeOf<HikNative.StreamInfo>(),
                    Id = new byte[HikNative.StreamIdLen],
                    Channel = (uint)channel,
                    Res = new byte[32],
                },
                BeginTime = HikNative.DvrTime.From(begin),
                EndTime = HikNative.DvrTime.From(end),
                Wnd = hWnd,
                DrawFrame = 0,
                StreamType = 0,     // main stream
                UseAsync = 0,
                Res2 = new byte[19],
            };

            // The callback may run (stream header) before this returns; it needs no lock.
            int handle = HikNative.NET_DVR_PlayBackByTime_V40(userId, ref vod);
            if (handle < 0)
            {
                _callback = null;
                throw HikException.LastError($"Kayıt oynatma açılamadı (kanal {channel})");
            }
            _playHandle = handle;
            if (!HikNative.NET_DVR_SetPlayDataCallBack_V40(handle, _callback, IntPtr.Zero))
            {
                // Without the callback no data reaches PlayCtrl; abort cleanly.
                HikNative.NET_DVR_StopPlayBack(handle);
                _playHandle = -1;
                _callback = null;
                throw HikException.LastError("Oynatma veri geri çağrısı kurulamadı");
            }
            Control(handle, HikNative.PlayStart);
        }
    }

    /// <summary>Control call without in/out buffers (start/pause/resume/…).</summary>
    static bool Control(int handle, uint code)
    {
        uint len = 0;
        return HikNative.NET_DVR_PlayBackControl_V40(handle, code, IntPtr.Zero, 0, IntPtr.Zero, ref len);
    }

    /// <summary>Ends the clip and the playback. Blocking (NET_DVR_StopPlayBack): call it off the UI thread.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            int handle = _playHandle;
            _playHandle = -1;
            _paused = false;
            _started = false;
            // A clip ends with its stream (seek, park, dispose): finalized, never cut off.
            if (StopRecorder() is { } finishing)
            {
                _lastRecording = finishing;
            }
            if (handle >= 0)
            {
                HikNative.NET_DVR_StopPlayBack(handle); // the SDK stops calling back
            }
            // Read after StopPlayBack: the callback may have opened the port while we were stopping.
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

    /// <summary>
    /// Brings speed and pause of the running playback to <see cref="RequestedSpeedSteps"/> /
    /// <see cref="RequestedPaused"/>. Safe to call any number of times (only the difference is sent) and
    /// from any thread; before the first frame it does nothing (call it again on <see cref="Started"/>).
    /// Blocking (the recorder is asked over the network). Throws <see cref="HikException"/> when the
    /// recorder refuses a command.
    /// </summary>
    public void SyncControls()
    {
        lock (_lock)
        {
            int port = Volatile.Read(ref _port);
            if (_playHandle < 0 || port < 0 || !_started)
            {
                return;
            }
            var target = Math.Clamp(RequestedSpeedSteps, -4, 4);
            while (_appliedSteps < target)
            {
                if (!Control(_playHandle, HikNative.PlayFast))
                    throw HikException.LastError("Hızlandırılamadı");
                HikNative.PlayM4_Fast(port);
                Volatile.Read(ref _recorder)?.Fast();
                _appliedSteps++;
            }
            while (_appliedSteps > target)
            {
                if (!Control(_playHandle, HikNative.PlaySlow))
                    throw HikException.LastError("Yavaşlatılamadı");
                HikNative.PlayM4_Slow(port);
                Volatile.Read(ref _recorder)?.Slow();
                _appliedSteps--;
            }
            var pause = RequestedPaused;
            if (pause != _paused)
            {
                if (!Control(_playHandle, pause ? HikNative.PlayPause : HikNative.PlayRestart))
                    throw HikException.LastError(pause ? "Duraklatılamadı" : "Devam ettirilemedi");
                HikNative.PlayM4_Pause(port, pause ? 1u : 0u);
                Volatile.Read(ref _recorder)?.Pause(pause);
                _paused = pause;
            }
        }
    }

    /// <summary>
    /// Recorder wall-clock time of the frame on screen: the absolute time in the stream (PlayM4_GetSystemTime, what
    /// the OSD shows), else NET_DVR_PLAYGETTIME. Null when not playing, or when an open or control command is in
    /// progress (the UI asks often and must never wait for the network).
    /// </summary>
    public DateTime? GetTime()
    {
        if (!Monitor.TryEnter(_lock, 20))
        {
            return null;
        }
        try
        {
            if (_playHandle < 0) return null;
            if (FrameTime(Volatile.Read(ref _port)) is { } frameTime)
            {
                return frameTime;
            }
            var time = new HikNative.DvrTime();
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<HikNative.DvrTime>());
            try
            {
                Marshal.StructureToPtr(time, ptr, false);
                uint len = (uint)Marshal.SizeOf<HikNative.DvrTime>();
                if (!HikNative.NET_DVR_PlayBackControl_V40(_playHandle, HikNative.PlayGetTime, IntPtr.Zero, 0, ptr, ref len))
                    return null;
                time = Marshal.PtrToStructure<HikNative.DvrTime>(ptr);
                return time.ToDateTime();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Absolute time of the current frame of a PlayCtrl port (local call); null before the first frame.</summary>
    static DateTime? FrameTime(int port)
    {
        if (port < 0)
        {
            return null;
        }
        var time = new HikNative.Pm4SystemTime();
        return HikNative.PlayM4_GetSystemTime(port, ref time) ? time.ToDateTime() : null;
    }

    /// <summary>
    /// Starts an MP4 clip of the decrypted picture (<see cref="HikFrameRecorder"/>; the SDK's NET_DVR_PlayBackSaveData
    /// would store the encrypted PS stream). False when nothing plays. Takes the control lock (waits for a command in
    /// progress): off the UI thread.
    /// </summary>
    public bool StartSave(string path)
    {
        lock (_lock)
        {
            if (_disposed || _playHandle < 0 || Volatile.Read(ref _header) is not { } header) return false;
            if (Volatile.Read(ref _recorder) is not null) return true;
            var recorder = HikFrameRecorder.Start(header, streamKeyPending, fileMode: true, path);
            // The clip port consumes at the display port's rate: same speed steps and pause.
            for (int i = 0; i < _appliedSteps; i++) recorder.Fast();
            for (int i = 0; i > _appliedSteps; i--) recorder.Slow();
            if (_paused) recorder.Pause(true);
            Volatile.Write(ref _recorder, recorder);
            return true;
        }
    }

    /// <summary>
    /// Ends the clip (playback continues). The task ends when the MP4 is finalized and checked; null when no clip ran.
    /// Takes the control lock; PlayCtrl calls only.
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

    /// <summary>Grabs the currently displayed frame via PlayM4_GetJPEG; null when not playing.</summary>
    public string? Snapshot(string path)
    {
        lock (_lock)
        {
            int port = Volatile.Read(ref _port);
            if (port < 0 || !_started) return null;
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

    /// <summary>
    /// Switches the motion / analysis boxes on the running playback: our port and the SDK's own playback port
    /// (no-op before the ports open; the open applies <see cref="ShowMotionBoxes"/> itself). Takes the player
    /// lock, which control commands hold while the recorder answers: call it off the UI thread.
    /// </summary>
    public void SetMotionBoxes(bool show)
    {
        ShowMotionBoxes = show;
        lock (_lock)
        {
            int port = Volatile.Read(ref _port);
            if (_disposed || _playHandle < 0)
            {
                return;
            }
            if (port >= 0)
            {
                HikRender.SetMotionBoxes(port, -1, show);
            }
            if (HikRender.SetPlaybackMotionBoxes(_playHandle, port, show))
            {
                _sdkPortTries = 0;
            }
        }
    }

    void OnPlayData(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user)
    {
        try
        {
            // No lock anywhere in here (see the class remarks): control calls hold the lock while the recorder
            // answers or while the SDK waits for this thread, so taking it here could stall or deadlock both.
            // One SDK thread delivers a handle's data in order, so the header setup cannot race the data path;
            // Stop releases the port only after NET_DVR_StopPlayBack, when this callback is done.
            int port = Volatile.Read(ref _port);

            if (dataType == HikNative.SysHead)
            {
                if (port >= 0) return; // already set up

                if (!HikNative.PlayM4_GetPort(ref port))
                    throw HikException.LastError("PlayCtrl portu alınamadı");
                Volatile.Write(ref _port, port); // published at once, so Stop frees it even if the setup fails

                // Encrypted recordings: register the key before OpenStream (ClientDemo pattern).
                if (!string.IsNullOrEmpty(streamKeyPending))
                    HikNative.PlayM4_SetSecretKey(port, HikNative.AesKeyType, streamKeyPending, HikNative.AesKeyBits);

                HikNative.PlayM4_SetDecodeEngineEx(port, HikNative.HardDecodeEngine); // GPU decode
                HikNative.PlayM4_SetStreamOpenMode(port, HikNative.StreamFile); // file/kill mode for VOD
                if (!HikNative.PlayM4_OpenStream(port, buffer, size, 10 * 1024 * 1024))
                    throw HikException.LastError("PlayCtrl akışı açamadı");
                var header = new byte[size];
                Marshal.Copy(buffer, header, 0, (int)size);
                Volatile.Write(ref _header, header);
                HikNative.PlayM4_SetDisplayBuf(port, 15);
                HikNative.PlayM4_SetOverlayMode(port, false, 0);
                if (!ShowMotionBoxes)
                {
                    HikRender.SetMotionBoxes(port, -1, false); // drawn by default; only switch off
                }

                int hwndValue = windowHandlePending;
                if (!HikNative.PlayM4_Play(port, (IntPtr)hwndValue))
                    throw HikException.LastError("PlayCtrl oynatmayı başlatamadı");

                // The SDK draws this playback into the same window with its own port, which also starts
                // with the boxes on. It may open that port after this header: keep looking for a while.
                if (!ShowMotionBoxes && !HikRender.SetPlaybackMotionBoxes(handle, port, false))
                {
                    _sdkPortTries = SdkPortTries;
                }
                _started = true;
                Started?.Invoke();
                return;
            }

            if (dataType == HikNative.StreamData && port >= 0)
            {
                // Hot path: a plain counter check; the lookup is a local SDK call (no network).
                if (_sdkPortTries > 0)
                {
                    _sdkPortTries = !ShowMotionBoxes && !HikRender.SetPlaybackMotionBoxes(handle, port, false) ? _sdkPortTries - 1 : 0;
                }
                for (int i = 0; i < 50; i++)
                {
                    if (HikNative.PlayM4_InputData(port, buffer, size)) break;
                    Thread.Sleep(2);
                }
                Volatile.Read(ref _recorder)?.Input(buffer, size);
                // Until the first frame's absolute time is known: ask PlayCtrl every 8th packet (local, cheap).
                if (!_timeKnown && ++_timePackets % 8 == 0 && FrameTime(port) is { } first)
                {
                    _timeKnown = true;
                    FirstFrameTime?.Invoke(first);
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex.Message);
        }
    }

    void OnSdkException(uint type, int userId, int handle)
    {
        if (type == HikNative.ExceptionPlayback && handle == _playHandle)
        {
            Ended?.Invoke();
        }
    }

    // Start parameters needed inside the callback; set right before PlayBackByTime_V40.
    string? streamKeyPending;
    int windowHandlePending;

    /// <summary>
    /// Stops the playback and finalizes a clip still running (waits for its MP4: 5 s by default,
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
