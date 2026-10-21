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

    /// <summary>
    /// "Donanım hızlandırma" (<see cref="AppSettings.HardwareDecoding"/>) for both video paths: SDK display ports and Flyleaf
    /// players. Streams opened from now on use it; the main window reopens the running ones.
    /// </summary>
    public static void ApplyHardwareDecoding()
    {
        ApplyHardwareDecoding();
        Players.VideoAcceleration = Settings.HardwareDecoding;
    }

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

        // HCNetSDK for encrypted streams; no-op on failure. Log: errors only unless settings.json raises SdkLogLevel.
        HikSdk.Init(Path.Combine(logDir, "sdk"), SdkLog.Level(Settings.SdkLogLevel));
        HikDecode.PreferHardware = Settings.HardwareDecoding;

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
        StartExitWatchdog(); // also when the exit did not come through the main window's close
        ThemeManager.Shutdown();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cleaned = HikSdk.Shutdown(TimeSpan.FromSeconds(2));
        LogShutdown(cleaned ? $"SDK temizlendi ({watch.ElapsedMilliseconds} ms)" : "NET_DVR_Cleanup 2 sn içinde bitmedi; çıkılıyor");
        base.OnExit(e);
    }

    /// <summary>How long the whole close may take before the process is ended anyway.</summary>
    static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(8);

    static int _watchdogStarted;

    /// <summary>
    /// Safety net for the close: if the process is still alive <see cref="ExitLimit"/> after the close began (a
    /// native SDK call hanging, a window keeping the dispatcher alive…), it is logged and the process exits with
    /// <see cref="Environment.Exit"/>; should even that hang (e.g. in a DLL's unload), it is killed 3 s later. A
    /// lingering process would keep the exe and the SDK DLLs locked. Idempotent.
    /// </summary>
    public static void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref _watchdogStarted, 1) != 0)
        {
            return;
        }
        new Thread(() =>
        {
            Thread.Sleep(ExitLimit);
            LogShutdown($"Kapanış {ExitLimit.TotalSeconds:0} sn içinde bitmedi; süreç sonlandırılıyor (Environment.Exit)");
            new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }) { IsBackground = true }.Start();
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "SPVision kapanış bekçisi",
        }.Start();
    }

    /// <summary>One line in logs\shutdown.log (close diagnostics; never throws).</summary>
    public static void LogShutdown(string text)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(Store.Path)!, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shutdown.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
