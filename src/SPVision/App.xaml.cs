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
        HikDecode.PreferHardware = Settings.HardwareDecoding;
        Players.VideoAcceleration = Settings.HardwareDecoding;
    }

    /// <summary>%APPDATA%\SPVision\logs (shutdown.log, crash.log, player.log; the SDK log under sdk\).</summary>
    public static string LogDirectory => Path.Combine(Path.GetDirectoryName(Store.Path)!, "logs");

    /// <summary>This process owns the session's single instance; null in a second launch that is exiting.</summary>
    static SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One SPVision per Windows session: a second launch brings the running one to the front and exits quietly
        // (Core SingleInstance; waits up to 10 s for a closing one — the updater relaunch). The newcomer is the
        // foreground process, so it lets the running one take the foreground.
        AllowSetForegroundWindow(AsfwAny);
        _instance = SingleInstance.Acquire();
        if (_instance is null)
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        RegisterCrashLog();
        DarkTitleBar.Register();
        RegisterWindowIcon();

        try
        {
            Settings = Store.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ayarlar okunamadı, boş ayarlarla başlatılıyor.\n{ex.Message}", "SPVision");
            Settings = new AppSettings();
        }

        ThemeManager.Initialize(); // light/dark from the setting (or Windows), before the first window
        Views.CertificateTrustUi.Register(); // HTTPS certificate pins: "sertifika değişti" question + saving (§8)

        var logDir = LogDirectory;
        Directory.CreateDirectory(logDir);

        // HCNetSDK for encrypted streams; no-op on failure. Log: errors only unless settings.json raises SdkLogLevel.
        HikSdk.Init(Path.Combine(logDir, "sdk"), SdkLog.Level(Settings.SdkLogLevel));
        ApplyHardwareDecoding();

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

        // Later launches: bring this window to the front (false while closing, so the newcomer waits and takes over).
        _instance.Listen(() =>
        {
            try
            {
                return Dispatcher.Invoke(() => (MainWindow as MainWindow)?.BringToFront() ?? false,
                    System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(1.5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                return false; // dispatcher shut down or busy
            }
        });
    }

    const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(int processId);

    // ---- Crash log (logs\crash.log, Core CrashLog: scrubbed, rolls at 1 MB, two files) ----

    static bool _errorShown;

    /// <summary>
    /// Every unhandled exception goes to logs\crash.log: the UI thread's (the app keeps running, a short message
    /// mentions the log), other threads' (the process ends after it) and unobserved tasks' (only logged).
    /// </summary>
    void RegisterCrashLog()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
            if (_errorShown)
            {
                return; // one box at a time: an error repeating on every frame must not stack dialogs
            }
            _errorShown = true;
            try
            {
                MessageBox.Show(
                    "Beklenmeyen bir hata oldu; SPVision çalışmaya devam ediyor. Bir şey düzgün çalışmazsa uygulamayı yeniden başlatın.\n\n" +
                    "Ayrıntılar hata günlüğüne yazıldı. Bize iletmek için: Ayarlar › Uygulama ayarları › Sorunu bildir…\n\n" +
                    LogScrubber.Scrub(args.Exception.Message),
                    "SPVision", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _errorShown = false;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.IsTerminating ? "UnhandledException (uygulama kapanıyor)" : "UnhandledException",
                args.ExceptionObject as Exception, args.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>One entry in logs\crash.log with version and OS; never throws.</summary>
    public static void LogCrash(string source, Exception? exception, string? fallback = null)
    {
        try
        {
            var entry = CrashLog.Entry(DateTimeOffset.Now, source, Updates.UpdateService.CurrentVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                exception?.ToString() ?? fallback ?? "(ayrıntı yok)");
            CrashLog.Append(LogDirectory, entry);
        }
        catch (Exception)
        {
            // the crash log itself must never crash the app
        }
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
        if (_instance is null)
        {
            base.OnExit(e); // second launch: nothing was started
            return;
        }
        StartExitWatchdog(); // also when the exit did not come through the main window's close
        ThemeManager.Shutdown();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cleaned = HikSdk.Shutdown(TimeSpan.FromSeconds(2));
        LogShutdown(cleaned ? $"SDK temizlendi ({watch.ElapsedMilliseconds} ms)" : "NET_DVR_Cleanup 2 sn içinde bitmedi; çıkılıyor");
        _instance.Dispose(); // released on the UI thread that acquired it; the next launch can start at once
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
            var dir = LogDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shutdown.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
