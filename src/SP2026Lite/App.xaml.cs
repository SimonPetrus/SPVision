using System.IO;
using System.Windows;
using FlyleafLib;
using SP2026Lite.Core;
using SP2026Lite.Hik;

namespace SP2026Lite;

public partial class App : Application
{
    public static SettingsStore Store { get; } = new();
    public static AppSettings Settings { get; private set; } = new();

    public static void SaveSettings() => Store.Save(Settings);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "SP-2026 Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            Settings = Store.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ayarlar okunamadı, boş ayarlarla başlatılıyor.\n{ex.Message}", "SP-2026 Lite");
            Settings = new AppSettings();
        }

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

    protected override void OnExit(ExitEventArgs e)
    {
        HikSdk.Shutdown();
        base.OnExit(e);
    }
}
