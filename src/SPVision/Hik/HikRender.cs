namespace SPVision.Hik;

/// <summary>
/// Turns the recorder's private-data overlay (motion / intelligent-analysis boxes) on or off. Display only:
/// these calls change how PlayCtrl and the SDK draw the video, nothing is sent to the device.
/// </summary>
static class HikRender
{
    /// <summary>
    /// port: our PlayCtrl port. realHandle: the NET_DVR_RealPlay_V40 handle, or -1 for playback. The live
    /// preview also passes the window to RealPlay, so the SDK's own renderer gets the same switch.
    /// </summary>
    public static void SetMotionBoxes(int port, int realHandle, bool show)
    {
        foreach (var type in HikNative.MotionBoxRenderTypes)
        {
            try
            {
                if (port >= 0)
                {
                    HikNative.PlayM4_RenderPrivateData(port, type, show);
                }
                if (realHandle >= 0)
                {
                    HikNative.NET_DVR_RenderPrivateData(realHandle, type, show);
                }
            }
            catch (EntryPointNotFoundException)
            {
                return; // older DLLs without the call: leave the default rendering
            }
        }
    }

    /// <summary>
    /// Playback: the SDK decodes and draws the stream into the window given to NET_DVR_PlayBackByTime_V40 with
    /// its own PlayCtrl port (the playback counterpart of the RealPlay renderer above). Same switch on that port,
    /// so its boxes do not show over ours. Returns false while the SDK has not opened that port yet.
    /// </summary>
    public static bool SetPlaybackMotionBoxes(int playHandle, int ownPort, bool show)
    {
        if (playHandle < 0)
        {
            return false;
        }
        int sdkPort;
        try
        {
            sdkPort = HikNative.NET_DVR_GetPlayBackPlayerIndex(playHandle);
        }
        catch (EntryPointNotFoundException)
        {
            return true; // older DLLs: nothing to find, stop asking
        }
        if (sdkPort < 0 || sdkPort == ownPort)
        {
            return false;
        }
        SetMotionBoxes(sdkPort, -1, show);
        return true;
    }
}
