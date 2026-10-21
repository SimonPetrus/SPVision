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

    readonly SemaphoreSlim _sdkGate = new(1, 1); // serializes SDK commands of this tile (open, stop, controls, clip)
    readonly Stopwatch _openWatch = new();
    readonly Stopwatch _seekWatch = new();       // since the view's seek (a wrong-start reopen keeps counting)

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
    bool _flyleafClipping;           // Flyleaf is remuxing the clip (audio switched off meanwhile: MP4 takes no G.711)
    bool _hikClipping;               // SDK clip asked for (the save itself runs in the SDK queue)
    bool _timeVerified;              // SDK: the first frame's real time was checked against the requested time
    int _startRetries;               // reopens of the current seek because the recorder started far too early
    int _clipGeneration;             // supersedes the failure report of an earlier clip start
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

    /// <summary>Why the last recording search failed (shown by the view for the focused camera); null when it worked.</summary>
    public string? SearchError { get; private set; }

    public TileState State => _state;

    /// <summary>Waiting for its next recording (or the recording ended): resumed by the view when the shared time is recorded.</summary>
    public bool IsParked { get; private set; }

    /// <summary>When the last seek was issued (the view does not re-seek a tile again right away).</summary>
    public DateTime LastSeekUtc { get; private set; }

    /// <summary>How long the last open took until the first frame; the view leads drift re-seeks by it.</summary>
    public TimeSpan OpenLatency { get; private set; } = TimeSpan.FromSeconds(1);

    public bool UsesSdk => Camera is not null && HikHost.Visibility == Visibility.Visible;

    public bool IsClipping => _flyleafClipping || _hikClipping;

    /// <summary>How the view is bringing this tile back in step without reopening it (SDK path only).</summary>
    public SyncNudge Nudge { get; private set; }

    DateTime _nudgeSince;

    /// <summary>How long the current nudge runs (real time).</summary>
    public TimeSpan NudgeAge => Nudge == SyncNudge.None ? TimeSpan.Zero : DateTime.UtcNow - _nudgeSince;

    /// <summary>
    /// The tile's time is the real frame time and it can be sped up / held (SDK path). Flyleaf's RTSP playback is paced
    /// by the recorder and its time is estimated from the open: those tiles are re-seeked instead.
    /// </summary>
    public bool CanNudge => UsesSdk && _hikPlayer is { IsStarted: true } && _timeVerified;

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

    /// <summary>
    /// Recorder time of the frame on screen; null when nothing plays. SDK path: the absolute time in the stream (what
    /// the OSD shows), and only once the start position was checked (a wrong start is reopened before anyone sees it).
    /// </summary>
    public DateTime? CurrentTime =>
        _player is { Status: Status.Playing or Status.Paused } p ? _openedAt + TimeSpan.FromTicks(Math.Max(0, p.CurTime))
        : _hikPlayer?.IsStarted == true && _timeVerified && HikHost.Visibility == Visibility.Visible ? _hikPlayer.GetTime()
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
        SearchError = null;
        IsParked = false;
        _preferHik = false;
        EmptyFrame.Visibility = Visibility.Collapsed;
        Frame.Visibility = Visibility.Visible;
        CameraName.Text = camera.Name;
        SetState(TileState.Connecting, header: "Kayıtlar aranıyor…");
    }

    public void Clear() => Clear(waitForSdk: false);

    /// <summary>
    /// Window closing: ends the clip and releases the stream. The SDK part (stop, MP4 finalization) runs on a worker
    /// thread, so all tiles release in parallel and the UI never waits; the task ends when it is done.
    /// </summary>
    public Task ReleaseAsync()
    {
        _hikClipping = false;
        StopFlyleafClip(report: false);
        _clipFile = null;
        Interlocked.Increment(ref _openGeneration);
        _showGeneration++;
        DisposeFlyleafPlayer();
        if (_hikPlayer is not { } player)
        {
            return Task.CompletedTask;
        }
        DetachHik(player);
        // Not in the tile's SDK queue (an open stuck on the network must not hold the exit up); the player lock
        // orders the dispose after a command in progress, and Dispose finalizes a running clip.
        return Task.Run(() => player.Dispose());
    }

    void Clear(bool waitForSdk)
    {
        StopClip();
        DisposePlayers(waitForSdk);
        _showGeneration++;
        Device = null;
        Camera = null;
        Segments = null;
        SearchDone = false;
        SearchError = null;
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

    /// <summary>
    /// Searches the camera's recordings of the day (ISAPI, read-only). False when the tile changed meanwhile.
    /// <paramref name="keepOnFailure"/>: a refresh of known segments keeps them when the recorder does not answer.
    /// </summary>
    public async Task<bool> LoadSegmentsAsync(DateTime day, SemaphoreSlim searches, CancellationToken ct, bool keepOnFailure = false)
    {
        if (Device is not { } device || Camera is not { } camera)
        {
            return false;
        }
        var generation = _showGeneration;
        List<RecordingSegment>? segments;
        string? error = null;
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
            error = e.Message;
        }
        if (generation != _showGeneration || ct.IsCancellationRequested)
        {
            return false; // the tile got another camera (or day) meanwhile
        }
        if (segments is not null || !keepOnFailure || Segments is null)
        {
            Segments = segments;
            SearchError = error;
        }
        SearchDone = true;
        return true;
    }

    /// <summary>
    /// Plays from <paramref name="time"/> at the given speed (paused or not). When the camera has no recording
    /// there, the tile is parked instead. Every call reopens the stream, which is what keeps tiles in step.
    /// </summary>
    public void SeekTo(DateTime time, int speedIndex, bool paused)
    {
        _startRetries = 0;
        _seekWatch.Restart();
        SeekCore(time, speedIndex, paused);
    }

    void SeekCore(DateTime time, int speedIndex, bool paused)
    {
        _speedIndex = speedIndex;
        _paused = paused;
        Nudge = SyncNudge.None; // a fresh open starts at the shared speed
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
        Nudge = SyncNudge.None;
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
        Nudge = SyncNudge.None;
        if (_hikPlayer is { } hik && UsesSdk)
        {
            ApplySdkControls(hik);
        }
        else if (_player is { Status: Status.Playing or Status.Paused })
        {
            ApplyFlyleafSpeed(_player);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Nudge = SyncNudge.None;
        if (_hikPlayer is { } hik && UsesSdk)
        {
            ApplySdkControls(hik);
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

    /// <summary>
    /// The view's drift correction: <see cref="SyncNudge.CatchUp"/> plays the tile one speed step faster,
    /// <see cref="SyncNudge.Hold"/> pauses it, <see cref="SyncNudge.None"/> returns to the shared speed / pause state.
    /// No reopen, so no black frame and no key-frame offset (SDK path; see <see cref="CanNudge"/>).
    /// </summary>
    public void SetNudge(SyncNudge nudge)
    {
        if (nudge == Nudge || (nudge != SyncNudge.None && !CanNudge))
        {
            return;
        }
        Nudge = nudge;
        _nudgeSince = DateTime.UtcNow;
        if (_hikPlayer is { } hik && UsesSdk)
        {
            ApplySdkControls(hik);
        }
    }

    /// <summary>Shared speed / pause plus the nudge, applied in the SDK queue.</summary>
    void ApplySdkControls(HikPlaybackPlayer hik)
    {
        hik.RequestedSpeedSteps = PlaybackSpeed.Steps(_speedIndex) + (Nudge == SyncNudge.CatchUp ? 1 : 0);
        hik.RequestedPaused = _paused || Nudge == SyncNudge.Hold;
        SyncSdkControls(hik);
    }

    /// <summary>
    /// Settings or the top bar toggled the motion boxes (SDK path; Flyleaf does not draw the recorder's private
    /// data). An open in progress picks the value up when its port opens; a running stream is switched in the
    /// tile's SDK queue, off the UI thread (the player lock is held while the recorder answers a command).
    /// </summary>
    public void ApplyMotionBoxes(bool show)
    {
        if (_hikPlayer is not { } player)
        {
            return;
        }
        player.ShowMotionBoxes = show;
        QueueSdk(() =>
        {
            if (!player.IsDisposed)
            {
                player.SetMotionBoxes(show);
            }
        });
    }

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

    /// <summary>Restored camera whose recordings are known: nothing plays until the user picks a day or presses play.</summary>
    public void ShowReady()
    {
        if (Camera is not null)
        {
            SetState(TileState.Stopped, "Oynatmaya hazır\nGün seçin ya da Oynat'a basın");
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
        _timeVerified = false;

        // Created and stored on the UI thread, so every dispose path (UI thread) sees it.
        if (_hikPlayer is null)
        {
            _hikPlayer = new HikPlaybackPlayer();
            _hikPlayer.Started += OnHikStarted;
            _hikPlayer.Error += OnHikError;
            _hikPlayer.Ended += OnHikEnded;
            _hikPlayer.FirstFrameTime += OnHikFirstFrameTime;
        }
        var player = _hikPlayer;
        player.ShowMotionBoxes = App.Settings.ShowMotionBoxes;
        player.RequestedSpeedSteps = PlaybackSpeed.Steps(_speedIndex);
        player.RequestedPaused = _paused;

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

    /// <summary>
    /// Runs a blocking SDK call in the tile's SDK queue, off the UI thread. The turn is taken by the caller (the
    /// UI thread), so commands run in the order they were issued (e.g. a clip stop before the next seek's open).
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

    /// <summary>Speed / pause commands block on the network: queued behind the tile's open, off the UI thread.</summary>
    void SyncSdkControls(HikPlaybackPlayer player)
    {
        var turn = _sdkGate.WaitAsync();
        Task.Run(async () =>
        {
            await turn;
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
            ApplySdkControls(player);
            if (!App.Settings.ShowMotionBoxes)
            {
                ApplyMotionBoxes(false); // again on the running handle (the SDK's own port may open after ours)
            }
            // PlayCtrl reports the first frame's real time shortly (OnHikFirstFrameTime). Should it never (a stream
            // without absolute time), the SDK's own time is used after a while instead of none at all.
            var generation = Volatile.Read(ref _openGeneration);
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _openGeneration) == generation && ReferenceEquals(_hikPlayer, player))
                {
                    _timeVerified = true;
                }
            }), TaskScheduler.Default);
        });
    }

    /// <summary>
    /// The first decoded frame's absolute time (SDK thread). The recorder sometimes starts a playback by time at the
    /// beginning of the recording file instead of the requested time (DIS KAPI: 23:11:04 for 00:00:00, the file
    /// began at 23:10:49, and NET_DVR_PLAYGETTIME hid it). Such a start is reopened at once, at most
    /// <see cref="PlaybackStart.MaxRetries"/> times; a start on the key frame before the request is fine.
    /// </summary>
    void OnHikFirstFrameTime(DateTime first)
    {
        var generation = Volatile.Read(ref _openGeneration);
        Dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _openGeneration) != generation || _hikPlayer is null || Camera is null || IsParked)
            {
                return; // an older open (the tile seeked meanwhile)
            }
            if (PlaybackStart.IsWrong(_seekTarget, first))
            {
                if (_startRetries < PlaybackStart.MaxRetries)
                {
                    _startRetries++;
                    var target = PlaybackStart.RetryTarget(_seekTarget, _seekWatch.Elapsed, PlaybackSpeed.Rate(_speedIndex), _paused);
                    SeekCore(target, _speedIndex, _paused);
                    SetState(TileState.Connecting, header: "Konum düzeltiliyor…");
                    return;
                }
                Notice?.Invoke(this, $"{Camera.Name}: kayıt cihazı oynatmayı istenen saatten başlatmadı ({first:HH:mm:ss}); eşitleme yeniden deneyecek.");
            }
            _timeVerified = true;
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
                StopClip(); // the clip ends with the recording: finalized and reported
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
        StopFlyleafClip(report: true);
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
            if (_flyleafClipping && !player.IsRecording)
            {
                StopClip(); // Flyleaf ended the recording itself (stream lost): finalize the badge and report the file
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
                    StopClip();
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

    /// <summary>
    /// Starts or ends an MP4 clip of what plays. Returns the immediate user message; "Klip kaydedildi: …" (or why not)
    /// follows as a <see cref="Notice"/> once the file is finalized and verified.
    /// SDK path: the decrypted picture is encoded to H.264 (HikFrameRecorder). The SDK's own NET_DVR_PlayBackSaveData
    /// wrote the encrypted PS stream — 0 bytes here, the old 0-byte clip — and the message came before the stop had even run.
    /// </summary>
    public string ToggleClip()
    {
        if (Camera is null)
        {
            return "Önce bir kare seçin.";
        }
        if (IsClipping)
        {
            return StopClip() ?? RecordingText.Finishing(clip: true);
        }
        System.IO.Directory.CreateDirectory(App.Settings.RecordingFolder);
        var file = System.IO.Path.Combine(App.Settings.RecordingFolder, $"{Safe(Camera.Name)}_{CurrentTime ?? DateTime.Now:yyyyMMdd_HHmmss}_klip.mp4");
        if (_hikPlayer is { IsStarted: true } hik && UsesSdk)
        {
            // NET_DVR_PlayBackSaveData waits for the SDK's data thread (and the player for a command in progress):
            // never on the UI thread. The badge shows at once; a refusal is reported and takes it back.
            _hikClipping = true;
            var generation = ++_clipGeneration;
            QueueSdk(() =>
            {
                string? error;
                try
                {
                    error = hik.StartSave(file) ? null : "oynatma durdu ya da görüntü henüz gelmedi";
                }
                catch (HikException ex)
                {
                    error = ex.Message;
                }
                if (error is not null)
                {
                    _ = Dispatcher.BeginInvoke(() => ClipStartFailed(generation, error));
                }
            });
        }
        else if (_player?.Status == Status.Playing)
        {
            if (!StartFlyleafClip(ref file))
            {
                return RecordingText.StartFailed(clip: true, "akış MP4 olarak kaydedilemiyor");
            }
        }
        else
        {
            return "Klip için kare oynuyor olmalı.";
        }
        _clipFile = file;
        ClipBadge.Visibility = Visibility.Visible;
        StateChanged?.Invoke(this);
        return RecordingText.Started(clip: true, file);
    }

    /// <summary>
    /// Flyleaf remuxes the RTSP streams into the MP4. Hikvision cameras send G.711 audio, which MP4 cannot hold (the
    /// header write fails and leaves a 0-byte file), so audio is switched off for the clip: video only.
    /// </summary>
    bool StartFlyleafClip(ref string file)
    {
        var player = _player!;
        try
        {
            player.Config.Audio.Enabled = false;
        }
        catch (Exception)
        {
            // Audio stays: a G.711 stream then fails the MP4 start below, which is reported.
        }
        player.StartRecording(ref file, false);
        if (player.IsRecording)
        {
            _flyleafClipping = true;
            return true;
        }
        RestoreFlyleafAudio(player);
        _ = Recordings.DeleteIfEmptyAsync(file);
        return false;
    }

    /// <summary>Ends a Flyleaf clip; <paramref name="report"/>: verify the file and tell the user.</summary>
    void StopFlyleafClip(bool report)
    {
        if (!_flyleafClipping || _player is not { } player)
        {
            _flyleafClipping = false;
            return;
        }
        _flyleafClipping = false;
        if (player.IsRecording)
        {
            player.StopRecording(); // Flyleaf writes the MP4 index here
        }
        RestoreFlyleafAudio(player);
        if (report && _clipFile is { } file)
        {
            Report(Recordings.VerifyAsync(file));
        }
    }

    static void RestoreFlyleafAudio(Player player)
    {
        try
        {
            player.Config.Audio.Enabled = true;
        }
        catch (Exception)
        {
            // The next open of the tile starts with audio again anyway.
        }
    }

    /// <summary>Shows the outcome once the file is finalized (any thread; the tile may show another camera by then).</summary>
    void Report(Task<RecordingResult>? finishing)
    {
        finishing?.ContinueWith(t =>
        {
            var message = t.IsCompletedSuccessfully
                ? RecordingText.Finished(t.Result, clip: true)
                : RecordingText.Finished(new RecordingResult("", false, 0, TimeSpan.Zero, 0, null, t.Exception?.InnerException?.Message), clip: true);
            Dispatcher.BeginInvoke(() => Notice?.Invoke(this, message));
        }, TaskScheduler.Default);
    }

    void ClipStartFailed(int generation, string error)
    {
        if (generation != _clipGeneration || !_hikClipping)
        {
            return; // stopped (or restarted) meanwhile
        }
        _hikClipping = false;
        _clipFile = null;
        ClipBadge.Visibility = Visibility.Collapsed;
        Notice?.Invoke(this, RecordingText.StartFailed(clip: true, Shorten(error)));
        StateChanged?.Invoke(this);
    }

    /// <summary>
    /// Ends a running clip (also before every seek: a clip is one continuous piece). Returns the immediate message,
    /// if any; the final one ("Klip kaydedildi: …" or why not) comes when the file is complete.
    /// </summary>
    string? StopClip()
    {
        if (!IsClipping)
        {
            return null;
        }
        StopFlyleafClip(report: true);
        if (_hikClipping)
        {
            _hikClipping = false;
            _clipGeneration++;
            if (_hikPlayer is { } hik)
            {
                // In the SDK queue: after a start still in progress, ahead of the next seek's open.
                QueueSdk(() => Report(hik.StopSave()));
            }
        }
        ClipBadge.Visibility = Visibility.Collapsed;
        _clipFile = null;
        var message = RecordingText.Finishing(clip: true);
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
            QueueSdk(() =>
            {
                if (Volatile.Read(ref _openGeneration) == generation)
                {
                    player.Stop(); // blocking (NET_DVR_StopPlayBack)
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
        DetachHik(player);
        if (wait)
        {
            player.Dispose();
        }
        else
        {
            Task.Run(player.Dispose);
        }
    }

    void DetachHik(HikPlaybackPlayer player)
    {
        player.Started -= OnHikStarted;
        player.Error -= OnHikError;
        player.Ended -= OnHikEnded;
        player.FirstFrameTime -= OnHikFirstFrameTime;
        _hikPlayer = null;
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
