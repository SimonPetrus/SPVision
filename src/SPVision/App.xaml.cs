using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyleafLib;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision;

public partial class App : Application
{
    public static SettingsStore Store { get; } = new();
    public static AppSettings Settings { get; private set; } = new();

    public static void SaveSettings() => Store.Save(Settings);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DarkTitleBar.Register();
        RegisterWindowIcon();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "SPVision", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            // Formerly "SP-2026 Lite": copy the old settings once (old folder is left as is).
            LegacyMigration.CopySettingsIfMissing(Store.Path, AppPaths.LegacySettingsFile);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Not fatal: start with empty settings and let the user add the device again.
        }

        try
        {
            Settings = Store.Load();
            LegacyMigration.UpdateLegacyFolders(Settings); // new snapshots/recordings go to the new folders
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ayarlar okunamadı, boş ayarlarla başlatılıyor.\n{ex.Message}", "SPVision");
            Settings = new AppSettings();
        }

        ThemeManager.Initialize(); // light/dark from the setting (or Windows), before the first window

        var logDir = Path.Combine(Path.GetDirectoryName(Store.Path)!, "logs");
        Directory.CreateDirectory(logDir);

        HikSdk.Init(Path.Combine(logDir, "sdk")); // HCNetSDK for encrypted streams; no-op on failure

        Engine.Start(new EngineConfig
        {
            FFmpegPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg"),
            FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Warning,
            LogOutput = Path.Combine(logDir, "player.log"),
            LogLevel = LogLevel.Warn,
            LogRollMaxFileSize = 2 * 1024 * 1024,
            LogRollMaxFiles = 2,
            UIRefresh = true,
            UIRefreshInterval = 250,
        });

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Fallback: gives every window without its own icon (dialogs) the app icon on Loaded. MainWindow sets
    /// <c>Icon</c> in XAML, so its title bar and taskbar button have it before the window is shown. The .ico holds
    /// 16-256 px frames, so Windows gets a hand-drawn small icon instead of a scaled-down large one.
    /// </summary>
    static void RegisterWindowIcon()
    {
        ImageSource? icon;
        try
        {
            icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SPVision.ico", UriKind.Absolute),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            icon.Freeze();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            return; // not fatal: windows fall back to the exe icon
        }

        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window { Icon: null } window)
                {
                    window.Icon = icon;
                }
            }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        HikSdk.Shutdown();
        base.OnExit(e);
    }
}
