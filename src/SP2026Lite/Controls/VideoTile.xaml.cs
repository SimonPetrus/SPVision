using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib.MediaPlayer;
using SP2026Lite.Core;
using SP2026Lite.Hik;

namespace SP2026Lite.Controls;

/// <summary>
/// One cell of the live wall. Two rendering paths share the tile:
/// Flyleaf (FFmpeg, unencrypted RTSP) and the Hikvision SDK (PlayCtrl drawing into
/// <see cref="HikHost"/> — the only way to decode stream-encrypted cameras).
/// The overlay lives in <see cref="OverlayPopup"/> because WPF content cannot
/// cover the SDK's video HWND (airspace).
/// </summary>
public partial class VideoTile : UserControl
{
    public const string DragFormat = "SP2026Lite.CameraRef";

    /// <summary>Added next to <see cref="DragFormat"/> when the drag starts from a tile (swap, not copy).</summary>
    const string TileDragFormat = "SP2026Lite.VideoTile";

    static readonly Brush IdleBorder = new SolidColorBrush(Color.FromRgb(0x2C, 0x30, 0x39));
    static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // Changing the offset forces an open Popup to re-evaluate its position against
    // PlacementTarget — a plain public-API way to make it follow the tile.
    const double OverlayNudge = 0.001;

    readonly System.Windows.Threading.DispatcherTimer _retryTimer = new();

    Player? _player;             // FFmpeg path
    HikLivePlayer? _hikPlayer;   // SDK path (stream encryption)
    bool _preferHik;             // sticky: once the SDK path proved necessary, try it first
    bool _triedHikFallback;      // one FFmpeg → SDK fallback per Open cycle
    int _openGeneration;         // cancels SDK opens superseded by a newer Open/Dispose

    bool _wantPlaying;
    int _retryCount;
    StreamQuality _quality = StreamQuality.Sub;
    StreamQuality _autoQuality = StreamQuality.Sub;
    string? _pendingRecordFile;

    Window? _rootWindow;
    bool _rootActive = true;
    Point? _dragOrigin;

    public VideoTile()
    {
        InitializeComponent();
        ShowEmpty();
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            if (_wantPlaying)
            {
                Open(_quality);
            }
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Frame.SizeChanged += (_, _) => NudgeOverlay();
    }

    public event Action<VideoTile>? Activated;
    public event Action<VideoTile>? DoubleClicked;
    public event Action<VideoTile, CameraRef>? CameraDropped;

    /// <summary>Another tile was dragged onto this one: (source tile, this tile).</summary>
    public event Action<VideoTile, VideoTile>? TileDropped;
    public event Action<VideoTile>? Changed;

    public Device? Device { get; private set; }
    public Camera? Camera { get; private set; }
    public CameraRef? Source => Device is null || Camera is null ? null : new CameraRef(Device.Id, Camera.Channel);
    public bool AutoQuality { get; private set; } = true;
    public StreamQuality Quality => _quality;
    public bool IsRecording => _player?.IsRecording == true || _hikPlayer?.IsSaving == true;

    /// <summary>True while the SDK surface is the active renderer.</summary>
    bool HikModeActive => HikHost.Visibility == Visibility.Visible;

    public bool IsSelected
    {
        set => Frame.BorderBrush = value ? SelectedBorder : IdleBorder;
    }

    public void Show(Device device, Camera camera)
    {
        Device = device;
        Camera = camera;
        var forced = App.Settings.LiveQuality;
        AutoQuality = forced is null;
        Open(forced ?? _autoQuality);
        Changed?.Invoke(this);
    }

    /// <summary>The live view tells each tile which stream suits its current size.</summary>
    public void SetAutoQuality(StreamQuality quality)
    {
        _autoQuality = quality;
        if (AutoQuality && Camera is not null && quality != _quality && !IsRecording)
        {
            Open(quality);
        }
    }

    public void Reconnect()
    {
        if (Camera is not null)
        {
            Open(_quality);
        }
    }

