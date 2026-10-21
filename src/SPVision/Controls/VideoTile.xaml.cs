using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlyleafLib.MediaPlayer;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision.Controls;

/// <summary>What a tile is doing right now (drives the header dot and the status bar counts).</summary>
public enum TileState
{
    Empty,
    Connecting,
    Playing,
    Failed,
    Stopped,
}

/// <summary>
/// One cell of the live wall. Two rendering paths share the tile:
/// Flyleaf (FFmpeg, unencrypted RTSP) and the Hikvision SDK (PlayCtrl drawing into
/// <see cref="HikHost"/> — the only way to decode stream-encrypted cameras).
/// Both render into HWNDs, which WPF content cannot cover (airspace). So the tile is two rows:
/// a WPF header strip (name, state, badges) and below it the video host with nothing drawn over it.
/// </summary>
public partial class VideoTile : UserControl
{
    public const string DragFormat = "SPVision.CameraRef";

    /// <summary>Added next to <see cref="DragFormat"/> when the drag starts from a tile (swap, not copy).</summary>
    const string TileDragFormat = "SPVision.VideoTile";

    const double HeaderHeight = 26;
    const double CompactHeaderHeight = 22;

    /// <summary>Tile margin (2 px per side) plus the 1 px frame border, top and bottom.</summary>
    const double VerticalChrome = 2 * 2 + 2 * 1;

    static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    readonly System.Windows.Threading.DispatcherTimer _retryTimer = new();
    readonly SemaphoreSlim _sdkGate = new(1, 1); // serializes SDK commands of this tile (open, stop, recording, boxes)

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
    bool _hikRecording;          // SDK recording asked for (the save itself runs in the SDK queue)
    int _recordGeneration;       // supersedes the failure report of an earlier recording start
    TileState _state = TileState.Empty;
    bool _selected;
    bool _compact;

