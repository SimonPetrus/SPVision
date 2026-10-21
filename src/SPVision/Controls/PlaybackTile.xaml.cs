using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyleafLib.MediaPlayer;
using SPVision.Core;
using SPVision.Hik;

namespace SPVision.Controls;

/// <summary>
/// One camera of the synchronous playback grid. Same chrome as the live tile (header strip above the video
/// HWND). Plays its camera's recording from a given time on the right path: the Hikvision SDK when a stream key
/// is set (or FFmpeg proved unable to decode it), otherwise Flyleaf over RTSP. The playback view tells every
/// tile where to be (<see cref="SeekTo"/>), how fast and whether paused; tiles report the time on screen.
/// A tile whose camera has no recording at the shared time is "parked": stopped, showing why, until the
/// shared time reaches its next recording.
/// </summary>
public partial class PlaybackTile : UserControl
{
    const double HeaderHeight = 26;
    const double CompactHeaderHeight = 22;

    readonly SemaphoreSlim _sdkGate = new(1, 1); // serializes SDK commands of this tile (open, stop, controls)
    readonly Stopwatch _openWatch = new();

    Player? _player;                 // FFmpeg path
    HikPlaybackPlayer? _hikPlayer;   // SDK path (stream encryption)
    bool _preferHik;                 // sticky: the SDK proved necessary for this camera
    bool _triedHikFallback;          // one FFmpeg → SDK fallback per seek
    bool _awaitingFirstFrame;        // latency of the current open not measured yet
    int _openGeneration;             // supersedes SDK commands of older seeks
    int _showGeneration;             // supersedes recording searches of an earlier Show/Clear
    DateTime _openedAt;              // Flyleaf: recorder time at CurTime 0
    DateTime _seekTarget;
    string? _clipFile;
    int _speedIndex = PlaybackSpeed.Normal;
    bool _paused;
    bool _focused;
    bool _compact;
    TileState _state = TileState.Empty;
    Window? _surface;

    public PlaybackTile()
    {
        InitializeComponent();
        ShowEmpty();
        Unloaded += (_, _) => DisposePlayers(waitForSdk: true);
        Host.SurfaceCreated += (_, _) => HookSurface(Host.Surface);
        HookSurface(Host.Surface);
    }

    /// <summary>The tile was clicked (it should take the focus).</summary>
    public event Action<PlaybackTile>? Activated;

    /// <summary>State, path or clip flag changed (header, list and status bar follow it).</summary>
    public event Action<PlaybackTile>? StateChanged;

    /// <summary>A short message for the user (status bar).</summary>
    public event Action<PlaybackTile, string>? Notice;

    public Device? Device { get; private set; }
    public Camera? Camera { get; private set; }
    public CameraRef? Source => Device is null || Camera is null ? null : new CameraRef(Device.Id, Camera.Channel);

    /// <summary>Recordings of the camera on the playback day; null while unknown (not searched yet, or the search failed).</summary>
    public IReadOnlyList<RecordingSegment>? Segments { get; private set; }

    /// <summary>The recording search of the day has finished (successfully or not): the tile can join the playback.</summary>
    public bool SearchDone { get; private set; }

    public TileState State => _state;

    /// <summary>Waiting for its next recording (or the recording ended): resumed by the view when the shared time is recorded.</summary>
    public bool IsParked { get; private set; }

    /// <summary>When the last seek was issued (the view does not re-seek a tile again right away).</summary>
    public DateTime LastSeekUtc { get; private set; }

    /// <summary>How long the last open took until the first frame; the view leads drift re-seeks by it.</summary>
    public TimeSpan OpenLatency { get; private set; } = TimeSpan.FromSeconds(1);

    public bool UsesSdk => Camera is not null && HikHost.Visibility == Visibility.Visible;

    public bool IsClipping => _player?.IsRecording == true || _hikPlayer?.IsSaving == true;

