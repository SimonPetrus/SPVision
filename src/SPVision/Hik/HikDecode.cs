namespace SPVision.Hik;

/// <summary>
/// Hardware (GPU) decoding of a PlayCtrl display port. PlayCtrl decodes with DXVA on the adapter that drives the play
/// window (on this laptop the Intel UHD, "GPU 0": its VideoDecode engine shows the load). Must be called after
/// <c>PlayM4_OpenStream</c> and before <c>PlayM4_Play</c>; earlier calls fail with PLAYM4_ORDER_ERROR and the port decodes
/// on the CPU (that was the case until 21.10.2026 although the status bar said "GPU çözme"). Ports without a window
/// (recording / download ports with a decode callback) keep software decoding: their callback needs pictures in system memory.
/// </summary>
static class HikDecode
{
    /// <summary>
    /// Hardware decoding for display ports opened from now on ("Donanım hızlandırma"; off = every port decodes on the CPU).
    /// Read when a port opens: running streams switch only when they are reopened (<see cref="App.ApplyHardwareDecoding"/>).
    /// </summary>
    public static volatile bool PreferHardware = true;

    /// <summary>
    /// Switches the port to the GPU decoder. True only when PlayCtrl accepted it and reports the hardware engine in
    /// effect; false keeps (or falls back to) software decoding. Never throws.
    /// </summary>
    public static bool TryHardware(int port)
    {
        if (!PreferHardware || port < 0)
        {
            return false;
        }
        try
        {
            return HikNative.PlayM4_SetDecodeEngineEx(port, HikNative.HardDecodeEngine)
                && HikNative.PlayM4_GetDecodeEngine(port) == HikNative.HardDecodeEngine;
        }
        catch (EntryPointNotFoundException)
        {
            return false; // an older PlayCtrl without the call: software decoding
        }
    }

    /// <summary>Engine in effect on a running port: true = GPU, false = CPU, null = unknown (no port / no such call).</summary>
    public static bool? IsHardware(int port)
    {
        if (port < 0)
        {
            return null;
        }
        try
        {
            return HikNative.PlayM4_GetDecodeEngine(port) == HikNative.HardDecodeEngine;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}
