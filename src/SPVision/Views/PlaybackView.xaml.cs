using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SPVision.Controls;
using SPVision.Core;
using Path = System.Windows.Shapes.Path;

namespace SPVision.Views;

/// <summary>
/// Synchronous multi-camera playback. The sidebar picks the day (calendar, ISAPI recording search) and the
/// cameras; every tile plays the same moment and the controls act on all of them.
/// <para>
/// Sync: one shared clock ("master") advances with the playback speed and follows the focused tile's real
/// time. Every seek and speed change is re-issued to every tile. Every <see cref="PlaybackSync.CheckInterval"/>
/// each tile's time is compared with the master; a tile more than <see cref="PlaybackSync.Tolerance"/> off is
/// re-seeked, a tile without recording at the master time is parked until its next recording. A tile the
/// recorder refuses (e.g. too many simultaneous playbacks) shows the reason; the others keep running.
/// </para>
/// </summary>
public partial class PlaybackView : UserControl
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(30);

    /// <summary>After a seek the clock waits this long at most for the focused tile's first frame.</summary>
    static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(6);

    /// <summary>A tile that was just seeked is left alone this long (its stream is still starting).</summary>
    static readonly TimeSpan SeekGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The focused tile corrects the clock when it differs by more than this (its time has 1 s steps); scaled up
    /// at high speeds, where a second of recording passes in a fraction of a second.
    /// </summary>
    static readonly TimeSpan FollowThreshold = TimeSpan.FromSeconds(1.5);

    static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(15);

    sealed record CameraRow(ToggleButton Button, Border Box, Path Mark, TextBlock Number, Camera Camera, CameraRef Source);

    readonly List<PlaybackTile> _tiles = [];
    readonly List<CameraRow> _rows = [];
    readonly List<(ToggleButton Button, Ellipse Dot, DateTime Date)> _dayButtons = [];
    readonly DispatcherTimer _tick = new() { Interval = TickInterval };
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly Stopwatch _clock = new();
    readonly Stopwatch _holdWatch = new();
    readonly SemaphoreSlim _searches = new(4, 4); // ISAPI searches at once (the recorder serves them one by one anyway)
    readonly SemaphoreSlim _markSearches = new(2, 2); // calendar requests at once (month queries, or day searches)
    readonly RecordedDays _recordedDays = new();
    readonly Dictionary<CameraRef, CancellationTokenSource> _markLoaders = []; // calendar searches per checked camera

    GridLayout _layout = GridLayout.PlaybackDefault;
    PlaybackSlots _slots = new(GridLayout.PlaybackDefault.TileCount);
    DateTime _day = DateTime.Today;
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime _anchor = DateTime.Today; // master time when _clock was (re)started
    bool _hasTime;                     // a shared time is set (first recording of the day, a seek…)
    bool _holding;                     // after a seek: the clock waits for the focused tile's first frame
    bool _startFromMidnight;           // a day was double-clicked: start it at 00:00, not at the first recording
    bool _paused;
    bool _stopped;                     // Stop pressed: streams closed, the shared time kept (Play restarts there)
    DateTime _seekFloor = DateTime.Today; // last requested time: the clock never follows a tile to a moment before it
    bool _active;                      // the view is shown and playing
    bool _awaitUser;                   // restored cameras: no start until the user picks a day, plays, seeks or adds a camera
    IReadOnlyList<CameraRef>? _restoreCameras; // cameras of the last session, put on the tiles when the view is first shown
    int _speed = PlaybackSpeed.Normal;
    int _tickCount;
    TimeSpan _sinceDriftCheck;
    string? _message;
    bool _stateChangePending;
    CancellationTokenSource _dayCts = new();
    DateTime _markMonth;               // month the running calendar searches are for
    (CameraRef Camera, DateTime Day)? _timelineKey; // focused camera and day the timeline last asked the recorder for

    public PlaybackView()
    {
        InitializeComponent();
        for (var i = 0; i < GridLayout.PlaybackMaxTiles; i++)
        {
            var tile = new PlaybackTile { Visibility = Visibility.Collapsed };
            tile.Activated += t => FocusTile(_tiles.IndexOf(t));
            tile.DoubleClicked += ToggleMaximize;
            tile.StateChanged += _ => UpdateUi(); // header, clip button, status bar
            tile.Notice += (_, text) => Message?.Invoke(text);
            // Deferred: the grid is re-arranged outside the drag-and-drop loop (and outside Flyleaf's surface window event).
            tile.CameraDropped += (target, source) => Dispatcher.BeginInvoke(() => DropCamera(target, source));
            tile.TileDropped += (from, to) => Dispatcher.BeginInvoke(() => DropTile(from, to));
            tile.Dragging += (_, on) => SetDragging(on);
            _tiles.Add(tile);
            TileGrid.Children.Add(tile); // always children: layout changes only move/hide them (no unload = no reopen)
        }
        foreach (var name in CalendarMonth.WeekdayNames)
        {
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(1, 4, 1, 4),
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        }

        Timeline.TimeSelected += time => SeekAll(time);
        Timeline.ViewChanged += UpdateZoomButtons;
        TransientPopup.Attach(GoToPopup, TimeButton);
        TransientPopup.Place(GoToPopup, (popup, target, scale) => new Point((target.Width - popup.Width) / 2, -popup.Height - 8 * scale));
        GoToPopup.Opened += (_, _) => OpenGoTo();
        _tick.Tick += (_, _) => Tick();
        _messageTimer.Tick += (_, _) => ShowMessage(null);
        _hoverTimer.Tick += (_, _) => HoverTick();
        TileBar.SnapshotRequested += tile => _ = SnapshotAsync(tile);
        TileBar.ClipRequested += ToggleClip;
        TileBar.ZoomRequested += tile => tile.ToggleZoom();
        TileBar.DownloadRequested += OpenDownload;
        DownloadPanel.CloseRequested += CloseDownload;
        Loaded += (_, _) => HookWindow();

        // Layout of the last session now (the top bar picker shows it); its cameras when the view is first shown,
        // so nothing is searched on the recorder before the user opens Kayıt Oynatma.
        var restore = PlaybackRestore.From(App.Settings);
        _layout = restore.Layout;
        _slots = new PlaybackSlots(_layout.TileCount);
        _restoreCameras = restore.Cameras.Count > 0 ? restore.Cameras : null;

        ArrangeTiles();
        BuildCalendar();
        UpdateZoomButtons();
        UpdateUi();
    }

    /// <summary>Short feedback for the user ("Kaydedildi: …"); the main window shows it in the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>Layout, tiles or day changed (coalesced): top bar picker and status bar follow it.</summary>
    public event Action? StateChanged;

    public GridLayout Layout => _layout;

    public DateTime Day => _day;

    /// <summary>Tiles of the current layout (empty ones included).</summary>
    public IReadOnlyList<PlaybackTile> VisibleTiles => _tiles.Take(_layout.TileCount).ToList();

    /// <summary>F11 hides the sidebar like the live one.</summary>
    public bool SidebarVisible
    {
        set => Sidebar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    PlaybackTile? FocusedTile => _slots.Focused is null ? null : _tiles[_slots.Focus];

    IEnumerable<PlaybackTile> ActiveTiles => _tiles.Take(_layout.TileCount).Where(t => t.Camera is not null);

    double Rate => PlaybackSpeed.Rate(_speed);

    /// <summary>The shared playback time: advances with the speed unless paused, stopped or waiting for the first frame.</summary>
    DateTime Master => _holding || _paused || _stopped || !_hasTime || !_active ? _anchor : ClampDay(_anchor + _clock.Elapsed * Rate);

    TransportState Transport => PlaybackTransport.State(_hasTime, _paused, _stopped);

    // ---- Main window ----

    /// <summary>The view is shown: resume where it was left (all tiles re-seek to the shared time).</summary>
    public void Activate()
    {
        _active = true;
        _tick.Start();
        _hoverTimer.Start();
        RestoreCameras();
        // Stopped before the view was left: it stays stopped (the tiles were stopped with the view); Play restarts.
        if (Transport == TransportState.Stopped)
        {
            UpdateUi();
            return;
        }
        if (_hasTime && ActiveTiles.Any())
        {
            SetMaster(_anchor);
            StartHold();
            foreach (var tile in ActiveTiles.Where(t => t.SearchDone))
            {
                tile.SeekTo(_anchor, _speed, _paused);
            }
        }
        else
        {
            TryStart();
        }
        UpdateUi();
    }

    /// <summary>The view is left: streams stop, cameras, day and time are kept.</summary>
    public void Stop()
    {
        if (_active)
        {
            _anchor = Master;
            _clock.Restart();
        }
        _active = false;
        _tick.Stop();
        _hoverTimer.Stop();
        HideTileBar();
        GoToPopup.IsOpen = false;
        foreach (var tile in ActiveTiles)
        {
            tile.Stop();
        }
        UpdateUi();
    }

    /// <summary>
    /// Window closing: clips end (finalized) and every stream is released, all tiles in parallel and off the UI thread.
    /// The task ends when they are done.
    /// </summary>
    public Task ShutdownAsync()
    {
        _active = false;
        _tick.Stop();
        _hoverTimer.Stop();
        HideTileBar();
        _dayCts.Cancel();
        CancelMarkLoaders(_ => true);
        // Downloads are not the view's: the main window's DownloadManager stops them (they outlive this view).
        return Task.WhenAll(_tiles.Select(tile => tile.ReleaseAsync()).ToList());
    }

    /// <summary>Top bar: layout picker (up to 16 tiles).</summary>
    public void SelectLayout(GridLayout layout)
    {
        if (ReferenceEquals(layout, _layout))
        {
            return;
        }
        _layout = layout;
        _maximized = null;
        _slots.Resize(layout.TileCount);
        ArrangeTiles();
        SyncTilesToSlots();
        UpdateFocus();
        SaveState();
    }

    /// <summary>
    /// First time the view is shown: the checked cameras of the last session come back on their tiles (in order, gone or
    /// hidden ones left out) and search the day, but nothing plays and the time stays unset until the user picks a day,
    /// presses play, seeks or adds a camera (<see cref="_awaitUser"/>).
    /// </summary>
    void RestoreCameras()
    {
        if (_restoreCameras is not { } cameras)
        {
            return;
        }
        _restoreCameras = null;
        foreach (var camera in cameras.Where(c => FindCamera(c) is { Camera.Hidden: false }).Take(_slots.TileCount))
        {
            _slots.Pick(camera);
        }
        _slots.SetFocus(0);
        _awaitUser = _slots.Count > 0;
        SyncTilesToSlots();
        UpdateFocus();
    }

    /// <summary>Layout and checked cameras are kept for the next start (<see cref="AppSettings.LastPlaybackView"/>).</summary>
    void SaveState()
    {
        App.Settings.LastPlaybackView = PlaybackRestore.Save(_layout, _slots.Slots);
        App.SaveSettings();
    }

    public void ApplyMotionBoxes(bool show)
    {
        foreach (var tile in _tiles)
        {
            tile.ApplyMotionBoxes(show);
        }
    }

    /// <summary>
    /// "Donanım hızlandırma" switched. Only while the view plays (leaving it — e.g. for Ayarlar — stops every tile and
    /// ends the clips, and <see cref="Activate"/> re-seeks all of them with the new decoder anyway): every tile re-seeks
    /// to the shared time like a timeline seek (the clock waits for the focused tile; nothing before that time is
    /// shown). A tile cutting a clip keeps its stream, the others re-seek in step, and it follows when the clip is
    /// stopped. Returns (reopened, kept for a clip).
    /// </summary>
    public (int Reopened, int Kept) ApplyHardwareDecoding()
    {
        var tiles = ActiveTiles.ToList();
        var clipping = tiles.Where(t => t.IsClipping).ToList();
        switch (DecoderSwitch.Playback(_active, Transport, clipping.Count))
        {
            case PlaybackReopen.RestartAll:
                StartAt(Master);
                return (tiles.Count(t => t.SearchDone), 0);
            case PlaybackReopen.ReseekOthers:
                var reopened = 0;
                foreach (var tile in tiles.Where(t => t.SearchDone && !t.IsParked && !t.IsClipping))
                {
                    tile.SeekTo(LeadFor(tile), _speed, _paused);
                    reopened++;
                }
                foreach (var tile in clipping)
                {
                    tile.DecoderStale = true;
                }
                UpdateUi();
                return (reopened, clipping.Count);
            default:
                return (0, 0);
        }
    }

    /// <summary>A clip that held its tile's stream back (decoder switch) was stopped: the tile re-seeks in step now.</summary>
    void ReseekIfDecoderStale(PlaybackTile tile)
    {
        if (tile.DecoderStale && !tile.IsClipping && _active && PlaybackTransport.TilesJoin(Transport) && tile.SearchDone)
        {
            tile.SeekTo(LeadFor(tile), _speed, _paused);
        }
    }

    /// <summary>Devices or their camera lists changed: rebuild the list, drop cameras that no longer exist.</summary>
    public void RefreshCameras()
    {
        _slots.Remove(source => FindCamera(source) is null);
        SyncTilesToSlots();
        foreach (var tile in ActiveTiles)
        {
            tile.RefreshCameraInfo();
        }
        BuildCameraList();
        UpdateFocus();
    }

    /// <summary>Login data of a device changed: its tiles reconnect with the new settings.</summary>
    public void ReloadDevice(Device device)
    {
        CancelMarkLoaders(camera => camera.DeviceId == device.Id); // restarted below with the new address / login
        foreach (var tile in ActiveTiles.Where(t => t.Device!.Id == device.Id).ToList())
        {
            if (device.Cameras.FirstOrDefault(c => c.Channel == tile.Camera!.Channel) is { } camera)
            {
                ShowCamera(tile, device, camera);
            }
        }
        RefreshCameras();
    }

    public void RemoveDevice(Guid deviceId)
    {
        _slots.Remove(source => source.DeviceId == deviceId);
        SyncTilesToSlots();
        BuildCameraList();
        UpdateFocus();
    }

    // ---- Tiles ----

    void ArrangeTiles()
    {
        TileGrid.ColumnDefinitions.Clear();
        TileGrid.RowDefinitions.Clear();
        for (var i = 0; i < _layout.Columns; i++)
        {
            TileGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var i = 0; i < _layout.Rows; i++)
        {
            TileGrid.RowDefinitions.Add(new RowDefinition());
        }
        // From 9 tiles on they are small: 22 px header strip (playback has less room than the live wall).
        var compact = _layout.TileCount >= 9;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            if (i < _layout.TileCount)
            {
                var cell = _layout.Cells[i];
                Grid.SetRow(tile, cell.Row);
                Grid.SetColumn(tile, cell.Column);
                Grid.SetRowSpan(tile, cell.RowSpan);
                Grid.SetColumnSpan(tile, cell.ColumnSpan);
                tile.IsCompact = compact;
                tile.Visibility = Visibility.Visible;
            }
            else
            {
                tile.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Brings every tile to the camera its slot holds; tiles that drop out release their streams.</summary>
    void SyncTilesToSlots()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var want = i < _slots.TileCount ? _slots.Slots[i] : null;
            if (tile.Source == want)
            {
                continue;
            }
            if (want is not null && FindCamera(want) is { } found)
            {
                ShowCamera(tile, found.Device, found.Camera);
            }
            else if (tile.Camera is not null)
            {
                tile.Clear();
            }
        }
        if (_maximized is { Camera: null })
        {
            RestoreMaximized(); // its camera was removed
        }
        UpdateRows();
        if (DownloadPanel.Visibility == Visibility.Visible)
        {
            DownloadPanel.RefreshCameras(PlaybackCameras());
        }
    }

    void ShowCamera(PlaybackTile tile, Device device, Camera camera)
    {
        tile.Show(device, camera);
        _ = LoadAndJoinAsync(tile, _dayCts.Token);
    }

    /// <summary>Searches the tile's recordings of the day, then lets it join the shared playback.</summary>
    async Task LoadAndJoinAsync(PlaybackTile tile, CancellationToken ct)
    {
        if (!await tile.LoadSegmentsAsync(_day, _searches, ct) || ct.IsCancellationRequested)
        {
            return;
        }
        OnSegmentsLoaded(tile);
        if (!_active)
        {
            return;
        }
        if (PlaybackTransport.TilesJoin(Transport))
        {
            tile.SeekTo(LeadFor(tile), _speed, _paused);
        }
        else if (_hasTime)
        {
            tile.Stop(); // playback stopped: the new camera waits for Play like the others
        }
        else
        {
            TryStart();
        }
        UpdateUi();
    }

    /// <summary>
    /// The focused camera's recordings were searched again (focus moved to it): the recorder may have more by now,
    /// or the earlier search failed. Known segments stay when it does not answer.
    /// </summary>
    async Task RefreshSegmentsAsync(PlaybackTile tile, CancellationToken ct)
    {
        if (await tile.LoadSegmentsAsync(_day, _searches, ct, keepOnFailure: true) && !ct.IsCancellationRequested)
        {
            OnSegmentsLoaded(tile);
        }
    }

    /// <summary>A tile's search of the day finished: calendar dot of the day, and the timeline if it is focused.</summary>
    void OnSegmentsLoaded(PlaybackTile tile)
    {
        if (tile.Source is { } source && tile.Segments is { } segments)
        {
            _recordedDays.Set(source, _day, segments.Count > 0);
        }
        LoadRecordedDays();
        if (ReferenceEquals(tile, FocusedTile))
        {
            UpdateTimeline();
            if (tile.Segments is null && tile.SearchError is { } error)
            {
                ShowMessage(PlaybackText.SearchFailed(error)); // otherwise the timeline simply stays without green
                _timelineKey = null; // asked again on the next focus or camera change
            }
        }
    }

    /// <summary>
    /// No shared time yet (new day, first cameras): start at the focused camera's first recording, or the
    /// earliest of any camera once all searches are done.
    /// </summary>
    void TryStart()
    {
        if (_awaitUser && _active && !_hasTime)
        {
            foreach (var tile in ActiveTiles.Where(t => t.SearchDone && !t.IsParked))
            {
                tile.ShowReady(); // restored: waits for a day or play
            }
            return;
        }
        if (_hasTime || !_active || FocusedTile is not { SearchDone: true } focused)
        {
            return;
        }
        if (_startFromMidnight)
        {
            StartFromMidnight(focused);
            return;
        }
        DateTime? start = focused.Segments is { Count: > 0 } own ? own.Min(s => s.Start) : null;
        var tiles = ActiveTiles.ToList();
        if (start is null && tiles.All(t => t.SearchDone))
        {
            start = tiles.Where(t => t.Segments is { Count: > 0 }).SelectMany(t => t.Segments!).Select(s => (DateTime?)s.Start).Min();
            if (start is null && tiles.Any(t => t.Segments is null))
            {
                start = _day; // a search failed: play from midnight, the recorder starts at its first recording
            }
            if (start is null)
            {
                foreach (var tile in tiles)
                {
                    tile.Park(tile.Segments, _day);
                }
                ShowMessage(PlaybackText.NoRecordingsToday);
                return;
            }
        }
        if (start is { } time)
        {
            StartAt(time);
        }
    }

    /// <summary>
    /// A double-clicked day (<see cref="PlayDay"/>): 00:00, or the focused camera's first recording of the day
    /// (<see cref="DayStart"/>). When the focused camera has none, waits until every tile searched the day.
    /// </summary>
    void StartFromMidnight(PlaybackTile focused)
    {
        var tiles = ActiveTiles.ToList();
        var all = tiles.All(t => t.SearchDone) ? tiles.Select(t => t.Segments).ToList() : null;
        if (DayStart.Resolve(_day, focused.Segments, all) is not { } start)
        {
            return;
        }
        if (start.Kind == DayStartKind.NoRecordings)
        {
            _startFromMidnight = false;
            foreach (var tile in tiles)
            {
                tile.Park(tile.Segments, _day);
            }
        }
        else
        {
            StartAt(start.Target);
        }
        if (start.Message is { } message)
        {
            ShowMessage(message); // otherwise a search error shown for the focused camera stays
        }
    }

    /// <summary>Every tile (whose recordings are known) to <paramref name="time"/>; the clock waits for the focused tile.</summary>
    void StartAt(DateTime time)
    {
        SetMaster(time);
        _seekFloor = _anchor; // nothing before the requested time: clock and timeline never go below it
        _hasTime = true;
        _stopped = false;
        _startFromMidnight = false;
        StartHold();
        foreach (var tile in ActiveTiles.Where(t => t.SearchDone))
        {
            tile.SeekTo(time, _speed, _paused);
        }
        UpdateUi();
    }

    /// <summary>
    /// A seek of the user (timeline, ±30 s, "Saate git"). Lands in the focused camera's recordings: a gap jumps
    /// to the next recording (amber notice); after the last recording nothing happens. Returns whether it seeked.
    /// </summary>
    bool SeekAll(DateTime requested)
    {
        if (FocusedTile is not { } focused)
        {
            ShowMessage(PlaybackText.SelectCameraFirst);
            return false;
        }
        _awaitUser = false;
        var target = ClampDay(requested);
        if (focused.Segments is { } segments)
        {
            var jump = RecordingJump.Resolve(segments, target);
            if (jump.Kind is JumpKind.NoneAfter or JumpKind.NoRecordings)
            {
                ShowMessage(jump.Message);
                return false;
            }
            target = jump.Target;
            ShowMessage(jump.Message);
        }
        else
        {
            ShowMessage(null);
        }
        if (!_active)
        {
            _anchor = target;
            _seekFloor = target;
            _hasTime = true;
            _stopped = false;
            UpdateUi();
            return true;
        }
        StartAt(target);
        return true;
    }

    /// <summary>Shared clock tick: follow the focused tile, resume parked tiles, correct drift, refresh the texts.</summary>
    void Tick()
    {
        _tickCount++;
        var focused = FocusedTile;
        if (_holding)
        {
            if (focused is null || focused.State != TileState.Connecting || _holdWatch.Elapsed > HoldLimit)
            {
                _holding = false;
                if (focused is { State: TileState.Playing } && focused.CurrentTime is { } first && PlaybackCatchUp.MayFollow(first, _seekFloor))
                {
                    _anchor = ClampDay(first);
                }
                _clock.Restart();
            }
        }
        else if (_hasTime && !_paused && !_stopped && _tickCount % 4 == 0 && focused is { State: TileState.Playing } &&
                 focused.CurrentTime is { } time && PlaybackCatchUp.MayFollow(time, _seekFloor) &&
                 (time - Master).Duration() > FollowThreshold * Math.Max(1, Rate / 3))
        {
            SetMaster(time); // the focused tile is the reference (e.g. the recorder skipped a gap); never before the request
        }

        if (PlaybackTransport.TilesJoin(Transport) && !_holding)
        {
            var master = Master;
            var tolerance = PlaybackSync.Tolerance(_speed);
            var now = DateTime.UtcNow;
            _sinceDriftCheck += TickInterval;
            var driftCheck = _sinceDriftCheck >= PlaybackSync.CheckInterval;
            if (driftCheck)
            {
                _sinceDriftCheck = TimeSpan.Zero;
            }
            foreach (var tile in ActiveTiles)
            {
                if (!tile.SearchDone || tile.State is TileState.Connecting or TileState.Failed || now - tile.LastSeekUtc < SeekGrace)
                {
                    continue; // still starting, or refused by the recorder (retried on the next seek)
                }
                if (!tile.IsParked && tile.Nudge == SyncNudge.None && (!driftCheck || _paused))
                {
                    continue; // parked and nudged tiles are checked every tick, playing ones every 5 s
                }
                if (tile.IsParked && tile.Segments is null)
                {
                    continue; // recording ended and its recordings are unknown: wait for the next seek
                }
                var tileTime = tile.IsParked ? null : tile.CurrentTime;
                // SDK tiles report the real frame time (PlayM4_GetSystemTime) and are nudged back in step (faster /
                // held) instead of reopened for a few seconds of key-frame offset; the focused tile is the reference.
                var canNudge = tile.CanNudge && !ReferenceEquals(tile, focused);
                if (!canNudge && tile.Nudge != SyncNudge.None)
                {
                    tile.SetNudge(SyncNudge.None); // became the focused tile (the reference), or left the SDK path
                }
                switch (PlaybackSync.Decide(master, tileTime, tile.IsParked, tile.Segments, tolerance, Rate, canNudge, tile.Nudge, tile.SinceNudgeChange))
                {
                    case SyncAction.Seek:
                        tile.SeekTo(LeadFor(tile), _speed, _paused);
                        break;
                    case SyncAction.Park:
                        tile.Park(tile.Segments, master);
                        break;
                    case SyncAction.CatchUp:
                        tile.SetNudge(SyncNudge.CatchUp);
                        break;
                    case SyncAction.Hold:
                        tile.SetNudge(SyncNudge.Hold);
                        break;
                    case SyncAction.Resume:
                        tile.SetNudge(SyncNudge.None);
                        break;
                }
            }
        }
        UpdateClockTexts();
    }

    /// <summary>Where a re-seeked tile should start so it is in step once its first frame arrives.</summary>
    DateTime LeadFor(PlaybackTile tile)
    {
        var master = Master;
        if (_paused || _holding)
        {
            return master;
        }
        var lead = TimeSpan.FromTicks((long)(tile.OpenLatency.Ticks * Rate));
        return ClampDay(master + (lead > MaxLead ? MaxLead : lead));
    }

    void SetMaster(DateTime time)
    {
        _anchor = ClampDay(time);
        _clock.Restart();
    }

    void StartHold()
    {
        _holding = true;
        _holdWatch.Restart();
        _sinceDriftCheck = TimeSpan.Zero;
    }

    DateTime ClampDay(DateTime time)
    {
        var last = _day.AddDays(1).AddSeconds(-1);
        return time < _day ? _day : time > last ? last : time;
    }

    // ---- Focus ----

    void FocusTile(int index)
    {
        if (_slots.SetFocus(index))
        {
            UpdateFocus();
        }
    }

    void UpdateFocus()
    {
        for (var i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].IsFocusedTile = i == _slots.Focus && _tiles[i].Camera is not null;
        }
        UpdateTimeline();
        UpdateRows();
        UpdateUi();
        LoadRecordedDays();
        RefreshFocusedSegments();
        TryStart();
    }

    /// <summary>
    /// Focus moved to another camera (or the day changed): its recordings are asked for again. A tile still
    /// searching delivers them itself; one that searched earlier is searched once more (in the background).
    /// </summary>
    void RefreshFocusedSegments()
    {
        if (FocusedTile is not { Source: { } source } focused || _timelineKey == (source, _day))
        {
            return;
        }
        _timelineKey = (source, _day);
        if (focused.SearchDone)
        {
            _ = RefreshSegmentsAsync(focused, _dayCts.Token);
        }
    }

    /// <summary>Recorded segments of the focused camera, clipped to the day, as green bars on the timeline.</summary>
    void UpdateTimeline()
    {
        Timeline.SetDay(_day, RecordingJump.ClipToDay(FocusedTile?.Segments ?? [], _day));
        Timeline.Position = _hasTime ? Master : null;
    }

    // ---- Sidebar: calendar ----

    void BuildCalendar()
    {
        MonthTitle.Text = CalendarMonth.Title(_month);
        DayGrid.Children.Clear();
        _dayButtons.Clear();
        var blanks = CalendarMonth.LeadingBlanks(_month.Year, _month.Month);
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        // Always six weeks, so the camera list below does not jump between months.
        for (var cell = 0; cell < 42; cell++)
        {
            var dayNumber = cell - blanks + 1;
            if (dayNumber < 1 || dayNumber > days)
            {
                DayGrid.Children.Add(new Border { Height = 30, Margin = new Thickness(1) });
                continue;
            }
            var date = new DateTime(_month.Year, _month.Month, dayNumber);
            var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 2, 0, 0), Fill = Brushes.Transparent };
            var number = new TextBlock { Text = dayNumber.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            var button = new ClickToggleButton
            {
                Style = (Style)FindResource("CalendarDay"),
                Content = new StackPanel { Children = { number, dot } },
                IsEnabled = date <= DateTime.Today,
                ToolTip = PlaybackText.DayToolTip,
            };
            // Name ("21 Ekim 2026[, kayıt var]") is set in UpdateDayButtons.
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, $"Day{dayNumber}");
            System.Windows.Automation.AutomationProperties.SetHelpText(button, "Tıklayın: günü seç. Çift tıklayın ya da Enter'a basın: günün kaydını 00:00'dan oynat");
            // Single click selects the day. A double-click arrives as Click (1st up), MouseDoubleClick (2nd down),
            // Click (2nd up): both Clicks select the same day, so the second one is a no-op in SelectDay and only
            // MouseDoubleClick seeks. Enter (normally a click) does what the double-click does.
            button.Click += (_, _) => SelectDay(date);
            button.MouseDoubleClick += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    PlayDay(date);
                }
            };
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                {
                    PlayDay(date);
                    e.Handled = true;
                }
            };
            _dayButtons.Add((button, dot, date));
            DayGrid.Children.Add(button);
        }
        NextMonthButton.IsEnabled = _month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        LoadRecordedDays();
    }

    /// <summary>Cameras checked in the list (they are in the grid): the calendar dots are theirs.</summary>
    List<CameraRef> CheckedCameras() => _slots.Slots.OfType<CameraRef>().Distinct().ToList();

    /// <summary>
    /// Selected day highlighted; a day carries a green dot when any checked camera recorded on it (white on the
    /// selected day's accent). Days after today never do.
    /// </summary>
    void UpdateDayButtons()
    {
        var marked = _recordedDays.Marked(CheckedCameras(), _month, DateTime.Today);
        foreach (var (button, dot, date) in _dayButtons)
        {
            var selected = date == _day;
            var recorded = marked.Contains(date.Day);
            button.IsChecked = selected;
            if (recorded)
            {
                dot.SetResourceReference(Shape.FillProperty, selected ? "SelectedFg" : "Recorded");
            }
            else
            {
                dot.Fill = Brushes.Transparent;
            }
            var text = date.ToString("d MMMM yyyy", Turkish) + (recorded ? ", kayıt var" : "");
            System.Windows.Automation.AutomationProperties.SetName(button, text);
        }
    }

    /// <summary>
    /// Dots for the shown month: one background search per checked camera that is not running yet (camera
    /// unchecked or month changed → its search stops). Answers are kept, so nothing is asked twice; dots appear
    /// day by day as the recorder answers.
    /// </summary>
    void LoadRecordedDays()
    {
        UpdateDayButtons();
        var cameras = CheckedCameras();
        if (_markMonth != _month)
        {
            CancelMarkLoaders(_ => true);
            _markMonth = _month;
        }
        CancelMarkLoaders(camera => !cameras.Contains(camera));
        foreach (var camera in cameras.Where(c => !_markLoaders.ContainsKey(c)))
        {
            var cts = new CancellationTokenSource();
            _markLoaders[camera] = cts;
            _ = LoadMarksAsync(camera, _month, cts);
        }
    }

    void CancelMarkLoaders(Func<CameraRef, bool> match)
    {
        foreach (var (camera, cts) in _markLoaders.Where(l => match(l.Key)).ToList())
        {
            cts.Cancel();
            _markLoaders.Remove(camera);
        }
    }

    /// <summary>
    /// Calendar dots of one camera: first the recorder's month query (one read-only request for the whole month,
    /// what its own web UI asks); where that is not supported, day by day (newest first) with the read-only
    /// recording search, one result per day. Days another checked camera already dotted are skipped. A failure
    /// leaves the remaining days unknown; they are asked again on the next focus, day or camera change.
    /// </summary>
    async Task LoadMarksAsync(CameraRef source, DateTime month, CancellationTokenSource cts)
    {
        try
        {
            if (FindCamera(source) is not { } found)
            {
                return;
            }
            // Same timeout as the playback day's search: the recorder serves searches one by one, so a day search
            // can wait behind them.
            using var isapi = new IsapiClient(found.Device, TimeSpan.FromSeconds(20));
            if (await LoadMonthMarksAsync(isapi, source, found.Camera.Channel, month, cts.Token))
            {
                return;
            }
            var failures = 0;
            while (!cts.IsCancellationRequested)
            {
                DateTime? next = null;
                bool? recorded = null;
                string? error = null;
                await _markSearches.WaitAsync(cts.Token);
                try
                {
                    // Chosen after the gate: another camera may have dotted the day while this one waited.
                    next = _recordedDays.NextToSearch(source, CheckedCameras(), month, DateTime.Today);
                    if (next is { } ask)
                    {
                        recorded = await isapi.HasRecordingsAsync(found.Camera.Channel, ask, ask.AddDays(1).AddSeconds(-1), cts.Token);
                    }
                }
                catch (Exception e) when (e is IsapiException or System.Net.Http.HttpRequestException or System.IO.IOException)
                {
                    error = e.Message; // (anything else used to end the loop silently)
                }
                finally
                {
                    _markSearches.Release();
                }
                if (next is not { } day)
                {
                    return; // every day is known
                }
                if (recorded is { } answer)
                {
                    failures = 0;
                    _recordedDays.Set(source, day, answer);
                    if (!cts.IsCancellationRequested)
                    {
                        UpdateDayButtons();
                    }
                    continue;
                }
                // A failure is never stored as "no recording": the same day is asked again a little later. (Before,
                // the first failure ended the loop silently and the remaining days never got their dot.)
                if (RecordedDays.RetryDelay(++failures) is not { } delay)
                {
                    Message?.Invoke(PlaybackText.CalendarSearchFailed(found.Camera.Name, error ?? ""));
                    return;
                }
                await Task.Delay(delay, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IsapiException or UriFormatException)
        {
            // No dots for the days left; the playback day's own search still works (and shows its error).
        }
        finally
        {
            if (_markLoaders.TryGetValue(source, out var current) && current == cts)
            {
                _markLoaders.Remove(source);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// The month query (<see cref="IsapiClient.GetRecordedDaysAsync"/>) for one camera. True when the month is
    /// answered (or nothing is left to ask); false = ask day by day: not supported (403/404, no day list) or
    /// still failing after the retries (the day searches then report the error).
    /// </summary>
    async Task<bool> LoadMonthMarksAsync(IsapiClient isapi, CameraRef source, int channel, DateTime month, CancellationToken ct)
    {
        var failures = 0;
        while (_recordedDays.NextToSearch(source, CheckedCameras(), month, DateTime.Today) is not null)
        {
            IReadOnlySet<int>? days = null;
            var failed = false;
            await _markSearches.WaitAsync(ct);
            try
            {
                days = await isapi.GetRecordedDaysAsync(channel, month, ct);
            }
            catch (Exception e) when (e is IsapiException or System.Net.Http.HttpRequestException or System.IO.IOException)
            {
                failed = true;
            }
            finally
            {
                _markSearches.Release();
            }
            if (!failed)
            {
                if (days is null)
                {
                    return false;
                }
                _recordedDays.SetMonth(source, month, days, DateTime.Today);
                if (!ct.IsCancellationRequested)
                {
                    UpdateDayButtons();
                }
                return true;
            }
            if (RecordedDays.RetryDelay(++failures) is not { } delay)
            {
                return false;
            }
            await Task.Delay(delay, ct);
        }
        return true;
    }

    void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        BuildCalendar();
    }

    void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        BuildCalendar();
    }

    /// <summary>New day: every tile searches its recordings again and playback restarts at the first one.</summary>
    void SelectDay(DateTime date)
    {
        if (date == _day)
        {
            UpdateDayButtons();
            if (_awaitUser)
            {
                _awaitUser = false; // restored cameras: picking the shown day starts it
                TryStart();
            }
            return;
        }
        _awaitUser = false;
        _day = date.Date;
        _hasTime = false;
        _stopped = false;
        _startFromMidnight = false;
        _anchor = _day;
        _seekFloor = _day;
        _holding = false;
        ShowMessage(null);
        _dayCts.Cancel();
        _dayCts = new CancellationTokenSource();
        foreach (var tile in ActiveTiles.ToList())
        {
            ShowCamera(tile, tile.Device!, tile.Camera!);
        }
        UpdateDayButtons();
        UpdateFocus();
    }

    /// <summary>
    /// Double-click (or Enter) on a day: that day on every tile from 00:00 at the current speed, playing even if
    /// paused. The focused camera without a recording at 00:00 starts at its first recording of the day instead
    /// (amber notice); the other tiles follow the shared time (parked where they have none). Days after today: nothing.
    /// </summary>
    void PlayDay(DateTime date)
    {
        if (date.Date > DateTime.Today)
        {
            return;
        }
        SelectDay(date); // the first click of a double-click already did: no new search
        if (FocusedTile is null)
        {
            ShowMessage(PlaybackText.SelectCameraForDay);
            return;
        }
        _hasTime = false;
        _stopped = false;
        _holding = false;
        _anchor = _day;
        _seekFloor = _day;
        _paused = false; // the tiles get it with the seek
        _startFromMidnight = true;
        TryStart(); // now if the searches of the day are done, otherwise when they finish
        UpdateUi();
    }

    // ---- Sidebar: cameras ----

    void BuildCameraList()
    {
        CameraList.Children.Clear();
        _rows.Clear();
        var devices = App.Settings.Devices;
        if (devices.Count == 0)
        {
            CameraList.Children.Add(new TextBlock
            {
                Text = "Henüz cihaz yok. Sağ üstteki Ayarlar düğmesiyle ekleyin.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = (double)FindResource("FontSizeTile"),
                Margin = new Thickness(10),
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        }
        foreach (var device in devices)
        {
            if (devices.Count > 1)
            {
                CameraList.Children.Add(new TextBlock
                {
                    Text = device.Name,
                    Style = (Style)FindResource("CaptionText"),
                    Margin = new Thickness(10, 8, 10, 4),
                });
            }
            // Cameras switched off under Settings > "Listede göster" are left out, like in the live list.
            foreach (var camera in device.ListedCameras())
            {
                CameraList.Children.Add(CameraRowFor(device, camera));
            }
        }
        UpdateRows();
    }

    ToggleButton CameraRowFor(Device device, Camera camera)
    {
        var mark = new Path
        {
            Data = (Geometry)FindResource("IconCheck"),
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 8,
            Height = 8,
            Stretch = Stretch.Uniform,
        }.WithBrush(Shape.StrokeProperty, "OnAccent");
        var box = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.5),
            Child = mark,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = camera.Name,
            Margin = new Thickness(8, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var number = new TextBlock
        {
            FontSize = (double)FindResource("FontSizeSmall"),
            VerticalAlignment = VerticalAlignment.Center,
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        grid.Children.Add(box);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(number, 2);
        grid.Children.Add(number);

        var source = new CameraRef(device.Id, camera.Channel);
        var button = new ClickToggleButton
        {
            Style = (Style)FindResource("CameraCheckRow"),
            Content = grid,
            Opacity = camera.Online ? 1 : 0.6,
            ToolTip = camera.Online ? $"{camera.Name} · Kanal {camera.Channel}" : $"{camera.Name} · Kanal {camera.Channel} — çevrimdışı",
        };
        button.Click += (_, _) => PickCamera(source);
        _rows.Add(new CameraRow(button, box, mark, number, camera, source));
        return button;
    }

    /// <summary>In the grid → removed; otherwise into the first empty tile; grid full → replaces the focused tile's camera.</summary>
    void PickCamera(CameraRef source)
    {
        if (_slots.IndexOf(source) < 0)
        {
            _awaitUser = false; // adding a camera starts playback as before (only a restore waits)
        }
        _slots.Pick(source);
        SyncTilesToSlots();
        UpdateFocus();
        SaveState();
    }

    // ---- Drag and drop (like the live wall) ----

    Point? _listDragOrigin;
    bool _dragging; // a camera or a tile is being dragged: drop targets are highlighted, the hover bar stays away

    void CameraList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _listDragOrigin = e.GetPosition(CameraList);

    /// <summary>
    /// A camera row dragged past the system threshold (a click stays a click: <see cref="PickCamera"/>) carries its camera
    /// to a tile, with the live list's data format and effect.
    /// </summary>
    void CameraList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _listDragOrigin = null; // a button pressed elsewhere and dragged in starts nothing
            return;
        }
        if (_listDragOrigin is not { } origin)
        {
            return;
        }
        var delta = e.GetPosition(CameraList) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _listDragOrigin = null;
        if (RowOf(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }
        SetDragging(true);
        try
        {
            DragDrop.DoDragDrop(row.Button, new DataObject(VideoTile.DragFormat, row.Source), DragDropEffects.Copy);
        }
        finally
        {
            SetDragging(false);
            UpdateRows(); // the pressed row was never clicked: its check mark stays what the slots say
        }
    }

    CameraRow? RowOf(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ToggleButton button && _rows.FirstOrDefault(r => ReferenceEquals(r.Button, button)) is { } row)
            {
                return row;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    void SetDragging(bool on)
    {
        _dragging = on;
        foreach (var tile in _tiles)
        {
            tile.DragActive = on;
        }
        if (on)
        {
            HideTileBar(); // the bar is a separate window: it would be in the way of a drop on the tile's lower edge
        }
    }

    /// <summary>
    /// A camera of the list dropped onto a tile: in another tile already → the two tiles trade places (their streams keep
    /// running: same moment, clip and zoom go along); otherwise it fills the tile or replaces its camera, opens there and
    /// joins the shared time like a clicked camera (<see cref="LoadAndJoinAsync"/>: <see cref="LeadFor"/>, speed, pause,
    /// Stop). The tile takes the focus.
    /// </summary>
    void DropCamera(PlaybackTile tile, CameraRef source)
    {
        var index = _tiles.IndexOf(tile);
        if (index < 0 || index >= _layout.TileCount || FindCamera(source) is null)
        {
            return;
        }
        var drop = _slots.Place(index, source);
        if (drop.Kind == SlotDropKind.Swapped)
        {
            SwapTiles(drop.Tile, drop.Other);
        }
        else if (drop.Kind == SlotDropKind.Placed)
        {
            _awaitUser = false; // adding a camera starts playback as before (only a restore waits)
        }
        SyncTilesToSlots();
        UpdateFocus();
        SaveState();
    }

    /// <summary>A tile dragged onto another: the two trade places (onto an empty tile = a move), nothing reopens.</summary>
    void DropTile(PlaybackTile from, PlaybackTile to)
    {
        var a = _tiles.IndexOf(from);
        var b = _tiles.IndexOf(to);
        if (a < 0 || b < 0 || a >= _layout.TileCount || b >= _layout.TileCount || from.Source is null || !_slots.Swap(a, b))
        {
            return;
        }
        SwapTiles(a, b);
        SyncTilesToSlots(); // nothing to reopen: rows, numbers and the download panel's order follow
        UpdateFocus();
        SaveState();
    }

    /// <summary>
    /// The tile objects trade cells, so each keeps its camera, stream, time, clip and zoom (the tiles' order is the slots'
    /// order). A maximized cell stays maximized and shows what its slot holds now.
    /// </summary>
    void SwapTiles(int a, int b)
    {
        var maximized = _maximized is null ? -1 : _tiles.IndexOf(_maximized);
        RestoreMaximized();
        (_tiles[a], _tiles[b]) = (_tiles[b], _tiles[a]);
        ArrangeTiles();
        HideTileBar(); // placed again on the next hover tick
        if (maximized >= 0 && _tiles[maximized].Camera is not null)
        {
            ToggleMaximize(_tiles[maximized]);
        }
    }

    /// <summary>Check box, tile number and focus highlight of every camera row.</summary>
    void UpdateRows()
    {
        var focused = _slots.Focused;
        foreach (var row in _rows)
        {
            var index = _slots.IndexOf(row.Source);
            var on = index >= 0;
            row.Button.IsChecked = on;
            row.Box.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "InputBorder");
            if (on)
            {
                row.Box.SetResourceReference(Border.BackgroundProperty, "Accent");
            }
            else
            {
                row.Box.Background = Brushes.Transparent;
            }
            row.Mark.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            row.Number.Text = on ? (index + 1).ToString(CultureInfo.InvariantCulture) : "";
            if (on && row.Source == focused)
            {
                row.Button.SetResourceReference(Control.BackgroundProperty, "ListSelectedBg");
            }
            else
            {
                row.Button.Background = Brushes.Transparent;
            }
            System.Windows.Automation.AutomationProperties.SetName(row.Button, on ? $"{row.Camera.Name}, kare {index + 1}" : row.Camera.Name);
        }
        SelectionCount.Text = $"{_slots.Count} / {_layout.TileCount}";
    }

    (Device Device, Camera Camera)? FindCamera(CameraRef source)
    {
        var device = App.Settings.Devices.FirstOrDefault(d => d.Id == source.DeviceId);
        var camera = device?.Cameras.FirstOrDefault(c => c.Channel == source.Channel);
        return device is null || camera is null ? null : (device, camera);
    }

    // ---- Controls ----

    void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        switch (PlaybackTransport.PlayPause(Transport))
        {
            case PlayPauseAction.Start:
                _awaitUser = false;
                TryStart();
                if (!_hasTime)
                {
                    ShowMessage(FocusedTile is null ? PlaybackText.SelectCameraFirst : null);
                }
                return;
            case PlayPauseAction.Restart:
                // After Stop: every tile reopens at the kept time (hidden until it is reached, like any seek).
                _paused = false;
                if (!ActiveTiles.Any())
                {
                    ShowMessage(PlaybackText.SelectCameraFirst);
                    return;
                }
                StartAt(_anchor);
                return;
        }
        _anchor = Master;
        _clock.Restart();
        _paused = !_paused;
        foreach (var tile in ActiveTiles)
        {
            tile.SetPaused(_paused);
        }
        UpdateUi();
    }

    /// <summary>
    /// Stop: every tile closes its stream (in its SDK queue, off the UI thread) and finalizes a running clip ("Klip
    /// kaydedildi" follows), keeps its camera and waits; the clock stops where it is, so Play starts there again.
    /// </summary>
    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!PlaybackTransport.CanStop(Transport, ActiveTiles.Any()))
        {
            return;
        }
        _anchor = Master;
        _clock.Restart();
        _holding = false;
        _stopped = true;
        foreach (var tile in ActiveTiles)
        {
            tile.Stop();
        }
        UpdateUi();
    }

    async void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (FocusedTile is not { } tile)
        {
            Message?.Invoke("Önce listeden kamera seçin.");
            return;
        }
        var path = await tile.TakeSnapshotAsync();
        Message?.Invoke(path is null ? "Anlık görüntü alınamadı." : $"Kaydedildi: {path}");
    }

    void Clip_Click(object sender, RoutedEventArgs e)
    {
        if (FocusedTile is not { } tile)
        {
            Message?.Invoke("Önce listeden kamera seçin.");
            return;
        }
        Message?.Invoke(tile.ToggleClip());
        ReseekIfDecoderStale(tile);
        UpdateUi();
    }

    void Back_Click(object sender, RoutedEventArgs e) => SeekBy(-SeekStep);

    void Forward_Click(object sender, RoutedEventArgs e) => SeekBy(SeekStep);

    void SeekBy(TimeSpan delta)
    {
        if (_hasTime)
        {
            SeekAll(Master + delta);
        }
        else
        {
            ShowMessage(FocusedTile is null ? PlaybackText.SelectCameraFirst : PlaybackText.NoRecordingsToday);
        }
    }

    void Slower_Click(object sender, RoutedEventArgs e) => SetSpeed(PlaybackSpeed.Slower(_speed));

    void Faster_Click(object sender, RoutedEventArgs e) => SetSpeed(PlaybackSpeed.Faster(_speed));

    /// <summary>New speed: the clock continues from the current time and every tile gets the command again.</summary>
    void SetSpeed(int speed)
    {
        if (speed == _speed)
        {
            return;
        }
        _anchor = Master;
        _clock.Restart();
        _speed = speed;
        _sinceDriftCheck = TimeSpan.Zero;
        foreach (var tile in ActiveTiles)
        {
            tile.SetSpeed(speed);
        }
        UpdateUi();
    }

    void ZoomOut_Click(object sender, RoutedEventArgs e) => Timeline.ZoomOut();

    void ZoomIn_Click(object sender, RoutedEventArgs e) => Timeline.ZoomIn();

    void UpdateZoomButtons()
    {
        ZoomOutButton.IsEnabled = Timeline.CanZoomOut;
        ZoomInButton.IsEnabled = Timeline.CanZoomIn;
        var span = Timeline.ViewSpan;
        var text = span >= TimeSpan.FromHours(1)
            ? $"{span.TotalHours.ToString("0.#", Turkish)} saat"
            : $"{span.TotalMinutes.ToString("0", Turkish)} dakika";
        ZoomOutButton.ToolTip = $"Uzaklaştır (şu an {text})";
        ZoomInButton.ToolTip = $"Yakınlaştır (şu an {text})";
    }

    // ---- "Saate git" ----

    void OpenGoTo()
    {
        var time = _hasTime ? Master : _day;
        HourBox.Text = time.ToString("HH", CultureInfo.InvariantCulture);
        MinuteBox.Text = time.ToString("mm", CultureInfo.InvariantCulture);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            HourBox.Focus();
            HourBox.SelectAll();
        });
    }

    void GoTo_Click(object sender, RoutedEventArgs e) => GoTo();

    void GoToCancel_Click(object sender, RoutedEventArgs e)
    {
        GoToPopup.IsOpen = false;
        TimeButton.Focus();
    }

    void GoTo()
    {
        if (PlaybackText.ParseTimeOfDay(HourBox.Text, MinuteBox.Text) is not { } time)
        {
            ShowMessage(PlaybackText.InvalidTime);
            return;
        }
        if (SeekAll(_day + time))
        {
            GoToPopup.IsOpen = false;
            TimeButton.Focus();
        }
    }

    void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoTo();
            e.Handled = true;
        }
    }

    void TimeBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    void Digits_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    // ---- Maximize (double-click), like the live view ----

    PlaybackTile? _maximized;

    /// <summary>
    /// Double-click: the tile fills the grid; again (or Esc) restores it. The other tiles stay in the grid, only hidden: their
    /// streams and video windows keep running and keep in step (the sync loop does not look at visibility), so the grid is
    /// back at once. Recordings always play from the main stream, so nothing switches quality.
    /// </summary>
    void ToggleMaximize(PlaybackTile tile)
    {
        if (_maximized is not null)
        {
            RestoreMaximized();
            return;
        }
        if (tile.Camera is null || _tiles.IndexOf(tile) >= _layout.TileCount)
        {
            return;
        }
        _maximized = tile;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var other = _tiles[i];
            other.Visibility = other == tile ? Visibility.Visible
                : i < _layout.TileCount ? Visibility.Hidden // hides its video window too: nothing covers the big one
                : Visibility.Collapsed;
        }
        tile.IsCompact = false;
        Grid.SetRow(tile, 0);
        Grid.SetColumn(tile, 0);
        Grid.SetRowSpan(tile, _layout.Rows);
        Grid.SetColumnSpan(tile, _layout.Columns);
        HideTileBar(); // placed again on the next hover tick
    }

    void RestoreMaximized()
    {
        if (_maximized is null)
        {
            return;
        }
        _maximized = null;
        ArrangeTiles();
        HideTileBar();
    }

    Window? _window;

    /// <summary>Esc restores a maximized tile; moving or minimizing the window hides the hover bar until the next tick.</summary>
    void HookWindow()
    {
        if (Window.GetWindow(this) is not { } window || ReferenceEquals(window, _window))
        {
            return;
        }
        _window = window;
        window.AddHandler(KeyDownEvent, new KeyEventHandler(Window_KeyDown));
        window.LocationChanged += (_, _) => HideTileBar();
        window.StateChanged += (_, _) => HideTileBar();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _maximized is not null && _active && IsVisible && Keyboard.Modifiers == ModifierKeys.None)
        {
            RestoreMaximized();
            e.Handled = true;
        }
    }

    // ---- Hover tool bar ----

    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    PlaybackTile? _barTile;
    NativePoint _lastCursor = new() { X = int.MinValue, Y = int.MinValue }; // cursor at the last full hover check
    DateTime _barHovered;
    (Point Origin, Size Size) _barPlace;

    /// <summary>
    /// Every 100 ms while the view is shown: the cursor (GetCursorPos — the video HWNDs take the mouse messages) over a
    /// tile's video area shows the bar there; it lingers <see cref="Core.TileToolBar.HideDelay"/> after the mouse left. It
    /// hides at once when the window is minimized or not in the foreground (another app on top), the view is hidden or a
    /// drag is in progress.
    /// </summary>
    void HoverTick()
    {
        if (!_active || _dragging || !IsVisible || _window is not { WindowState: not WindowState.Minimized } window || !IsForeground(window))
        {
            HideTileBar();
            return;
        }
        // Idle poll: no bar and the cursor where it was → nothing can have changed for the bar (no hit tests, no
        // allocations). A bar that is shown keeps being checked (it lingers, follows layout changes and tile state).
        if (GetCursorPos(out var cursor))
        {
            if (_barTile is null && cursor.X == _lastCursor.X && cursor.Y == _lastCursor.Y)
            {
                return;
            }
            _lastCursor = cursor;
        }
        var hovered = HoveredTile(window);
        var now = DateTime.UtcNow;
        if (hovered is not null)
        {
            _barHovered = now;
        }
        var shown = _barTile is null ? (int?)null : _tiles.IndexOf(_barTile);
        var owner = Core.TileToolBar.Owner(hovered is null ? null : _tiles.IndexOf(hovered), shown, _barHovered, now);
        if (owner is not { } index || _tiles[index] is not { Camera: not null, IsVisible: true } tile)
        {
            HideTileBar();
            return;
        }
        ShowTileBar(tile);
    }

    PlaybackTile? HoveredTile(Window window)
    {
        if (!GetCursorPos(out var cursor))
        {
            return null;
        }
        var screen = new Point(cursor.X, cursor.Y);
        if (TileBarPopup.IsOpen && _barTile is not null && Contains(TileBar, screen))
        {
            return _barTile; // over the bar itself
        }
        // Only when our window is the one under the cursor (not another app's window on top of a tile).
        var root = GetAncestor(WindowFromPoint(cursor), GaRoot);
        if (root != new System.Windows.Interop.WindowInteropHelper(window).Handle && root != PopupHandle())
        {
            return null;
        }
        return _tiles.Take(_layout.TileCount).FirstOrDefault(t => t.Camera is not null && t.IsVisible && Contains(t.VideoSurface, screen));
    }

    static bool Contains(FrameworkElement element, Point screen)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || PresentationSource.FromVisual(element) is null)
        {
            return false;
        }
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return Core.TileToolBar.Contains((a.X, a.Y, b.X - a.X, b.Y - a.Y), screen.X, screen.Y);
    }

    void ShowTileBar(PlaybackTile tile)
    {
        var surface = tile.VideoSurface;
        if (Core.TileToolBar.Place(0, 0, surface.ActualWidth, surface.ActualHeight, 1, 4) is not { } place)
        {
            HideTileBar(); // too small for the buttons
            return;
        }
        TileBar.Update(tile, DownloadPanel.Visibility == Visibility.Visible && DownloadPanel.Target == tile.Source);
        TileBar.Width = place.Width;
        var where = (surface.PointToScreen(new Point(0, 0)), new Size(surface.ActualWidth, surface.ActualHeight));
        if (!ReferenceEquals(TileBarPopup.PlacementTarget, surface))
        {
            TileBarPopup.PlacementTarget = surface;
        }
        TileBarPopup.HorizontalOffset = place.X;
        TileBarPopup.VerticalOffset = place.Y;
        _barTile = tile;
        if (!TileBarPopup.IsOpen)
        {
            TileBarPopup.IsOpen = true;
        }
        else if (where != _barPlace)
        {
            // A Popup does not follow its target: nudge it so WPF places it again (layout change, panel, maximize).
            TileBarPopup.HorizontalOffset = place.X + 1;
            TileBarPopup.HorizontalOffset = place.X;
        }
        _barPlace = where;
    }

    void HideTileBar()
    {
        if (TileBarPopup.IsOpen)
        {
            TileBarPopup.IsOpen = false;
        }
        _barTile = null;
        _barPlace = default;
    }

    IntPtr PopupHandle() =>
        TileBarPopup.IsOpen && PresentationSource.FromVisual(TileBar) is System.Windows.Interop.HwndSource source ? source.Handle : IntPtr.Zero;

    bool IsForeground(Window window)
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero &&
            (foreground == new System.Windows.Interop.WindowInteropHelper(window).Handle || foreground == PopupHandle());
    }

    const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // ---- Tool bar actions (on the bar's tile) ----

    async Task SnapshotAsync(PlaybackTile tile)
    {
        var path = await tile.TakeSnapshotAsync();
        Message?.Invoke(path is null ? "Anlık görüntü alınamadı." : $"Kaydedildi: {path}");
    }

    void ToggleClip(PlaybackTile tile)
    {
        Message?.Invoke(tile.ToggleClip());
        ReseekIfDecoderStale(tile);
        UpdateUi();
    }

    // ---- "Video indir" panel ----

    /// <summary>The app's downloads (main window): the panel starts a batch there and closes; it outlives this view.</summary>
    internal DownloadManager? Downloads
    {
        get => DownloadPanel.Downloads;
        set => DownloadPanel.Downloads = value;
    }

    /// <summary>Cameras of the playback tiles, in tile order (the panel's list).</summary>
    List<(Device Device, Camera Camera)> PlaybackCameras() => ActiveTiles.Select(t => (t.Device!, t.Camera!)).ToList();

    void OpenDownload(PlaybackTile tile)
    {
        var time = tile.CurrentTime ?? (_hasTime ? Master : null);
        DownloadPanel.Open(PlaybackCameras(), tile.Source, time, _day);
        DownloadPanel.Visibility = Visibility.Visible;
        HideTileBar();
    }

    void CloseDownload()
    {
        DownloadPanel.Visibility = Visibility.Collapsed; // a running download goes on (the top bar's pill shows it)
        HideTileBar();
    }

    // ---- Texts ----

    /// <summary>Amber notice in the info bar (replaces state and speed); null clears it.</summary>
    void ShowMessage(string? text)
    {
        _message = text;
        _messageTimer.Stop();
        if (text is not null)
        {
            _messageTimer.Start();
        }
        UpdateUi();
    }

    void UpdateUi()
    {
        DateText.Text = _day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var transport = Transport;
        var playing = PlaybackTransport.ShowsPause(transport);
        PlayStateText.Text = transport == TransportState.Stopped ? "Durduruldu" : playing ? "Oynatılıyor" : "Duraklatıldı";
        StopButton.IsEnabled = PlaybackTransport.CanStop(transport, ActiveTiles.Any());
        SpeedBadge.Text = PlaybackSpeed.Label(_speed);
        SpeedText.Text = PlaybackSpeed.Label(_speed);
        MessageText.Text = _message ?? "";
        MessagePanel.ToolTip = _message;
        MessagePanel.Visibility = _message is null ? Visibility.Collapsed : Visibility.Visible;
        StatePanel.Visibility = _message is null ? Visibility.Visible : Visibility.Collapsed;
        FocusName.Text = FocusedTile?.Camera?.Name ?? "—";
        FocusName.ToolTip = FocusedTile?.Camera?.Name;

        PlayPauseIcon.FillData = (Geometry)FindResource(playing ? "IconPause" : "IconPlay");
        PlayPauseButton.ToolTip = playing ? "Duraklat" : "Oynat";
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, playing ? "Duraklat" : "Oynat");

        var clipping = FocusedTile?.IsClipping == true;
        ClipIcon.Data = (Geometry)FindResource(clipping ? "IconStopSquare" : "IconScissors");
        ClipIcon.FillData = clipping ? (Geometry)FindResource("IconStopSquare") : null;
        ClipIcon.SetResourceReference(GlyphIcon.ForegroundProperty, clipping ? "DangerIcon" : "IconFg");
        ClipButton.ToolTip = clipping ? "Klibi bitir" : "Klip al (seçili kare, MP4)";
        System.Windows.Automation.AutomationProperties.SetName(ClipButton, clipping ? "Klibi bitir" : "Seçili kareden klip al");

        SlowerButton.IsEnabled = PlaybackSpeed.CanSlowDown(_speed);
        FasterButton.IsEnabled = PlaybackSpeed.CanSpeedUp(_speed);
        UpdateClockTexts();
        RaiseStateChanged();
    }

    void UpdateClockTexts()
    {
        var text = _hasTime ? Master.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--:--:--";
        TimeText.Text = text;
        TimeButtonText.Text = text;
        Timeline.Position = _hasTime ? Master : null;
    }

    void RaiseStateChanged()
    {
        if (_stateChangePending)
        {
            return;
        }
        _stateChangePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _stateChangePending = false;
            StateChanged?.Invoke();
        });
    }
}