    public bool IsFocusedTile
    {
        get => _focused;
        set
        {
            _focused = value;
            // Resource reference (not a brush value) so the frame follows a theme switch.
            Frame.SetResourceReference(Border.BorderBrushProperty, value ? "Accent" : "TileBorder");
            if (_player is not null)
            {
                _player.Audio.Mute = !value; // only the focused camera is heard
            }
        }
    }

    /// <summary>Small tiles use the 22 px header, large ones 26 px (one height per layout).</summary>
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

    /// <summary>Recorder time of the frame on screen; null when nothing plays.</summary>
    public DateTime? CurrentTime =>
        _player is { Status: Status.Playing or Status.Paused } p ? _openedAt + TimeSpan.FromTicks(Math.Max(0, p.CurTime))
        : _hikPlayer?.IsStarted == true && HikHost.Visibility == Visibility.Visible ? _hikPlayer.GetTime()
        : null;

    /// <summary>New camera for this tile; it waits ("Kayıtlar aranıyor…") until the view seeks it.</summary>
    public void Show(Device device, Camera camera)
    {
        StopClip();
        DisposePlayers();
        _showGeneration++;
        Device = device;
        Camera = camera;
        Segments = null;
        SearchDone = false;
        IsParked = false;
        _preferHik = false;
        EmptyFrame.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Visible;
        CameraName.Text = camera.Name;
        SetState(TileState.Connecting, header: "Kayıtlar aranıyor…");
    }

    public void Clear() => Clear(waitForSdk: false);

    /// <summary>Window closing: releases the SDK stream before returning (the SDK is cleaned up right after).</summary>
    public void Release() => Clear(waitForSdk: true);

    void Clear(bool waitForSdk)
    {
        StopClip();
        DisposePlayers(waitForSdk);
        _showGeneration++;
        Device = null;
        Camera = null;
        Segments = null;
        SearchDone = false;
        IsParked = false;
        ShowEmpty();
    }

    /// <summary>The camera list was refreshed (names may have changed).</summary>
    public void RefreshCameraInfo()
    {
        if (Camera is not null)
        {
            CameraName.Text = Camera.Name;
        }
    }

    /// <summary>Searches the camera's recordings of the day (ISAPI). False when the tile changed meanwhile.</summary>
    public async Task<bool> LoadSegmentsAsync(DateTime day, SemaphoreSlim searches, CancellationToken ct)
    {
        if (Device is not { } device || Camera is not { } camera)
        {
            return false;
        }
        var generation = _showGeneration;
        List<RecordingSegment>? segments;
        try
        {
            await searches.WaitAsync(ct);
            try
            {
                using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(20));
                segments = await isapi.SearchRecordingsAsync(camera.Channel, day.Date, day.Date.AddDays(1).AddSeconds(-1), ct);
            }
            finally
            {
                searches.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is IsapiException or UriFormatException)
        {
            segments = null; // unknown: the tile simply plays from the requested time
        }
        if (generation != _showGeneration || ct.IsCancellationRequested)
        {
            return false; // the tile got another camera (or day) meanwhile
        }
        Segments = segments;
        SearchDone = true;
        return true;
    }

    /// <summary>
    /// Plays from <paramref name="time"/> at the given speed (paused or not). When the camera has no recording
    /// there, the tile is parked instead. Every call reopens the stream, which is what keeps tiles in step.
    /// </summary>
    public void SeekTo(DateTime time, int speedIndex, bool paused)
    {
        _speedIndex = speedIndex;
        _paused = paused;
        if (Device is null || Camera is null)
        {
            return;
        }
        StopClip();
        LastSeekUtc = DateTime.UtcNow;
        if (Segments is { } segments && !RecordingJump.IsRecorded(segments, time))
        {
            Park(segments, time);
            return;
        }
        IsParked = false;
        _seekTarget = time;
        _triedHikFallback = false;
        _awaitingFirstFrame = true;
        _openWatch.Restart();
        bool useHik = _preferHik || !string.IsNullOrEmpty(Device.StreamKeyFor(Camera));
        if (useHik && HikSdk.Available)
        {
            OpenHik(time);
        }
        else
        {
            OpenFlyleaf(time);
        }
    }

