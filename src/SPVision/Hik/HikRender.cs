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
}
