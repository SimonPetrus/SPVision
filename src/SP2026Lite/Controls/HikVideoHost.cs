using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SP2026Lite.Controls;

/// <summary>
/// A plain child HWND PlayCtrl can draw into. The SDK renders with its own
/// DirectDraw/GDI pipeline, so WPF content cannot overlap it (airspace);
/// overlays live in VideoTile's Popup instead.
/// </summary>
public sealed class HikVideoHost : HwndHost
{
    const int WsChild = 0x4000_0000;
    const int WsVisible = 0x1000_0000;
    const int SsBlackRect = 0x0004;

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
}
