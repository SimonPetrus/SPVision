using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SPVision;

/// <summary>
/// Native title bar colour (Windows 10 20H1+ / Windows 11): DWMWA_USE_IMMERSIVE_DARK_MODE = 1 for the dark theme,
/// 0 for the light one. Follows <see cref="ThemeManager"/>; re-applied to every open window on a theme switch.
/// </summary>
static class DarkTitleBar
{
    const int DwmwaUseImmersiveDarkMode = 20;
    const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    static bool _dark = true;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Applies the title bar colour to every window of the app as soon as its HWND exists.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender, redraw: false)));
    }

    /// <summary>Call from a window constructor to get the right bar before the first paint.</summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window, redraw: false);

    /// <summary>Theme switched: store the mode and re-apply it to all open windows.</summary>
    public static void SetDark(bool dark)
    {
        var changed = dark != _dark;
        _dark = dark;
        if (!changed || Application.Current is null)
        {
            return;
        }
        foreach (Window window in Application.Current.Windows)
        {
            Apply(window, redraw: true);
        }
    }

    /// <summary>A window got its caption back (e.g. leaving full screen): make sure it has the current mode.</summary>
    public static void Reapply(Window window) => Apply(window, redraw: true);

    static void Apply(Window window, bool redraw)
    {
        // Borderless helper windows (e.g. Flyleaf's video surface) have no title bar.
        if (window.WindowStyle == WindowStyle.None)
        {
            return;
        }
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        var value = _dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (redraw)
        {
            // An already painted caption only picks up the new mode after a frame change.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }
}
