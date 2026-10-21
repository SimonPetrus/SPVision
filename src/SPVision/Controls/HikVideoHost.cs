using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SPVision.Controls;

/// <summary>
/// A plain child HWND PlayCtrl can draw into. The SDK renders with its own
/// DirectDraw/GDI pipeline, so WPF content cannot overlap it (airspace);
/// VideoTile shows camera name and badges in a header strip above it instead.
/// The window is transparent to hit testing: clicks, drags and drops over the video
/// go to the main window, where the tile handles them like any WPF input.
/// </summary>
public sealed class HikVideoHost : HwndHost
{
    const int WsChild = 0x4000_0000;
    const int WsVisible = 0x1000_0000;
    const int SsBlackRect = 0x0004;
    const int WmNcHitTest = 0x0084;
    const int HtTransparent = -1;
    const int WmMouseWheel = 0x020A;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyWindow(IntPtr hwnd);

    /// <summary>The HWND to pass to PlayM4_Play / NET_DVR preview.</summary>
    public IntPtr VideoHandle => Handle;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var hwnd = CreateWindowEx(0, "STATIC", null, WsChild | WsVisible | SsBlackRect,
            0, 0, 0, 0, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Video penceresi oluşturulamadı.");
        }
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    /// <summary>
    /// A wheel message that reached the video window itself (wheel delta, cursor in physical screen pixels); return true
    /// when it was used. Normally the wheel goes to the main window (the window is transparent to hit testing), but a
    /// wheel routed to the window under the cursor, or forwarded by a window PlayCtrl creates inside it, lands here; unused,
    /// it is passed on to the main window (DefWindowProc) as before.
    /// </summary>
    public Func<int, int, int, bool>? WheelHandler { get; set; }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }
        if (msg == WmMouseWheel && WheelHandler is { } wheel)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            var x = (short)(lParam.ToInt64() & 0xFFFF);          // screen coordinates (GET_X_LPARAM)
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            if (wheel(delta, x, y))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
}