    /// <summary>No recording at the shared time: stop and say when the next one starts.</summary>
    public void Park(IReadOnlyList<RecordingSegment>? segments, DateTime time)
    {
        StopClip();
        StopPlayers();
        IsParked = true;
        var next = segments is null ? null : RecordingJump.NextStart(segments, time);
        SetState(TileState.Stopped,
            next is { } start ? $"Bu saatte kayıt yok\nSonraki kayıt {start:HH:mm:ss}" :
            segments is { Count: > 0 } ? "Bu saatten sonra kayıt yok" :
            "Bu gün için kayıt yok");
    }

    /// <summary>Speed for every later frame; re-issued to the recorder (SDK) or the player (Flyleaf).</summary>
    public void SetSpeed(int speedIndex)
    {
        _speedIndex = speedIndex;
        if (_hikPlayer is { } hik && UsesSdk)
        {
            hik.RequestedSpeedSteps = PlaybackSpeed.Steps(speedIndex);
            SyncSdkControls(hik);
        }
        else if (_player is { Status: Status.Playing or Status.Paused })
        {
            ApplyFlyleafSpeed(_player);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_hikPlayer is { } hik && UsesSdk)
        {
            hik.RequestedPaused = paused;
            SyncSdkControls(hik);
        }
        else if (_player is { } player)
        {
            if (paused && player.Status == Status.Playing)
            {
                player.Pause();
            }
            else if (!paused && player.Status == Status.Paused)
            {
                player.Play();
            }
        }
    }

    /// <summary>Settings or the top bar toggled the motion boxes.</summary>
    public void ApplyMotionBoxes(bool show) => _hikPlayer?.SetMotionBoxes(show);

    /// <summary>Stops playback (view left); the camera stays assigned.</summary>
    public void Stop()
    {
        StopClip();
        StopPlayers();
        IsParked = false;
        if (Camera is not null)
        {
            SetState(TileState.Stopped, "Durduruldu");
        }
    }

    // ---- SDK path ----

