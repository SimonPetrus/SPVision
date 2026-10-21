using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// One SDK playback session: NET_DVR_PlayBackByTime_V40 with the data callback fed into
/// PlayCtrl (same SYSHEAD pipeline as live view). Timeline position comes from
/// NET_DVR_PlayBackControl_V40(NET_DVR_PLAYGETTIME); seeks re-issue PlayBackByTime_V40
/// (PLAYSETTIME support varies by device, a fresh open is always reliable).
/// Callbacks arrive on SDK threads; UI code must dispatch.
/// </summary>
public sealed class HikPlaybackPlayer : IDisposable
{
    readonly object _lock = new();

    HikNative.DataCallback? _callback;
    int _playHandle = -1;
    int _port = -1;
    bool _saving;
    bool _paused;
    volatile bool _started;
    volatile bool _disposed;

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

    public bool IsRunning { get { lock (_lock) return _playHandle >= 0; } }
    public bool IsStarted => _started;
    public bool IsSaving { get { lock (_lock) return _saving; } }
    public bool IsPaused { get { lock (_lock) return _paused; } }
    public bool IsDisposed => _disposed;

    /// <summary>Opens playback for an absolute time range; hWnd is the window PlayCtrl draws into.</summary>
    public void Start(int userId, int channel, DateTime begin, DateTime end, IntPtr hWnd, string? streamKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        lock (_lock)
        {
            _started = false;
            _paused = false;
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

    public void Stop()
    {
        bool wasSaving;
        int handle, port;
        lock (_lock)
        {
            handle = _playHandle;
            port = _port;
            wasSaving = _saving;
            _playHandle = -1;
            _port = -1;
            _saving = false;
            _paused = false;
            _started = false;
            _callback = null;
        }
        if (handle >= 0)
        {
            if (wasSaving) HikNative.NET_DVR_StopPlayBackSave(handle);
            HikNative.NET_DVR_StopPlayBack(handle);
        }
        if (port >= 0)
        {
            HikNative.PlayM4_Stop(port);
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
        }
    }

    public bool Pause()
    {
        lock (_lock)
        {
            if (_playHandle < 0 || _paused) return _paused;
            if (!Control(_playHandle, HikNative.PlayPause))
                throw HikException.LastError("Duraklatılamadı");
            _paused = true;
            return true;
        }
    }

    public bool Resume()
    {
        lock (_lock)
        {
            if (_playHandle < 0) return false;
            if (!_paused) return true;
            if (!Control(_playHandle, HikNative.PlayRestart))
                throw HikException.LastError("Devam ettirilemedi");
            _paused = false;
            return true;
        }
    }

    /// <summary>Recorder wall-clock time of the frame on screen; null when not playing.</summary>
    public DateTime? GetTime()
    {
        lock (_lock)
        {
            if (_playHandle < 0) return null;
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
    }

    /// <summary>Saves the stream as received (SDK muxes to MP4) via NET_DVR_PlayBackSaveData.</summary>
    public void StartSave(string path)
    {
        lock (_lock)
        {
            if (_playHandle < 0 || _saving) return;
            if (!HikNative.NET_DVR_PlayBackSaveData(_playHandle, path))
                throw HikException.LastError("Klip kaydı başlatılamadı");
            _saving = true;
        }
    }

    public void StopSave()
    {
        lock (_lock)
        {
            if (_playHandle < 0 || !_saving) return;
            HikNative.NET_DVR_StopPlayBackSave(_playHandle);
            _saving = false;
        }
    }

    /// <summary>Grabs the currently displayed frame via PlayM4_GetJPEG; null when not playing.</summary>
    public string? Snapshot(string path)
    {
        lock (_lock)
        {
            if (_port < 0 || !_started) return null;
            const uint maxJpeg = 8 * 1024 * 1024;
            var buffer = Marshal.AllocHGlobal((nint)maxJpeg);
            try
            {
                uint size = 0;
                if (!HikNative.PlayM4_GetJPEG(_port, buffer, maxJpeg, ref size) || size == 0)
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

    void OnPlayData(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user)
    {
        try
        {
            int port;
            lock (_lock) port = _port;

            if (dataType == HikNative.SysHead)
            {
                lock (_lock)
                {
                    if (_port >= 0) return; // already set up

                    if (!HikNative.PlayM4_GetPort(ref _port))
                        throw HikException.LastError("PlayCtrl portu alınamadı");

                    // Encrypted recordings: register the key before OpenStream (ClientDemo pattern).
                    if (!string.IsNullOrEmpty(streamKeyPending))
                        HikNative.PlayM4_SetSecretKey(_port, HikNative.AesKeyType, streamKeyPending, HikNative.AesKeyBits);

                    HikNative.PlayM4_SetDecodeEngineEx(_port, HikNative.HardDecodeEngine); // GPU decode
                    HikNative.PlayM4_SetStreamOpenMode(_port, HikNative.StreamFile); // file/kill mode for VOD
                    if (!HikNative.PlayM4_OpenStream(_port, buffer, size, 10 * 1024 * 1024))
                        throw HikException.LastError("PlayCtrl akışı açamadı");
                    HikNative.PlayM4_SetDisplayBuf(_port, 15);
                    HikNative.PlayM4_SetOverlayMode(_port, false, 0);

                    int hwndValue = windowHandlePending;
                    if (!HikNative.PlayM4_Play(_port, (IntPtr)hwndValue))
                        throw HikException.LastError("PlayCtrl oynatmayı başlatamadı");
                }
                _started = true;
                Started?.Invoke();
                return;
            }

            if (dataType == HikNative.StreamData && port >= 0)
            {
                for (int i = 0; i < 50; i++)
                {
                    if (HikNative.PlayM4_InputData(port, buffer, size)) return;
                    Thread.Sleep(2);
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
        if (type == HikNative.ExceptionPlayback && handle == Volatile.Read(ref _playHandle))
        {
            Ended?.Invoke();
        }
    }

    // Start parameters needed inside the callback; set right before PlayBackByTime_V40.
    string? streamKeyPending;
    int windowHandlePending;

    public void Dispose()
    {
        _disposed = true;
        HikSdk.ExceptionRaised -= OnSdkException;
        Stop();
    }
}