    Window? _surface;            // Flyleaf's video surface window (a child HWND of the main window)
    Point? _dragOrigin;
    IInputElement? _dragOriginElement;

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
        Host.SurfaceCreated += (_, _) => HookSurface(Host.Surface);
        HookSurface(Host.Surface);
    }

    public event Action<VideoTile>? Activated;
    public event Action<VideoTile>? DoubleClicked;
    public event Action<VideoTile, CameraRef>? CameraDropped;

    /// <summary>Another tile was dragged onto this one: (source tile, this tile).</summary>
    public event Action<VideoTile, VideoTile>? TileDropped;
    public event Action<VideoTile>? Changed;

    /// <summary>State, quality, recording or path changed (header, list dots and status bar follow it).</summary>
    public event Action<VideoTile>? StateChanged;

    /// <summary>A short message for the user (shown as a toast by the live view).</summary>
    public event Action<VideoTile, string>? Notice;

    /// <summary>Context menu actions that concern the whole wall.</summary>
    public event Action? StopAllRequested;
    public event Action? OpenFoldersRequested;

    public Device? Device { get; private set; }
    public Camera? Camera { get; private set; }
    public CameraRef? Source => Device is null || Camera is null ? null : new CameraRef(Device.Id, Camera.Channel);
    public bool AutoQuality { get; private set; } = true;
    public StreamQuality Quality => _quality;
    public bool IsRecording => _player?.IsRecording == true || _hikRecording;
    public TileState State => _state;

    /// <summary>True when the camera is waiting for a retry or retrying after a lost/failed stream.</summary>
    public bool IsReconnecting =>
        Camera is not null && _wantPlaying && (_state == TileState.Failed || (_state == TileState.Connecting && _retryCount > 0));

    /// <summary>The stream runs through the Hikvision SDK (encrypted); otherwise through Flyleaf/FFmpeg.</summary>
    public bool UsesSdk => Camera is not null && HikModeActive;

    /// <summary>Height of the video area alone (tile minus header strip and borders), in DIPs.</summary>
    public double VideoHeight => Math.Max(0, ActualHeight - VerticalChrome - HeaderRow.Height.Value);

    /// <summary>True while the SDK surface is the active renderer.</summary>
    bool HikModeActive => HikHost.Visibility == Visibility.Visible;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            // Resource references (not brush values) so the frame follows a theme switch.
            Frame.SetResourceReference(Border.BorderBrushProperty, value ? "Accent" : "TileBorder");
            EmptyOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, value ? "Accent" : "WallLine");
        }
    }

    /// <summary>Small tiles (layouts of 16 tiles or more) use the 22 px header, the others 26 px.</summary>
    public bool IsCompact
    {
        get => _compact;
        set
        {
            _compact = value;
            HeaderRow.Height = new GridLength(value ? CompactHeaderHeight : HeaderHeight);
            CameraName.FontSize = (double)FindResource(value ? "FontSizeSmall" : "FontSizeTile");
        }
    }

    public void Show(Device device, Camera camera)
    {
        if (Device?.Id != device.Id || Camera?.Channel != camera.Channel)
        {
            StopRecording(); // the MP4 belongs to the camera that leaves this tile (page change, drop)
        }
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
        Changed?.Invoke(this);
    }

    /// <summary>
    /// Settings or the top bar toggled the motion boxes: re-applies them to the running SDK stream. An open in
    /// progress picks the value up when its port opens; the running stream is switched in the SDK queue.
    /// </summary>
    public void ApplyMotionBoxes(bool show)
    {
        if (_hikPlayer is not { } player)
        {
            return;
        }
        player.ShowMotionBoxes = show;
        QueueSdk(() => player.SetMotionBoxes(show));
    }

    /// <summary>The camera list was refreshed (names may have changed): update the header strip.</summary>
    public void RefreshCameraInfo()
    {
        if (Camera is not null)
        {
            CameraName.Text = Camera.Name;
        }
    }

    public void Stop()
    {
        _wantPlaying = false;
        _retryTimer.Stop();
        StopRecording();
        _player?.Stop();
        if (_hikPlayer is { } player)
        {
            QueueSdk(player.Stop); // blocking (NET_DVR_StopRealPlay)
        }
        if (Camera is not null)
        {
            SetState(TileState.Stopped, "Durduruldu");
        }
    }

    /// <summary>
    /// Window closing: ends the recording and releases the SDK stream before returning (the SDK is cleaned up
    /// right after). Blocks the closing window briefly; the data callbacks never wait for the UI thread.
    /// </summary>
    public void Release()
    {
        _wantPlaying = false;
        _retryTimer.Stop();
        StopRecording();
        _player?.Stop();
        DisposeHikPlayer(wait: true);
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
        if (_hikRecording)
        {
            // Reopening (reconnect) replaces the RealPlay handle, which ends its recording.
            _hikRecording = false;
            _recordGeneration++;
            RecBadge.Visibility = Visibility.Collapsed;
        }
        EmptyFrame.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Visible;
        CameraName.Text = Camera.Name;
        QualityBadge.Text = quality == StreamQuality.Main ? "HD" : "SD";
        SetState(TileState.Connecting);

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
        StateChanged?.Invoke(this);
    }

    /// <summary>Login + RealPlay are blocking network calls, so they run off the UI thread (in the SDK queue).</summary>
    void OpenHik()
    {
        DisposeFlyleafPlayer();
        Host.Visibility = Visibility.Collapsed;
        HikHost.Visibility = Visibility.Visible;
        HikHost.UpdateLayout(); // build the child HWND now
        StateChanged?.Invoke(this);

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
        _hikPlayer.ShowMotionBoxes = App.Settings.ShowMotionBoxes;
        var player = _hikPlayer;

        var turn = _sdkGate.WaitAsync(); // taken here, on the UI thread: SDK commands run in the order issued
        Task.Run(async () =>
        {
            await turn;
            try
            {
                if (IsStale(player, generation))
                {
                    return;
                }
                // The HwndHost builds its window during layout; give it a moment.
                var hwnd = IntPtr.Zero;
                for (var i = 0; i < 150 && hwnd == IntPtr.Zero && !IsStale(player, generation); i++)
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
                if (IsStale(player, generation))
                {
                    return; // a newer Open or a Dispose superseded us
                }

                // Recorders number their digital channels from StartDChan (e.g. 33); the ISAPI
                // channel is 1-based. Wrong numbering fails with NET_DVR_CHANNEL_ERROR.
                player.Start(userId, session.SdkChannel(camera.Channel), quality, hwnd, key);
            }
            catch (HikException ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (IsStale(player, generation) || Camera is null)
                    {
                        return;
                    }
                    SetState(TileState.Failed, $"Görüntü açılamadı\n{Shorten(ex.Message)}");
                    ScheduleRetry();
                });
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _sdkGate.Release();
            }
        });
    }

    bool IsStale(HikLivePlayer player, int generation) =>
        player.IsDisposed || System.Threading.Volatile.Read(ref _openGeneration) != generation;

    /// <summary>
    /// Runs a blocking SDK call (save, stop, motion boxes, dispose) in the tile's SDK queue, off the UI thread.
    /// The turn is taken by the caller (the UI thread), so commands run in the order they were issued.
    /// </summary>
    void QueueSdk(Action work)
    {
        var turn = _sdkGate.WaitAsync();
        Task.Run(async () =>
        {
            await turn;
            try
            {
                work();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _sdkGate.Release();
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
                    SetState(TileState.Connecting, _retryCount > 0 ? $"Yeniden bağlanıyor… ({_retryCount})" : null);
                    break;
                case Status.Playing:
                    _retryCount = 0;
                    SetState(TileState.Playing);
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
                    SetState(TileState.Connecting, "Şifreli akış için SDK deneniyor…");
                    Dispatcher.BeginInvoke(OpenHik); // deferred: not inside the player's own event
                    break;
                case Status.Failed:
                    SetState(TileState.Failed, $"Görüntü açılamadı\n{Shorten(_player.LastError)}\n\n{EncryptionHint}");
                    ScheduleRetry();
                    break;
                case Status.Ended:
                    SetState(TileState.Failed, "Bağlantı koptu");
                    ScheduleRetry();
                    break;
                default:
                    StateChanged?.Invoke(this); // recording flag
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
            _retryCount = 0;
            _preferHik = true;
            if (!App.Settings.ShowMotionBoxes)
            {
                ApplyMotionBoxes(false); // again now that the RealPlay handle is known
            }
            SetState(TileState.Playing);
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
            SetState(TileState.Failed, $"Görüntü hatası\n{Shorten(message)}");
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
            StateChanged?.Invoke(this);
        }
    }

    /// <summary>Hikvision "stream encryption" output cannot be decoded by FFmpeg; the SDK path plays it with the key.</summary>
    public const string EncryptionHint =
        "Görüntü şifrelemesi açıksa kayıt cihazının web arayüzünden kapatın: " +
        "Yapılandırma › Ağ › Gelişmiş › Platform Erişimi › \"Akış Şifreleme\".";

    static string Shorten(string? error) =>
        string.IsNullOrEmpty(error) ? "" : error.Length > 120 ? error[..120] + "…" : error;

    /// <summary>
    /// Updates the header strip. Failed/stopped tiles hide their (black) video host so the reason
    /// can be shown in the video area; the next Open shows the host again.
    /// </summary>
    void SetState(TileState state, string? detail = null)
    {
        _state = state;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch
        {
            TileState.Playing => "Online",
            TileState.Connecting => "Warning",
            TileState.Failed => "Danger",
            _ => "Offline",
        });
        StateText.Visibility = state == TileState.Connecting ? Visibility.Visible : Visibility.Collapsed;
        var showMessage = state is TileState.Failed or TileState.Stopped;
        MessageText.Text = showMessage ? detail ?? "" : "";
        VideoLayer.Visibility = showMessage ? Visibility.Hidden : Visibility.Visible;
        Header.ToolTip = Camera is null
            ? null
            : $"{Camera.Name} · Kanal {Camera.Channel} · {(_quality == StreamQuality.Main ? "HD (ana akış)" : "SD (alt akış)")}" +
              (string.IsNullOrEmpty(detail) ? "" : $"\n{detail}");
        StateChanged?.Invoke(this);
    }

    void ShowEmpty()
    {
        CameraName.Text = "";
        QualityBadge.Text = "";
        RecBadge.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Collapsed;
        EmptyFrame.Visibility = Visibility.Visible;
        EmptyFrame.ToolTip = "Listeden bir kamerayı buraya sürükleyin ya da listede çift tıklayın";
        SetState(TileState.Empty);
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

    /// <summary>
    /// NET_DVR_StopRealPlay blocks (and waits for a command in progress on the player), so the player is disposed
    /// in the SDK queue; at shutdown it must finish before the SDK is cleaned up.
    /// </summary>
    void DisposeHikPlayer(bool wait = false)
    {
        System.Threading.Volatile.Write(ref _openGeneration, _openGeneration + 1);
        if (_hikPlayer is not { } player)
        {
            return;
        }
        player.Error -= OnHikError;
        player.Started -= OnHikStarted;
        _hikPlayer = null;
        _hikRecording = false; // the dispose ends the recording
        if (wait)
        {
            player.Dispose();
        }
        else
        {
            QueueSdk(player.Dispose);
        }
    }

    // ---- Snapshot / recording ----

    public async Task<string?> TakeSnapshotAsync()
    {
        if (Device is null || Camera is null)
        {
            return null;
        }
        var folder = App.Settings.SnapshotFolder;
        System.IO.Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"{Safe(Camera.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

        // The recorder serves a full-resolution JPEG even when the tile shows the sub stream.
        try
        {
            using var isapi = new IsapiClient(Device, TimeSpan.FromSeconds(5));
            await System.IO.File.WriteAllBytesAsync(path, await isapi.GetSnapshotAsync(Camera.Channel));
            return path;
        }
        catch (IsapiException)
        {
        }
        if (HikModeActive && _hikPlayer is { IsStarted: true } hik)
        {
            var saved = await Task.Run(() => hik.Snapshot(path)); // waits for a save/stop in progress: off the UI thread
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
        System.IO.Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, $"{Safe(Camera.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
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

    /// <summary>
    /// NET_DVR_SaveRealData_V30 waits for the SDK's data thread: it runs in the SDK queue, never on the UI thread.
    /// The button and the KAYIT badge switch at once; a refusal is reported and switches them back.
    /// </summary>
    void TryStartHikSave(string file)
    {
        if (_hikPlayer is not { } player)
        {
            return;
        }
        _hikRecording = true;
        var generation = ++_recordGeneration;
        RecBadge.Visibility = Visibility.Visible;
        StateChanged?.Invoke(this);
        QueueSdk(() =>
        {
            string? error;
            try
            {
                error = player.StartSave(file) ? null : "görüntü açık değil";
            }
            catch (HikException ex)
            {
                error = ex.Message;
            }
            if (error is not null)
            {
                Dispatcher.BeginInvoke(() => HikSaveFailed(generation, error));
            }
        });
    }

    void HikSaveFailed(int generation, string error)
    {
        if (generation != _recordGeneration || !_hikRecording)
        {
            return; // stopped (or restarted) meanwhile
        }
        _hikRecording = false;
        RecBadge.Visibility = Visibility.Collapsed;
        Notice?.Invoke(this, $"Kayıt başlatılamadı: {Shorten(error)}");
        StateChanged?.Invoke(this);
    }

    void StopRecording()
    {
        _pendingRecordFile = null;
        if (_player?.IsRecording == true)
        {
            _player.StopRecording();
        }
        if (_hikRecording)
        {
            _hikRecording = false;
            _recordGeneration++;
            if (_hikPlayer is { } player)
            {
                QueueSdk(player.StopSave); // blocking: in the SDK queue, ahead of a following open
            }
            RecBadge.Visibility = Visibility.Collapsed;
            StateChanged?.Invoke(this);
        }
    }

    static string Safe(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    // ---- Lifetime ----

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSurface(Host.Surface);
        if (_wantPlaying && Camera is not null)
        {
            Reconnect(); // re-added after an unload; the live view itself keeps its tiles in the tree (maximize only hides them)
        }
    }

    void OnUnloaded(object sender, RoutedEventArgs e) => DisposePlayers();

    // ---- Mouse / drag & drop / menu ----
    //
    // Input arrives from three places: the WPF parts of the tile (header, empty tile, message area),
    // the SDK video HWND (HikVideoHost reports HTTRANSPARENT, so its clicks and drops reach the tile
    // through the main window), and Flyleaf's surface — a separate child window, hooked below.

    void HookSurface(Window? surface)
    {
        if (surface is null || ReferenceEquals(surface, _surface))
        {
            return;
        }
        if (_surface is not null)
        {
            _surface.MouseLeftButtonDown -= Surface_MouseLeftButtonDown;
            _surface.MouseMove -= Surface_MouseMove;
            _surface.MouseRightButtonUp -= Surface_MouseRightButtonUp;
            _surface.DragEnter -= Tile_DragOver;
            _surface.DragOver -= Tile_DragOver;
            _surface.Drop -= Tile_Drop;
        }
        _surface = surface;
        surface.AllowDrop = true;
        surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
        surface.MouseMove += Surface_MouseMove;
        surface.MouseRightButtonUp += Surface_MouseRightButtonUp;
        surface.DragEnter += Tile_DragOver;
        surface.DragOver += Tile_DragOver;
        surface.Drop += Tile_Drop;
    }

    void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PointerDown(e, this);

    void Tile_MouseMove(object sender, MouseEventArgs e) => PointerMove(e, this);

    void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => PointerDown(e, (IInputElement)sender);

    void Surface_MouseMove(object sender, MouseEventArgs e) => PointerMove(e, (IInputElement)sender);

    void Surface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        Menu.PlacementTarget = LayoutRoot;
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
        e.Handled = true;
    }

    void PointerDown(MouseButtonEventArgs e, IInputElement relativeTo)
    {
        Activated?.Invoke(this);
        // Deferred: a click on Flyleaf's surface window must not leave keyboard focus there (F11 etc.),
        // and a double click re-arranges the grid, which should not happen inside the surface's own event.
        if (e.ClickCount == 2)
        {
            _dragOrigin = null;
            Dispatcher.BeginInvoke(() => DoubleClicked?.Invoke(this));
        }
        else
        {
            _dragOrigin = e.GetPosition(relativeTo);
            _dragOriginElement = relativeTo;
        }
        Dispatcher.BeginInvoke(() => Focus());
    }

    /// <summary>Dragging a tile with a camera onto another tile swaps the two (handled by the live view).</summary>
    void PointerMove(MouseEventArgs e, IInputElement relativeTo)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragOrigin = null;
            return;
        }
        if (_dragOrigin is not { } origin || !ReferenceEquals(relativeTo, _dragOriginElement) || Source is not { } source)
        {
            return;
        }
        var delta = e.GetPosition(relativeTo) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragOrigin = null;
        var data = new DataObject(DragFormat, source);
        data.SetData(TileDragFormat, this);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    void Tile_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TileDragFormat)
            ? (ReferenceEquals(e.Data.GetData(TileDragFormat), this) ? DragDropEffects.None : DragDropEffects.Move)
            : e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Tile_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
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
        if (path is null)
        {
            Notice?.Invoke(this, "Anlık görüntü alınamadı.");
            return;
        }
        Flash();
        Notice?.Invoke(this, $"Kaydedildi: {path}");
    }

    void MenuRecord_Click(object sender, RoutedEventArgs e)
    {
        var wasRecording = IsRecording;
        var file = ToggleRecording();
        if (file is not null)
        {
            Notice?.Invoke(this, $"Kayıt: {file}");
        }
        else if (wasRecording)
        {
            Notice?.Invoke(this, "Kayıt durduruldu.");
        }
        StateChanged?.Invoke(this);
    }

    void MenuReconnect_Click(object sender, RoutedEventArgs e) => Reconnect();

    void MenuClose_Click(object sender, RoutedEventArgs e) => Clear();

    void MenuStopAll_Click(object sender, RoutedEventArgs e) => StopAllRequested?.Invoke();

    void MenuFolders_Click(object sender, RoutedEventArgs e) => OpenFoldersRequested?.Invoke();

    /// <summary>Short flash of the header strip as snapshot feedback (the video itself cannot be drawn over).</summary>
    public void Flash()
    {
        var animation = new System.Windows.Media.Animation.ColorAnimation(
            (Color)FindResource("FlashColor"), (Color)FindResource("TileHeaderColor"), TimeSpan.FromMilliseconds(450));
        var brush = new SolidColorBrush((Color)FindResource("FlashColor"));
        // Afterwards the header goes back to the theme brush, so a later theme switch still reaches it.
        animation.Completed += (_, _) => Header.SetResourceReference(Border.BackgroundProperty, "TileHeaderBg");
        Header.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