    public void Clear()
    {
        _wantPlaying = false;
        _retryTimer.Stop();
        StopRecording();
        DisposePlayers();
        Device = null;
        Camera = null;
        ShowEmpty();
        UpdateOverlay();
        Changed?.Invoke(this);
    }

    public void Stop()
    {
        _wantPlaying = false;
        _retryTimer.Stop();
        StopRecording();
        _player?.Stop();
        _hikPlayer?.Stop();
        if (Camera is not null)
        {
            StatusText.Text = "Durduruldu";
        }
    }

    // ---- Opening: mode selection ----

    void Open(StreamQuality quality)
    {
        if (Device is null || Camera is null)
        {
            return;
        }
        _quality = quality;
        _wantPlaying = true;
        _retryTimer.Stop();
        _triedHikFallback = false;
        CameraName.Text = Camera.Name;
        QualityBadge.Text = quality == StreamQuality.Main ? "HD" : "SD";
        StatusText.Text = "Bağlanıyor…";
        UpdateOverlay();

        // Stream encryption cannot be decoded by FFmpeg; a configured key means "use the SDK".
        bool useHik = _preferHik || !string.IsNullOrEmpty(Device.StreamKeyFor(Camera));
        if (useHik && HikSdk.Available)
        {
            OpenHik();
        }
        else
        {
            OpenFlyleaf();
        }
    }

    void OpenFlyleaf()
    {
        DisposeHikPlayer();
        HikHost.Visibility = Visibility.Collapsed;
        Host.Visibility = Visibility.Visible;
        if (_player is null)
        {
            _player = Players.CreateLive();
            _player.PropertyChanged += Player_PropertyChanged;
            Host.Player = _player;
        }
        _player.OpenAsync(StreamUrls.Live(Device!, Camera!.Channel, _quality));
    }

