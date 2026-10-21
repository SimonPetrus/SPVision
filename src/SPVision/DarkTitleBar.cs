using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SPVision;

/// <summary>Asks DWM for the dark native title bar (Windows 10 20H1+ / Windows 11).</summary>
static class DarkTitleBar
{
    const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies the dark title bar to every window of the app as soon as its HWND exists.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));
    }

    /// <summary>Call from a window constructor to get the dark bar before the first paint.</summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => Apply(window);

    static void Apply(Window window)
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
        var enabled = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }
}
