using System.Diagnostics;
using System.Globalization;
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
    readonly Dictionary<(Guid Device, int Channel, DateTime Month), HashSet<int>> _recordedDays = [];

    GridLayout _layout = GridLayout.PlaybackDefault;
    PlaybackSlots _slots = new(GridLayout.PlaybackDefault.TileCount);
    DateTime _day = DateTime.Today;
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime _anchor = DateTime.Today; // master time when _clock was (re)started
    bool _hasTime;                     // a shared time is set (first recording of the day, a seek…)
    bool _holding;                     // after a seek: the clock waits for the focused tile's first frame
    bool _paused;
    bool _active;                      // the view is shown and playing
    int _speed = PlaybackSpeed.Normal;
    int _tickCount;
    TimeSpan _sinceDriftCheck;
    string? _message;
    bool _stateChangePending;
    CancellationTokenSource _dayCts = new();
    CancellationTokenSource? _marksCts;

    public PlaybackView()
    {
        InitializeComponent();
        for (var i = 0; i < GridLayout.PlaybackMaxTiles; i++)
        {
            var tile = new PlaybackTile { Visibility = Visibility.Collapsed };
            tile.Activated += t => FocusTile(_tiles.IndexOf(t));
            tile.StateChanged += _ => UpdateUi(); // header, clip button, status bar
            tile.Notice += (_, text) => Message?.Invoke(text);
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

    /// <summary>The shared playback time: advances with the speed unless paused or waiting for the first frame.</summary>
    DateTime Master => _holding || _paused || !_hasTime || !_active ? _anchor : ClampDay(_anchor + _clock.Elapsed * Rate);

    // ---- Main window ----

    /// <summary>The view is shown: resume where it was left (all tiles re-seek to the shared time).</summary>
    public void Activate()
    {
        _active = true;
        _tick.Start();
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
        GoToPopup.IsOpen = false;
        foreach (var tile in ActiveTiles)
        {
            tile.Stop();
        }
        UpdateUi();
    }

    /// <summary>Window closing: every stream is released right away.</summary>
    public void Shutdown()
    {
        _active = false;
        _tick.Stop();
        _dayCts.Cancel();
        _marksCts?.Cancel();
        foreach (var tile in _tiles)
        {
            tile.Release();
        }
    }

    /// <summary>Top bar: layout picker (up to 16 tiles).</summary>
    public void SelectLayout(GridLayout layout)
    {
        if (ReferenceEquals(layout, _layout))
        {
            return;
        }
        _layout = layout;
        _slots.Resize(layout.TileCount);
        ArrangeTiles();
        SyncTilesToSlots();
        UpdateFocus();
    }

    public void ApplyMotionBoxes(bool show)
    {
        foreach (var tile in _tiles)
        {
            tile.ApplyMotionBoxes(show);
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
        UpdateRows();
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
        if (ReferenceEquals(tile, FocusedTile))
        {
            UpdateTimeline();
            _ = LoadRecordedDaysAsync();
        }
        if (!_active)
        {
            return;
        }
        if (_hasTime)
        {
            tile.SeekTo(LeadFor(tile), _speed, _paused);
        }
        else
        {
            TryStart();
        }
        UpdateUi();
    }

    /// <summary>
    /// No shared time yet (new day, first cameras): start at the focused camera's first recording, or the
    /// earliest of any camera once all searches are done.
    /// </summary>
    void TryStart()
    {
        if (_hasTime || !_active || FocusedTile is not { SearchDone: true } focused)
        {
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

    /// <summary>Every tile (whose recordings are known) to <paramref name="time"/>; the clock waits for the focused tile.</summary>
    void StartAt(DateTime time)
    {
        SetMaster(time);
        _hasTime = true;
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
            _hasTime = true;
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
                if (focused is { State: TileState.Playing } && focused.CurrentTime is { } first)
                {
                    _anchor = ClampDay(first);
                }
                _clock.Restart();
            }
        }
        else if (_hasTime && !_paused && _tickCount % 4 == 0 && focused is { State: TileState.Playing } &&
                 focused.CurrentTime is { } time && (time - Master).Duration() > FollowThreshold * Math.Max(1, Rate / 3))
        {
            SetMaster(time); // the focused tile is the reference (e.g. the recorder skipped a gap)
        }

        if (_hasTime && !_holding)
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
                if (!tile.IsParked && (!driftCheck || _paused))
                {
                    continue; // parked tiles are checked every tick, playing ones every 5 s
                }
                if (tile.IsParked && tile.Segments is null)
                {
                    continue; // recording ended and its recordings are unknown: wait for the next seek
                }
                var tileTime = tile.IsParked ? null : tile.CurrentTime;
                switch (PlaybackSync.Decide(master, tileTime, tile.IsParked, tile.Segments, tolerance))
                {
                    case SyncAction.Seek:
                        tile.SeekTo(LeadFor(tile), _speed, _paused);
                        break;
                    case SyncAction.Park:
                        tile.Park(tile.Segments, master);
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
        _ = LoadRecordedDaysAsync();
        TryStart();
    }

    void UpdateTimeline()
    {
        Timeline.SetDay(_day, FocusedTile?.Segments ?? []);
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
            var button = new ToggleButton
            {
                Style = (Style)FindResource("CalendarDay"),
                Content = new StackPanel { Children = { number, dot } },
                IsEnabled = date <= DateTime.Today,
            };
            button.Click += (_, _) => SelectDay(date);
            _dayButtons.Add((button, dot, date));
            DayGrid.Children.Add(button);
        }
        NextMonthButton.IsEnabled = _month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        UpdateDayButtons();
        _ = LoadRecordedDaysAsync();
    }

    /// <summary>Selected day highlighted; days with recordings of the focused camera carry a dot.</summary>
    void UpdateDayButtons()
    {
        var marks = FocusedTile is { Device: { } device, Camera: { } camera } &&
                    _recordedDays.TryGetValue((device.Id, camera.Channel, _month), out var days) ? days : null;
        foreach (var (button, dot, date) in _dayButtons)
        {
            var selected = date == _day;
            var recorded = marks?.Contains(date.Day) == true ||
                           (selected && FocusedTile?.Segments is { Count: > 0 });
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

    /// <summary>Asks the recorder, day by day, whether the focused camera recorded anything (one result per day).</summary>
    async Task LoadRecordedDaysAsync()
    {
        _marksCts?.Cancel();
        UpdateDayButtons();
        if (FocusedTile is not { Device: { } device, Camera: { } camera })
        {
            return;
        }
        var key = (device.Id, camera.Channel, _month);
        if (_recordedDays.ContainsKey(key))
        {
            return;
        }
        var cts = _marksCts = new CancellationTokenSource();
        var days = new HashSet<int>();
        try
        {
            using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(10));
            for (var date = _month; date.Month == _month.Month && date <= DateTime.Today; date = date.AddDays(1))
            {
                if (await isapi.HasRecordingsAsync(camera.Channel, date, date.AddDays(1).AddSeconds(-1), cts.Token))
                {
                    days.Add(date.Day);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IsapiException)
        {
            return; // no marks; the day search itself still works
        }
        _recordedDays[key] = days;
        UpdateDayButtons();
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
            return;
        }
        _day = date.Date;
        _hasTime = false;
        _anchor = _day;
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
        var button = new ToggleButton
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
        _slots.Pick(source);
        SyncTilesToSlots();
        UpdateFocus();
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
        if (!_hasTime)
        {
            TryStart();
            if (!_hasTime)
            {
                ShowMessage(FocusedTile is null ? PlaybackText.SelectCameraFirst : null);
            }
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
        var playing = _hasTime && !_paused;
        PlayStateText.Text = playing ? "Oynatılıyor" : "Duraklatıldı";
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