    /// <summary>Login + PlayBackByTime are blocking network calls, so they run off the UI thread.</summary>
    void OpenHik(DateTime time)
    {
        DisposeFlyleafPlayer();
        Host.Visibility = Visibility.Collapsed;
        HikHost.Visibility = Visibility.Visible;
        VideoLayer.Visibility = Visibility.Visible;
        HikHost.UpdateLayout(); // build the child HWND now
        SetState(TileState.Connecting);

        var device = Device!;
        var camera = Camera!;
        var key = device.StreamKeyFor(camera);
        var end = time.Date.AddDays(1).AddSeconds(-1);
        var generation = Interlocked.Increment(ref _openGeneration);

        // Created and stored on the UI thread, so every dispose path (UI thread) sees it.
        if (_hikPlayer is null)
        {
            _hikPlayer = new HikPlaybackPlayer();
            _hikPlayer.Started += OnHikStarted;
            _hikPlayer.Error += OnHikError;
            _hikPlayer.Ended += OnHikEnded;
        }
        var player = _hikPlayer;
        player.ShowMotionBoxes = App.Settings.ShowMotionBoxes;
        player.RequestedSpeedSteps = PlaybackSpeed.Steps(_speedIndex);
        player.RequestedPaused = _paused;

        Task.Run(async () =>
        {
            await _sdkGate.WaitAsync();
            try
            {
                if (IsStale(player, generation))
                {
                    return;
                }
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
                var session = HikSession.GetOrCreate(device);
                var userId = session.UserId;
                if (IsStale(player, generation))
                {
                    return; // a newer seek or a dispose superseded us
                }
                // Recorders number their digital channels from StartDChan (e.g. 33); ISAPI is 1-based.
                player.Start(userId, session.SdkChannel(camera.Channel), time, end, hwnd, key);
                if (player.IsDisposed)
                {
                    player.Stop(); // disposed while opening: do not leave the stream running
                }
            }
            catch (HikException ex)
            {
                // E.g. the recorder's limit of simultaneous playbacks: this tile shows why, the others keep playing.
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!IsStale(player, generation) && Camera is not null)
                    {
                        SetState(TileState.Failed, $"Oynatılamadı\n{Shorten(ex.Message)}");
                    }
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

    bool IsStale(HikPlaybackPlayer player, int generation) =>
        player.IsDisposed || Volatile.Read(ref _openGeneration) != generation;

    /// <summary>Speed / pause commands block on the network: queued behind the tile's open, off the UI thread.</summary>
    void SyncSdkControls(HikPlaybackPlayer player)
    {
        Task.Run(async () =>
        {
            await _sdkGate.WaitAsync();
            try
            {
                if (!player.IsDisposed)
                {
                    player.SyncControls();
                }
            }
            catch (HikException ex)
            {
                _ = Dispatcher.BeginInvoke(() => Notice?.Invoke(this, $"{Camera?.Name}: {Shorten(ex.Message)}"));
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

    void OnHikStarted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_hikPlayer is not { } player || Camera is null || IsParked)
            {
                return;
            }
            MeasureLatency();
            _preferHik = true;
            SetState(TileState.Playing);
            // Fresh handle = normal speed and playing: bring it to the shared speed / pause state.
            player.RequestedSpeedSteps = PlaybackSpeed.Steps(_speedIndex);
            player.RequestedPaused = _paused;
            SyncSdkControls(player);
        });
    }

    void OnHikError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_hikPlayer is not null && Camera is not null && !IsParked)
            {
                SetState(TileState.Failed, $"Oynatma hatası\n{Shorten(message)}");
            }
        });
    }

    void OnHikEnded()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_hikPlayer is not null && Camera is not null && !IsParked)
            {
                StopPlayers();
                IsParked = true; // resumed by the view if the shared time is still recorded (e.g. after a network blip)
                SetState(TileState.Stopped, "Kayıt sonu");
            }
        });
    }

    // ---- Flyleaf path ----

    void OpenFlyleaf(DateTime time)
    {
        DisposeHikPlayer();
        HikHost.Visibility = Visibility.Collapsed;
        Host.Visibility = Visibility.Visible;
        VideoLayer.Visibility = Visibility.Visible;
        if (_player is null)
        {
            _player = Players.CreatePlayback();
            _player.PropertyChanged += Player_PropertyChanged;
            Host.Player = _player;
        }
        _player.Audio.Mute = !_focused;
        if (_player.IsRecording)
        {
            _player.StopRecording();
        }
        _openedAt = time;
        SetState(TileState.Connecting);
        _player.OpenAsync(StreamUrls.Playback(Device!, Camera!.Channel, time, time.Date.AddDays(1).AddSeconds(-1)));
    }

    void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Player.Status) or nameof(Player.IsRecording)))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_player is not { } player || Camera is null || IsParked)
            {
                return;
            }
            switch (player.Status)
            {
                case Status.Opening:
                    SetState(TileState.Connecting);
                    break;
                case Status.Playing or Status.Paused:
                    if (_awaitingFirstFrame)
                    {
                        MeasureLatency();
                        ApplyFlyleafSpeed(player);
                        if (_paused && player.Status == Status.Playing)
                        {
                            player.Pause();
                        }
                    }
                    SetState(TileState.Playing);
                    break;
                case Status.Failed when HikSdk.Available && !_triedHikFallback:
                    // FFmpeg could not open/decode: typical for encrypted recordings. Try the SDK once.
                    _triedHikFallback = true;
                    _preferHik = true;
                    SetState(TileState.Connecting, header: "SDK deneniyor…");
                    var target = _seekTarget;
                    Dispatcher.BeginInvoke(() => OpenHik(target)); // deferred: not inside the player's own event
                    break;
                case Status.Failed:
                    SetState(TileState.Failed, $"Oynatılamadı\n{Shorten(player.LastError)}");
                    break;
                case Status.Ended:
                    player.Stop();
                    IsParked = true;
                    SetState(TileState.Stopped, "Kayıt sonu");
                    break;
                default:
                    StateChanged?.Invoke(this); // recording flag
                    break;
            }
        });
    }

    /// <summary>
    /// RTSP playback is paced by the recorder, so Flyleaf can slow down freely but can only speed up as far as
    /// its buffer allows; drift correction re-seeks a tile that falls behind.
    /// </summary>
    void ApplyFlyleafSpeed(Player player)
    {
        try
        {
            player.Speed = PlaybackSpeed.Rate(_speedIndex);
        }
        catch (ArgumentException)
        {
            // Outside Flyleaf's range: keep its current speed.
        }
    }

    void MeasureLatency()
    {
        if (_awaitingFirstFrame)
        {
            _awaitingFirstFrame = false;
            OpenLatency = TimeSpan.FromTicks(Math.Clamp(_openWatch.Elapsed.Ticks, TimeSpan.FromMilliseconds(200).Ticks, TimeSpan.FromSeconds(5).Ticks));
        }
    }

    // ---- Snapshot / clip (focused tile) ----

    /// <summary>JPEG of the frame on screen; null when nothing plays.</summary>
    public async Task<string?> TakeSnapshotAsync()
    {
        if (Camera is null)
        {
            return null;
        }
        System.IO.Directory.CreateDirectory(App.Settings.SnapshotFolder);
        var path = System.IO.Path.Combine(App.Settings.SnapshotFolder, $"{Safe(Camera.Name)}_{CurrentTime ?? DateTime.Now:yyyyMMdd_HHmmss}_kayit.jpg");
        if (_hikPlayer is { IsStarted: true } hik && UsesSdk)
        {
            return await Task.Run(() => hik.Snapshot(path));
        }
        if (_player?.Status is Status.Playing or Status.Paused)
        {
            _player.TakeSnapshotToFile(path, 0, 0);
            return path;
        }
        return null;
    }

    /// <summary>Starts or ends an MP4 clip of what plays. Returns the user message.</summary>
    public string ToggleClip()
    {
        if (Camera is null)
        {
            return "Önce bir kare seçin.";
        }
        if (IsClipping)
        {
            return StopClip() ?? "Klip kaydedildi.";
        }
        System.IO.Directory.CreateDirectory(App.Settings.RecordingFolder);
        var file = System.IO.Path.Combine(App.Settings.RecordingFolder, $"{Safe(Camera.Name)}_{CurrentTime ?? DateTime.Now:yyyyMMdd_HHmmss}_klip.mp4");
        if (_hikPlayer is { IsStarted: true } hik && UsesSdk)
        {
            try
            {
                hik.StartSave(file);
            }
            catch (HikException ex)
            {
                return $"Klip başlatılamadı: {Shorten(ex.Message)}";
            }
        }
        else if (_player?.Status == Status.Playing)
        {
            _player.StartRecording(ref file, false);
        }
        else
        {
            return "Klip için kare oynuyor olmalı.";
        }
        _clipFile = file;
        ClipBadge.Visibility = Visibility.Visible;
        StateChanged?.Invoke(this);
        return $"Klip kaydediliyor: {file}";
    }

    /// <summary>Ends a running clip (also before every seek: a clip is one continuous piece). Returns the message, if any.</summary>
    string? StopClip()
    {
        if (!IsClipping)
        {
            return null;
        }
        if (_player?.IsRecording == true)
        {
            _player.StopRecording();
        }
        _hikPlayer?.StopSave();
        ClipBadge.Visibility = Visibility.Collapsed;
        var message = $"Klip kaydedildi: {_clipFile}";
        _clipFile = null;
        Notice?.Invoke(this, message);
        StateChanged?.Invoke(this);
        return message;
    }

    static string Safe(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    static string Shorten(string? error) =>
        string.IsNullOrEmpty(error) ? "" : error.Length > 120 ? error[..120] + "…" : error;

    // ---- State / lifetime ----

    /// <summary>
    /// Updates the header strip. Stopped/failed tiles hide their (black) video host so the reason can be shown
    /// in the video area; the next seek shows the host again.
    /// </summary>
    void SetState(TileState state, string? detail = null, string? header = null)
    {
        _state = state;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch
        {
            TileState.Playing => "Online",
            TileState.Connecting => "Warning",
            TileState.Failed => "Danger",
            _ => "Offline",
        });
        StateText.Text = header ?? "Bağlanıyor…";
        StateText.Visibility = state == TileState.Connecting ? Visibility.Visible : Visibility.Collapsed;
        var showMessage = state is TileState.Failed or TileState.Stopped;
        MessageText.Text = showMessage ? detail ?? "" : "";
        VideoLayer.Visibility = showMessage ? Visibility.Hidden : Visibility.Visible;
        Header.ToolTip = Camera is null
            ? null
            : $"{Camera.Name} · Kanal {Camera.Channel}" + (string.IsNullOrEmpty(detail) ? "" : $"\n{detail}");
        StateChanged?.Invoke(this);
    }

    void ShowEmpty()
    {
        CameraName.Text = "";
        ClipBadge.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Collapsed;
        EmptyFrame.Visibility = Visibility.Visible;
        SetState(TileState.Empty);
    }

    /// <summary>Stops the streams but keeps the player objects (the next seek reuses them).</summary>
    void StopPlayers()
    {
        var generation = Interlocked.Increment(ref _openGeneration); // cancels in-flight opens
        _player?.Stop();
        if (_hikPlayer is { } player)
        {
            Task.Run(async () =>
            {
                await _sdkGate.WaitAsync();
                try
                {
                    if (Volatile.Read(ref _openGeneration) == generation)
                    {
                        player.Stop(); // blocking (NET_DVR_StopPlayBack)
                    }
                }
                finally
                {
                    _sdkGate.Release();
                }
            });
        }
    }

    void DisposePlayers(bool waitForSdk = false)
    {
        Interlocked.Increment(ref _openGeneration); // cancel in-flight SDK opens
        DisposeFlyleafPlayer();
        DisposeHikPlayer(waitForSdk);
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
    /// NET_DVR_StopPlayBack blocks, and waits for an open still in progress on the player lock, so it normally
    /// runs off the UI thread; at shutdown it must finish before the SDK is cleaned up.
    /// </summary>
    void DisposeHikPlayer(bool wait = false)
    {
        Interlocked.Increment(ref _openGeneration);
        if (_hikPlayer is not { } player)
        {
            return;
        }
        player.Started -= OnHikStarted;
        player.Error -= OnHikError;
        player.Ended -= OnHikEnded;
        _hikPlayer = null;
        if (wait)
        {
            player.Dispose();
        }
        else
        {
            Task.Run(player.Dispose);
        }
    }

    // ---- Input: WPF parts, the SDK HWND (transparent to hit testing) and Flyleaf's surface window ----

    void HookSurface(Window? surface)
    {
        if (surface is null || ReferenceEquals(surface, _surface))
        {
            return;
        }
        if (_surface is not null)
        {
            _surface.MouseLeftButtonDown -= Surface_MouseLeftButtonDown;
        }
        _surface = surface;
        surface.MouseLeftButtonDown += Surface_MouseLeftButtonDown;
    }

    void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Activate();

    void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Activate();

    void Activate()
    {
        if (Camera is null)
        {
            return; // empty tiles cannot take the focus
        }
        Activated?.Invoke(this);
        // Deferred: a click on Flyleaf's surface window must not leave keyboard focus there (F11 etc.).
        Dispatcher.BeginInvoke(() => Focus());
    }
}