    /// <summary>Login + RealPlay are blocking network calls, so they run off the UI thread.</summary>
    void OpenHik()
    {
        DisposeFlyleafPlayer();
        Host.Visibility = Visibility.Collapsed;
        HikHost.Visibility = Visibility.Visible;
        HikHost.UpdateLayout(); // build the child HWND now

        var device = Device!;
        var camera = Camera!;
        var quality = _quality;
        var key = device.StreamKeyFor(camera);
        int generation = System.Threading.Volatile.Read(ref _openGeneration) + 1;
        System.Threading.Volatile.Write(ref _openGeneration, generation);

        // The player object is created and stored on the UI thread, so every dispose
        // path (DisposePlayers runs on the UI thread) is guaranteed to see it.
        if (_hikPlayer is null)
        {
            _hikPlayer = new HikLivePlayer();
            _hikPlayer.Error += OnHikError;
            _hikPlayer.Started += OnHikStarted;
        }
        var player = _hikPlayer;

        Task.Run(() =>
        {
            try
            {
                // The HwndHost builds its window during layout; give it a moment.
                var hwnd = IntPtr.Zero;
                for (var i = 0; i < 150 && hwnd == IntPtr.Zero && !player.IsDisposed; i++)
                {
                    hwnd = HikHost.VideoHandle;
                    if (hwnd == IntPtr.Zero)
                    {
                        Thread.Sleep(20);
                    }
                }
                if (hwnd == IntPtr.Zero)
                {
                    throw new HikException(0, "Video penceresi hazırlanamadı");
                }

                // Login is the only remaining blocking network step; it retries via EnsureLoggedIn.
                var session = HikSession.GetOrCreate(device);
                var userId = session.UserId;
                if (player.IsDisposed || System.Threading.Volatile.Read(ref _openGeneration) != generation)
                {
                    return; // a newer Open or a Dispose superseded us
                }

                // Recorders number their digital channels from StartDChan (e.g. 33); the ISAPI
                // channel is 1-based. Wrong numbering fails with NET_DVR_CHANNEL_ERROR.
                player.Start(userId, session.SdkChannel(camera.Channel), quality, hwnd, key);
            }
            catch (HikException ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (player.IsDisposed || System.Threading.Volatile.Read(ref _openGeneration) != generation || Camera is null)
                    {
                        return;
                    }
                    StatusText.Text = $"Görüntü açılamadı\n{Shorten(ex.Message)}";
                    ScheduleRetry();
                });
            }
        });
    }

    // ---- Status handling, both paths ----

    void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Player.Status) or nameof(Player.IsRecording)))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_player is null || Camera is null)
            {
                return;
            }
            RecBadge.Visibility = _player.IsRecording ? Visibility.Visible : Visibility.Collapsed;
            switch (_player.Status)
            {
                case Status.Opening:
                    StatusText.Text = _retryCount > 0 ? $"Yeniden bağlanıyor… ({_retryCount})" : "Bağlanıyor…";
                    break;
                case Status.Playing:
                    StatusText.Text = "";
                    _retryCount = 0;
                    if (_pendingRecordFile is not null)
                    {
                        var file = _pendingRecordFile;
                        _pendingRecordFile = null;
                        _player.StartRecording(ref file, false);
                    }
                    break;
                case Status.Failed when HikSdk.Available && !_triedHikFallback:
                    // FFmpeg could not decode — typical for stream encryption. Try the SDK once.
                    _triedHikFallback = true;
                    _preferHik = true;
                    StatusText.Text = "Şifreli akış için SDK deneniyor…";
                    Dispatcher.BeginInvoke(OpenHik); // deferred: not inside the player's own event
                    break;
                case Status.Failed:
                    StatusText.Text = $"Görüntü açılamadı\n{Shorten(_player.LastError)}\n\n{EncryptionHint}";
                    ScheduleRetry();
                    break;
                case Status.Ended:
                    StatusText.Text = "Bağlantı koptu";
                    ScheduleRetry();
                    break;
            }
        });
    }

    void OnHikStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Camera is null)
            {
                return;
            }
            StatusText.Text = "";
            _retryCount = 0;
            _preferHik = true;
            if (_pendingRecordFile is not null)
            {
                var file = _pendingRecordFile;
                _pendingRecordFile = null;
                TryStartHikSave(file);
            }
        });
    }

    void OnHikError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_hikPlayer is null || Camera is null || !_wantPlaying)
            {
                return;
            }
            StatusText.Text = $"Görüntü hatası\n{Shorten(message)}";
            ScheduleRetry();
        });
    }

    /// <summary>Recorders drop streams on reboots and network blips; keep trying with back-off.</summary>
    void ScheduleRetry()
    {
        if (_wantPlaying && !_retryTimer.IsEnabled)
        {
            _retryTimer.Interval = RetryDelays[Math.Min(_retryCount, RetryDelays.Length - 1)];
            _retryCount++;
            _retryTimer.Start();
        }
    }

    /// <summary>Hikvision "stream encryption" output cannot be decoded by FFmpeg; the SDK path plays it with the key.</summary>
    public const string EncryptionHint =
        "Görüntü şifrelemesi açıksa kayıt cihazının web arayüzünden kapatın: " +
        "Yapılandırma › Ağ › Gelişmiş › Platform Erişimi › \"Akış Şifreleme\".";

    static string Shorten(string? error) =>
        string.IsNullOrEmpty(error) ? "" : error.Length > 120 ? error[..120] + "…" : error;

    void ShowEmpty()
    {
        CameraName.Text = "";
        QualityBadge.Text = "";
        RecBadge.Visibility = Visibility.Collapsed;
        StatusText.Text = "Kamera sürükleyin veya listeden çift tıklayın";
    }

    void DisposePlayers()
    {
        System.Threading.Volatile.Write(ref _openGeneration, _openGeneration + 1); // cancel in-flight SDK opens
        DisposeFlyleafPlayer();
        DisposeHikPlayer();
    }

    void DisposeFlyleafPlayer()
    {
        if (_player is null)
        {
            return;
        }
        _player.PropertyChanged -= Player_PropertyChanged;
        Host.Player = null;
        _player.Dispose();
        _player = null;
    }

    void DisposeHikPlayer()
    {
        System.Threading.Volatile.Write(ref _openGeneration, _openGeneration + 1);
        if (_hikPlayer is null)
        {
            return;
        }
        _hikPlayer.Error -= OnHikError;
        _hikPlayer.Started -= OnHikStarted;
        _hikPlayer.Dispose();
        _hikPlayer = null;
    }

    // ---- Snapshot / recording ----

    public async Task<string?> TakeSnapshotAsync()
    {
        if (Device is null || Camera is null)
        {
            return null;
        }
        var folder = App.Settings.SnapshotFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Safe(Camera.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

        // The recorder serves a full-resolution JPEG even when the tile shows the sub stream.
        try
        {
            using var isapi = new IsapiClient(Device, TimeSpan.FromSeconds(5));
            await File.WriteAllBytesAsync(path, await isapi.GetSnapshotAsync(Camera.Channel));
            return path;
        }
        catch (IsapiException)
        {
        }
        if (HikModeActive && _hikPlayer?.IsStarted == true)
        {
            var saved = await Task.Run(() => _hikPlayer.Snapshot(path));
            if (saved is not null)
            {
                return saved;
            }
        }
        if (_player?.Status == Status.Playing)
        {
            _player.TakeSnapshotToFile(path, 0, 0);
            return path;
        }
        return null;
    }

    /// <summary>Records the main stream to MP4 (remux only, no re-encode).</summary>
    public string? ToggleRecording()
    {
        if (Device is null || Camera is null || (!HikModeActive && _player is null))
        {
            return null;
        }
        if (IsRecording)
        {
            StopRecording();
            return null;
        }
        var folder = App.Settings.RecordingFolder;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Safe(Camera.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        if (_quality != StreamQuality.Main)
        {
            AutoQuality = false;
            _pendingRecordFile = file;
            Open(StreamQuality.Main);
        }
        else if (HikModeActive)
        {
            TryStartHikSave(file);
        }
        else
        {
            _player!.StartRecording(ref file, false);
        }
        return file;
    }

    void TryStartHikSave(string file)
    {
        try
        {
            _hikPlayer!.StartSave(file);
            RecBadge.Visibility = Visibility.Visible;
        }
        catch (HikException ex)
        {
            StatusText.Text = Shorten(ex.Message);
        }
    }

    void StopRecording()
    {
        _pendingRecordFile = null;
        if (_player?.IsRecording == true)
        {
            _player.StopRecording();
        }
        if (_hikPlayer?.IsSaving == true)
        {
            _hikPlayer.StopSave();
            RecBadge.Visibility = Visibility.Collapsed;
        }
    }

    static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    // ---- Overlay popup: follows the tile, hides while the window is inactive ----

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookRootWindow();
        UpdateOverlay();
        if (_wantPlaying && Camera is not null)
        {
            Reconnect(); // tile came back into the layout (e.g. after maximize)
        }
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposePlayers();
        OverlayPopup.IsOpen = false;
    }

    void HookRootWindow()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _rootWindow))
        {
            return;
        }
        if (_rootWindow is not null)
        {
            _rootWindow.Activated -= RootWindow_Activated;
            _rootWindow.Deactivated -= RootWindow_Deactivated;
            _rootWindow.LocationChanged -= RootWindow_Moved;
            _rootWindow.StateChanged -= RootWindow_Moved;
        }
        _rootWindow = window;
        if (window is not null)
        {
            window.Activated += RootWindow_Activated;
            window.Deactivated += RootWindow_Deactivated;
            window.LocationChanged += RootWindow_Moved;
            window.StateChanged += RootWindow_Moved;
        }
    }

    void RootWindow_Activated(object? sender, EventArgs e)
    {
        _rootActive = true;
        UpdateOverlay();
    }

    void RootWindow_Deactivated(object? sender, EventArgs e)
    {
        _rootActive = false;
        UpdateOverlay();
    }

    void RootWindow_Moved(object? sender, EventArgs e) => NudgeOverlay();

    void UpdateOverlay()
    {
        var open = IsLoaded && IsVisible
            && _rootWindow is not null
            && _rootWindow.WindowState != WindowState.Minimized
            && _rootActive;
        if (OverlayPopup.IsOpen != open)
        {
            OverlayPopup.IsOpen = open;
        }
        if (open)
        {
            NudgeOverlay();
        }
    }

    void NudgeOverlay()
    {
        if (!OverlayPopup.IsOpen)
        {
            return;
        }
        // Two offset changes force the Popup to reposition against its PlacementTarget,
        // following tile moves and layout changes while netting out to zero.
        OverlayPopup.HorizontalOffset += OverlayNudge;
        OverlayPopup.HorizontalOffset -= OverlayNudge;
    }

    // ---- Mouse / drag & drop / menu ----

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Activated?.Invoke(this);
        if (e.ClickCount == 2)
        {
            _dragOrigin = null;
            DoubleClicked?.Invoke(this);
        }
        else
        {
            _dragOrigin = e.GetPosition(Overlay);
        }
    }

    /// <summary>Dragging a tile with a camera onto another tile swaps the two (handled by the live view).</summary>
    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragOrigin = null;
            return;
        }
        if (_dragOrigin is not { } origin || Source is not { } source)
        {
            return;
        }
        var delta = e.GetPosition(Overlay) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragOrigin = null;
        var data = new DataObject(DragFormat, source);
        data.SetData(TileDragFormat, this);
        DragDrop.DoDragDrop(Overlay, data, DragDropEffects.Move);
    }

    void Overlay_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TileDragFormat)
            ? (ReferenceEquals(e.Data.GetData(TileDragFormat), this) ? DragDropEffects.None : DragDropEffects.Move)
            : e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Overlay_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TileDragFormat) is VideoTile from)
        {
            if (from != this)
            {
                Activated?.Invoke(this);
                TileDropped?.Invoke(from, this);
            }
        }
        else if (e.Data.GetData(DragFormat) is CameraRef source)
        {
            Activated?.Invoke(this);
            CameraDropped?.Invoke(this, source);
        }
    }

    void Menu_Opened(object sender, RoutedEventArgs e)
    {
        var hasCamera = Camera is not null;
        foreach (var item in new[] { MenuMain, MenuSub, MenuAuto, MenuSnapshot, MenuRecord, MenuReconnect, MenuClose })
        {
            item.IsEnabled = hasCamera;
        }
        MenuMain.IsChecked = hasCamera && !AutoQuality && _quality == StreamQuality.Main;
        MenuSub.IsChecked = hasCamera && !AutoQuality && _quality == StreamQuality.Sub;
        MenuAuto.IsChecked = AutoQuality;
        MenuRecord.Header = IsRecording ? "Kaydı durdur" : "Kaydı başlat";
    }

    void MenuMain_Click(object sender, RoutedEventArgs e) => SetManualQuality(StreamQuality.Main);

    void MenuSub_Click(object sender, RoutedEventArgs e) => SetManualQuality(StreamQuality.Sub);

    public void SetManualQuality(StreamQuality quality)
    {
        AutoQuality = false;
        if (Camera is not null && quality != _quality)
        {
            StopRecording();
            Open(quality);
        }
    }

    public void EnableAutoQuality()
    {
        AutoQuality = true;
        if (Camera is not null && _autoQuality != _quality && !IsRecording)
        {
            Open(_autoQuality);
        }
    }

    void MenuAuto_Click(object sender, RoutedEventArgs e) => EnableAutoQuality();

    async void MenuSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var path = await TakeSnapshotAsync();
        StatusText.Text = path is null ? "Anlık görüntü alınamadı" : "";
        if (path is not null)
        {
            Flash();
        }
    }

    void MenuRecord_Click(object sender, RoutedEventArgs e) => ToggleRecording();

    void MenuReconnect_Click(object sender, RoutedEventArgs e) => Reconnect();

    void MenuClose_Click(object sender, RoutedEventArgs e) => Clear();

    /// <summary>Short white flash as snapshot feedback.</summary>
    public void Flash()
    {
        var animation = new System.Windows.Media.Animation.ColorAnimation(
            Color.FromArgb(0x80, 255, 255, 255), Color.FromArgb(0x01, 0, 0, 0), TimeSpan.FromMilliseconds(350));
        var brush = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        Overlay.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
