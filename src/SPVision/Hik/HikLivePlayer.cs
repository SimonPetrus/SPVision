using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// One SDK live view: NET_DVR_RealPlay_V40 data callback fed into PlayCtrl
/// (GetPort → SetSecretKey → SetDecodeEngineEx → OpenStream → Play on an HWND),
/// following the official 2-Preview-NVRCsharpDemo / ClientDemo patterns.
/// Callbacks arrive on SDK threads; UI code must dispatch.
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
    volatile bool _saving;
    volatile bool _showMotionBoxes = true;
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
    public bool IsSaving => _saving;
    public bool IsDisposed => _disposed;

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
            windowHandlePending = hWnd.ToInt32();

            var preview = new HikNative.PreviewInfo
            {
                Channel = channel,
                StreamType = quality == StreamQuality.Main ? 0u : 1u, // 0 = main, 1 = sub
                LinkMode = 0,       // TCP
                PlayWnd = hWnd,     // also let the SDK know the window (used by PlayM4_Play)
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
            bool wasSaving = _saving;
            _realHandle = -1;
            _saving = false;
            _started = false;
            if (handle >= 0)
            {
                if (wasSaving) HikNative.NET_DVR_StopSaveRealData(handle);
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
                HikRender.SetMotionBoxes(port, _realHandle, show);
            }
        }
    }

    /// <summary>
    /// Saves the stream (as received, including the SDK's MP4 muxing) via NET_DVR_SaveRealData_V30.
    /// False when no stream runs. Blocking: the SDK waits for its data thread.
    /// </summary>
    public bool StartSave(string path)
    {
        lock (_lock)
        {
            if (_disposed || _realHandle < 0) return false;
            if (_saving) return true;
            if (!HikNative.NET_DVR_SaveRealData_V30(_realHandle, HikNative.TransTypeMp4, path))
                throw HikException.LastError("Kayıt başlatılamadı");
            _saving = true;
            return true;
        }
    }

    /// <summary>Ends the recording (the stream keeps running). Blocking.</summary>
    public void StopSave()
    {
        lock (_lock)
        {
            if (_realHandle < 0 || !_saving) return;
            HikNative.NET_DVR_StopSaveRealData(_realHandle);
            _saving = false;
        }
    }

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
                if (!HikNative.PlayM4_GetJPEG(port, buffer, maxJpeg, ref size) || size == 0)
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

                HikNative.PlayM4_SetDecodeEngineEx(port, HikNative.HardDecodeEngine); // GPU decode
                HikNative.PlayM4_SetStreamOpenMode(port, HikNative.StreamRealtime);
                if (!HikNative.PlayM4_OpenStream(port, buffer, size, 2 * 1024 * 1024))
                    throw HikException.LastError("PlayCtrl akışı açamadı");
                HikNative.PlayM4_SetDisplayBuf(port, 15);
                HikNative.PlayM4_SetOverlayMode(port, false, 0);
                if (!ShowMotionBoxes)
                {
                    // PlayCtrl draws them by default; only switch off (keeps today's behaviour when shown).
                    // handle = this RealPlay handle (the field may not be set yet while RealPlay_V40 returns).
                    HikRender.SetMotionBoxes(port, handle, false);
                }

                int hwndValue = windowHandlePending;
                if (!HikNative.PlayM4_Play(port, (IntPtr)hwndValue))
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
        if (type == HikNative.ExceptionPreview && handle == _realHandle)
        {
            Error?.Invoke("Bağlantı koptu (önizleme)");
        }
    }

    // Start parameters needed inside the callback; set right before RealPlay_V40.
    string? streamKeyPending;
    int windowHandlePending;

    /// <summary>Stops the stream (and the recording). Blocking: off the UI thread, except at shutdown.</summary>
    public void Dispose()
    {
        _disposed = true;
        HikSdk.ExceptionRaised -= OnSdkException;
        Stop();
    }
}
