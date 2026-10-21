using System.Runtime.InteropServices;
using SP2026Lite.Core;

namespace SP2026Lite.Hik;

/// <summary>
/// One SDK live view: NET_DVR_RealPlay_V40 data callback fed into PlayCtrl
/// (GetPort → SetSecretKey → SetDecodeEngineEx → OpenStream → Play on an HWND),
/// following the official 2-Preview-NVRCsharpDemo / ClientDemo patterns.
/// Callbacks arrive on SDK threads; UI code must dispatch.
/// </summary>
public sealed class HikLivePlayer : IDisposable
{
    readonly object _lock = new();

    HikNative.DataCallback? _callback;
    int _realHandle = -1;
    int _port = -1;
    bool _saving;
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

    public bool IsRunning { get { lock (_lock) return _realHandle >= 0; } }
    public bool IsStarted => _started;
    public bool IsSaving { get { lock (_lock) return _saving; } }
    public bool IsDisposed => _disposed;

    /// <summary>userId: HCNetSDK login id; channel: ISAPI channel id (1-based); hWnd: window PlayCtrl draws into.</summary>
    public void Start(int userId, int channel, StreamQuality quality, IntPtr hWnd, string? streamKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        lock (_lock)
        {
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

            int handle = HikNative.NET_DVR_RealPlay_V40(userId, ref preview, _callback, IntPtr.Zero);
            if (handle < 0)
            {
                _callback = null;
                throw HikException.LastError($"Canlı görüntü açılamadı (kanal {channel})");
            }
            _realHandle = handle;
        }
    }

    public void Stop()
    {
        int handle, port;
        lock (_lock)
        {
            handle = _realHandle;
            port = _port;
            _realHandle = -1;
            _port = -1;
            _saving = false;
            _started = false;
            _callback = null;
        }
        if (handle >= 0) HikNative.NET_DVR_StopRealPlay(handle);
        if (port >= 0)
        {
            HikNative.PlayM4_Stop(port);
            HikNative.PlayM4_CloseStream(port);
            HikNative.PlayM4_FreePort(port);
        }
    }

    /// <summary>Saves the stream (as received, including the SDK's MP4 muxing) via NET_DVR_SaveRealData_V30.</summary>
    public void StartSave(string path)
    {
        lock (_lock)
        {
            if (_disposed || _realHandle < 0 || _saving) return;
            if (!HikNative.NET_DVR_SaveRealData_V30(_realHandle, HikNative.TransTypeMp4, path))
                throw HikException.LastError("Kayıt başlatılamadı");
            _saving = true;
        }
    }

    public void StopSave()
    {
        lock (_lock)
        {
            if (_realHandle < 0 || !_saving) return;
            HikNative.NET_DVR_StopSaveRealData(_realHandle);
            _saving = false;
        }
    }

    /// <summary>Grabs the currently displayed frame via PlayM4_GetJPEG; null when not playing.</summary>
    public string? Snapshot(string path)
    {
        lock (_lock)
        {
            if (_disposed || _port < 0 || !_started) return null;
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

    void OnRealData(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user)
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

                    // Encrypted streams: key must be registered before OpenStream (ClientDemo DlgPlayRemoteTime.cpp).
                    if (!string.IsNullOrEmpty(streamKeyPending))
                        HikNative.PlayM4_SetSecretKey(_port, HikNative.AesKeyType, streamKeyPending, HikNative.AesKeyBits);

                    HikNative.PlayM4_SetDecodeEngineEx(_port, HikNative.HardDecodeEngine); // GPU decode
                    HikNative.PlayM4_SetStreamOpenMode(_port, HikNative.StreamRealtime);
                    if (!HikNative.PlayM4_OpenStream(_port, buffer, size, 2 * 1024 * 1024))
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
        if (type == HikNative.ExceptionPreview && handle == Volatile.Read(ref _realHandle))
        {
            Error?.Invoke("Bağlantı koptu (önizleme)");
        }
    }

    // Start parameters needed inside the callback; set right before RealPlay_V40.
    string? streamKeyPending;
    int windowHandlePending;

    public void Dispose()
    {
        _disposed = true;
        HikSdk.ExceptionRaised -= OnSdkException;
        Stop();
    }
}
