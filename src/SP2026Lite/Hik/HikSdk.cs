namespace SP2026Lite.Hik;

/// <summary>
/// Global HCNetSDK lifecycle: NET_DVR_Init once at startup, NET_DVR_Cleanup at exit,
/// connect/reconnect behaviour and the SDK exception callback (GELISTIRICI_GOREVI.md §1).
/// </summary>
public static class HikSdk
{
    static HikNative.ExceptionCallback? _exceptionCallback;

    /// <summary>True when NET_DVR_Init succeeded (SDK DLLs found and initialized).</summary>
    public static bool Available { get; private set; }

    /// <summary>
    /// Raised from SDK threads — NOT the UI thread. Handlers must dispatch themselves.
    /// Args: (exceptionType, userId, handle). Types: 0x8003 preview lost, 0x8005 reconnect, 0x8010 playback.
    /// </summary>
    public static event Action<uint, int, int>? ExceptionRaised;

    /// <summary>Idempotent. A failure (e.g. missing DLLs) never throws — the FFmpeg path keeps working.</summary>
    public static void Init(string? sdkLogDirectory = null)
    {
        if (Available) return;
        try
        {
            Available = HikNative.NET_DVR_Init();
            if (!Available) return;

            HikNative.NET_DVR_SetConnectTime(5_000, 1);
            HikNative.NET_DVR_SetReconnect(30_000, true);

            _exceptionCallback = OnException; // keep the delegate alive for the SDK's lifetime
            HikNative.NET_DVR_SetExceptionCallBack_V30(0, IntPtr.Zero, _exceptionCallback, IntPtr.Zero);

            if (sdkLogDirectory is not null)
            {
                System.IO.Directory.CreateDirectory(sdkLogDirectory);
                HikNative.NET_DVR_SetLogToFile(3, sdkLogDirectory, true);
            }
        }
        catch
        {
            // Missing SDK DLLs or a failed init must not break startup; video layers check Available.
            Available = false;
        }
    }

    public static void Shutdown()
    {
        if (!Available) return;
        Available = false;
        try { HikNative.NET_DVR_Cleanup(); }
        catch { /* ignore during process exit */ }
    }

    static void OnException(uint type, int userId, int handle, IntPtr user) =>
        ExceptionRaised?.Invoke(type, userId, handle);
}
