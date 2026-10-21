using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// "Video indir": the 400 px panel docked on the right of Kayıt Oynatma (a real layout column — the tile grid shrinks
/// while it is open; nothing is drawn over the video HWNDs). Opened from a tile's hover bar (İndir): the cameras of the
/// playback tiles, that tile's camera checked; one calendar for the day range (first click = start day, second = end
/// day), the two times (default: the tile's playback time −5 / +5 min), the summary with duration, estimated size and
/// free space (İndir is off when the input is invalid or the files would not fit; no maximum length), the target folder.
/// İndir downloads the checked cameras one after another (<see cref="DownloadBatch"/>); the panel then shows each
/// camera's progress (bar, %, speed, estimate) with İptal, and "Klasörü aç" at the end.
/// </summary>
public partial class DownloadPanel : UserControl
{
    static readonly string[] WeekdayNames = ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];
    static string? _chosenFolder; // "…" choice, kept for the session (the settings' recording folder stays as it is)

    sealed record CameraRow(ToggleButton Button, Border Box, GlyphIcon Tick, TextBlock Name, Device Device, Camera Camera, CameraRef Source);

    sealed record DayCell(DateTime Date, Border Band, Button Button, TextBlock Number, Ellipse Dot);

    sealed record ProgressRow(ProgressBar Bar, TextBlock Status, TextBlock Detail);

    readonly List<CameraRow> _rows = [];
    readonly List<DayCell> _days = [];
    readonly HashSet<CameraRef> _checked = [];
    readonly Dictionary<CameraRef, double> _rates = [];                         // estimated file bytes per second
    readonly Dictionary<(CameraRef Camera, DateTime Month), IReadOnlySet<int>> _recorded = [];
    readonly HashSet<(CameraRef Camera, DateTime Month)> _recordedAsked = [];
    readonly Dictionary<DownloadItem, ProgressRow> _progressRows = [];
    DateRangePick _range = new(DateTime.Today, DateTime.Today);
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DownloadBatch? _batch;
    bool _loading;

    public DownloadPanel()
    {
        InitializeComponent();
        foreach (var name in WeekdayNames)
        {
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = name,
                Height = 22,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, 4, 0, 0),
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        }
    }

    /// <summary>The panel's close button / Vazgeç (the view collapses the column).</summary>
    public event Action? CloseRequested;

    /// <summary>Outcome messages for the status bar ("İndirildi: …").</summary>
    public event Action<string>? Message;

    /// <summary>A download is running (closing the panel keeps it running; the window's close cancels it).</summary>
    public bool IsDownloading => _batch?.Completion is { IsCompleted: false };

    /// <summary>The camera the panel was last opened for (its tile's İndir shows as active).</summary>
    public CameraRef? Target { get; private set; }

    string Folder => _chosenFolder ?? App.Settings.RecordingFolder;

    /// <summary>
    /// Opens (or re-targets) the panel for <paramref name="target"/>: the cameras on the playback tiles, the target
    /// checked; range = the target's playback time −5 … +5 min, or the selected day. While a download runs its progress
    /// stays on screen.
    /// </summary>
    public void Open(IReadOnlyList<(Device Device, Camera Camera)> cameras, CameraRef? target, DateTime? playbackTime, DateTime day)
    {
        Target = target;
        if (IsDownloading)
        {
            return;
        }
        ShowForm();
        _checked.Clear();
        if (target is { } t && cameras.Any(c => new CameraRef(c.Device.Id, c.Camera.Channel) == t))
        {
            _checked.Add(t);
        }
        BuildCameraList(cameras);
        var (start, end) = DownloadRange.Default(playbackTime, day, DateTime.Now);
        _range = new DateRangePick(start, end);
        _month = new DateTime(start.Year, start.Month, 1);
        _loading = true;
        StartTimeBox.Text = DownloadTimeText.Format(start);
        EndTimeBox.Text = DownloadTimeText.Format(end);
        _loading = false;
        FolderText.Text = Folder;
        FolderText.ToolTip = Folder;
        BuildCalendar();
        Evaluate();
        LoadRates();
    }

    /// <summary>The playback tiles changed while the form is shown: the list follows (checks of remaining cameras stay).</summary>
    public void RefreshCameras(IReadOnlyList<(Device Device, Camera Camera)> cameras)
    {
        if (FormPanel.Visibility != Visibility.Visible)
        {
            return;
        }
        var present = cameras.Select(c => new CameraRef(c.Device.Id, c.Camera.Channel)).ToHashSet();
        _checked.RemoveWhere(c => !present.Contains(c));
        BuildCameraList(cameras);
        Evaluate();
        LoadRates();
        LoadRecordedDays();
    }

    /// <summary>Window closing: running downloads stop, their partial files are deleted.</summary>
    public Task ShutdownAsync() => DownloadBatch.CancelAllAsync(TimeSpan.FromSeconds(4));

    // ---- Cameras ----

    void BuildCameraList(IReadOnlyList<(Device Device, Camera Camera)> cameras)
    {
        CameraList.Children.Clear();
        _rows.Clear();
        var number = 0;
        foreach (var (device, camera) in cameras)
        {
            number++;
            var tick = new GlyphIcon
            {
                Data = Geometry.Parse("M5,12 L10,17 L19,7"),
                Size = 11,
                StrokeThickness = 3.5,
            };
            tick.SetResourceReference(GlyphIcon.ForegroundProperty, "OnAccent");
            var box = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Child = tick,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
            };
            var name = new TextBlock
            {
                Text = camera.Name,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
            grid.Children.Add(box);
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);
            var source = new CameraRef(device.Id, camera.Channel);
            var button = new ClickToggleButton
            {
                Style = (Style)FindResource("CameraRow"),
                Content = grid,
                ToolTip = $"{camera.Name} · Kanal {camera.Channel}",
            };
            AutomationProperties.SetAutomationId(button, $"DownloadCamera{number}");
            button.Click += (_, _) => ToggleCamera(source);
            CameraList.Children.Add(button);
            _rows.Add(new CameraRow(button, box, tick, name, device, camera, source));
        }
        if (_rows.Count == 0)
        {
            CameraList.Children.Add(new TextBlock
            {
                Text = "Oynatılan kamera yok.",
                Margin = new Thickness(8),
                FontSize = (double)FindResource("FontSizeTile"),
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        }
        UpdateRows();
    }

    void ToggleCamera(CameraRef source)
    {
        if (!_checked.Remove(source))
        {
            _checked.Add(source);
        }
        UpdateRows();
        Evaluate();
        LoadRates();
        LoadRecordedDays();
    }

    void UpdateRows()
    {
        foreach (var row in _rows)
        {
            var on = _checked.Contains(row.Source);
            row.Button.IsChecked = on;
            row.Button.SetResourceReference(BackgroundProperty, on ? "RangeBg" : "CardBg");
            row.Name.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            row.Box.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "InputBorder");
            if (on)
            {
                row.Box.SetResourceReference(Border.BackgroundProperty, "Accent");
            }
            else
            {
                row.Box.Background = Brushes.Transparent;
            }
            row.Tick.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            AutomationProperties.SetName(row.Button, row.Camera.Name + (on ? ", seçili" : ""));
        }
        CameraCount.Text = DownloadForm.Count(_checked.Count, _rows.Count);
    }

    IEnumerable<CameraRow> CheckedRows => _rows.Where(r => _checked.Contains(r.Source));

    /// <summary>Estimated file rate per camera from its main stream (read-only ISAPI GET; asked once per camera).</summary>
    void LoadRates()
    {
        foreach (var row in CheckedRows.Where(r => !_rates.ContainsKey(r.Source)).ToList())
        {
            _rates[row.Source] = DownloadForm.BytesPerSecond(0, 0, 0, 0); // until the answer: 4 Mb/s
            _ = LoadRateAsync(row);
        }
    }

    async Task LoadRateAsync(CameraRow row)
    {
        try
        {
            using var isapi = new IsapiClient(row.Device, TimeSpan.FromSeconds(10));
            var doc = await isapi.GetConfigAsync(IsapiPaths.Stream(row.Camera.Channel, StreamQuality.Main));
            if (StreamSettings.Parse(doc) is { } stream)
            {
                _rates[row.Source] = DownloadForm.BytesPerSecond(stream.Width, stream.Height, stream.FrameRate, stream.Bitrate);
                Evaluate();
            }
        }
        catch (Exception e) when (e is IsapiException or UriFormatException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            // The default estimate stays.
        }
    }

    // ---- Calendar ----

    void BuildCalendar()
    {
        MonthTitle.Text = DownloadForm.MonthTitle(_month);
        DayGrid.Children.Clear();
        _days.Clear();
        var blanks = CalendarMonth.LeadingBlanks(_month.Year, _month.Month);
        var count = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (var cell = 0; cell < 42; cell++)
        {
            var dayNumber = cell - blanks + 1;
            var holder = new Grid { Height = 34, Margin = new Thickness(0, 0, 0, 2) };
            DayGrid.Children.Add(holder);
            if (dayNumber < 1 || dayNumber > count)
            {
                continue;
            }
            var date = new DateTime(_month.Year, _month.Month, dayNumber);
            var band = new Border();
            var number = new TextBlock { Text = dayNumber.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 1, 0, 0), Fill = Brushes.Transparent };
            var button = new Button
            {
                Style = (Style)FindResource("RangeDay"),
                Content = new StackPanel { Children = { number, dot } },
                IsEnabled = date <= DateTime.Today,
            };
            AutomationProperties.SetAutomationId(button, $"DownloadDay{dayNumber}");
            button.Click += (_, _) => PickDay(date);
            holder.Children.Add(band);
            holder.Children.Add(button);
            _days.Add(new DayCell(date, band, button, number, dot));
        }
        NextMonthButton.IsEnabled = _month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        UpdateDays();
        LoadRecordedDays();
    }

    void PickDay(DateTime date)
    {
        if (_range.Click(date, DateTime.Today))
        {
            UpdateDays();
            Evaluate();
        }
    }

    void UpdateDays()
    {
        var recorded = RecordedDaysOfMonth();
        foreach (var day in _days)
        {
            var column = ((int)day.Date.DayOfWeek + 6) % 7;
            var band = _range.Band(day.Date, column);
            var endpoint = band != RangeBand.None && _range.IsEndpoint(day.Date);
            if (band is RangeBand.None or RangeBand.Single)
            {
                day.Band.Background = Brushes.Transparent;
            }
            else
            {
                day.Band.SetResourceReference(Border.BackgroundProperty, "RangeBg");
            }
            day.Band.CornerRadius = band switch
            {
                RangeBand.Start => new CornerRadius(17, 0, 0, 17),
                RangeBand.End => new CornerRadius(0, 17, 17, 0),
                RangeBand.StartEnd => new CornerRadius(17),
                _ => new CornerRadius(0),
            };
            if (endpoint)
            {
                day.Button.SetResourceReference(BackgroundProperty, "Accent");
                day.Button.SetResourceReference(ForegroundProperty, "OnAccent");
            }
            else
            {
                day.Button.Background = Brushes.Transparent;
                day.Button.SetResourceReference(ForegroundProperty, "TextFg");
            }
            day.Button.FontWeight = endpoint ? FontWeights.Bold : FontWeights.Medium;
            var hasRecording = recorded.Contains(day.Date.Day) && day.Date <= DateTime.Today;
            if (hasRecording)
            {
                day.Dot.SetResourceReference(Shape.FillProperty, endpoint ? "OnAccent" : "Recorded");
            }
            else
            {
                day.Dot.Fill = Brushes.Transparent;
            }
            AutomationProperties.SetName(day.Button, day.Date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR")) +
                (hasRecording ? ", kayıt var" : "") + (endpoint ? (day.Date == _range.Start ? ", başlangıç" : ", bitiş") : ""));
        }
        HintText.Text = _range.Hint;
        var endDay = _range.Picking ? _range.Start : _range.End;
        StartLabel.Text = $"Başlangıç · {DownloadTimeText.Date(_range.Start)}";
        EndLabel.Text = $"Bitiş · {DownloadTimeText.Date(endDay)}";
    }

    HashSet<int> RecordedDaysOfMonth()
    {
        var days = new HashSet<int>();
        foreach (var row in CheckedRows)
        {
            if (_recorded.TryGetValue((row.Source, _month), out var known))
            {
                days.UnionWith(known);
            }
        }
        return days;
    }

    /// <summary>Green dots: the recorder's month query per checked camera (read-only, what the playback calendar uses).</summary>
    void LoadRecordedDays()
    {
        foreach (var row in CheckedRows.Where(r => _recordedAsked.Add((r.Source, _month))).ToList())
        {
            _ = LoadRecordedDaysAsync(row, _month);
        }
        UpdateDays();
    }

    async Task LoadRecordedDaysAsync(CameraRow row, DateTime month)
    {
        try
        {
            using var isapi = new IsapiClient(row.Device, TimeSpan.FromSeconds(20));
            if (await isapi.GetRecordedDaysAsync(row.Camera.Channel, month) is { } days)
            {
                _recorded[(row.Source, month)] = days;
                if (month == _month)
                {
                    UpdateDays();
                }
            }
        }
        catch (Exception e) when (e is IsapiException or UriFormatException or System.Net.Http.HttpRequestException or System.IO.IOException)
        {
            _recordedAsked.Remove((row.Source, month)); // asked again on the next month / camera change
        }
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

    // ---- Times, summary ----

    void Time_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(c => char.IsAsciiDigit(c) || c == ':');

    void Time_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            Evaluate();
        }
    }

    DownloadSummary? _summary;

    void Evaluate()
    {
        var rate = CheckedRows.Sum(r => _rates.TryGetValue(r.Source, out var value) ? value : DownloadForm.BytesPerSecond(0, 0, 0, 0));
        var endDay = _range.Picking ? _range.Start : _range.End;
        _summary = DownloadForm.Evaluate(_range.Start, StartTimeBox.Text, endDay, EndTimeBox.Text, _checked.Count, rate, FreeSpace(), DateTime.Now);
        SummaryText.Text = _summary.Text;
        SizeText.Text = _summary.SizeLine ?? "";
        SizeText.Visibility = _summary.SizeLine is null ? Visibility.Collapsed : Visibility.Visible;
        SummaryBox.SetResourceReference(Border.BackgroundProperty, _summary.CanStart ? "WindowBg" : "WarningSoftBg");
        var fg = _summary.CanStart ? "TextFg" : "WarningText";
        SummaryText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        SizeText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        StartButton.IsEnabled = _summary.CanStart;
        StartButton.ToolTip = _summary.CanStart ? "Seçili kameraları indir" : _summary.Text;
    }

    long? FreeSpace()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Folder));
            return string.IsNullOrEmpty(root) ? null : new System.IO.DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "İndirme klasörünü seçin", Multiselect = false };
        if (System.IO.Directory.Exists(Folder))
        {
            dialog.InitialDirectory = Folder;
        }
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _chosenFolder = dialog.FolderName;
            FolderText.Text = Folder;
            FolderText.ToolTip = Folder;
            Evaluate();
        }
    }

    // ---- Download ----

    void Start_Click(object sender, RoutedEventArgs e)
    {
        Evaluate();
        if (_summary is not { CanStart: true, Start: { } start, End: { } end })
        {
            return;
        }
        var cameras = CheckedRows.Select(r => (r.Device, r.Camera)).ToList();
        _batch = new DownloadBatch(cameras, start, end, Folder, Dispatcher);
        _batch.ItemChanged += OnItemChanged;
        ShowProgress(_batch);
        _ = RunBatchAsync(_batch);
    }

    async Task RunBatchAsync(DownloadBatch batch)
    {
        await batch.RunAsync();
        if (!ReferenceEquals(batch, _batch))
        {
            return;
        }
        CancelButton.Visibility = Visibility.Collapsed;
        DoneFooter.Visibility = Visibility.Visible;
        OpenFolderButton.IsEnabled = batch.Items.Any(i => i.Outcome?.HasFile == true);
        ProgressTitle.Text = batch.IsCancelled ? "İndirme iptal edildi"
            : batch.Items.All(i => i.Outcome?.Kind == Hik.DownloadOutcomeKind.Done) ? "İndirildi"
            : "İndirme bitti";
    }

    void OnItemChanged(DownloadItem item)
    {
        if (!_progressRows.TryGetValue(item, out var row))
        {
            return;
        }
        var outcome = item.Outcome;
        row.Status.Text = outcome switch
        {
            { Kind: Hik.DownloadOutcomeKind.Done } => "İndirildi",
            { Kind: Hik.DownloadOutcomeKind.Incomplete } => "Eksik indirildi",
            { Kind: Hik.DownloadOutcomeKind.Cancelled } => "İptal edildi",
            { Kind: Hik.DownloadOutcomeKind.NoRecordings } => "Kayıt yok",
            { Kind: Hik.DownloadOutcomeKind.Failed } => "İndirilemedi",
            _ when item.Running => item.Progress is { } p ? $"%{p.Percent}" : item.Status,
            _ => item.Status,
        };
        row.Status.SetResourceReference(TextBlock.ForegroundProperty, outcome switch
        {
            { Kind: Hik.DownloadOutcomeKind.Done } => "Online",
            { Kind: Hik.DownloadOutcomeKind.Failed or Hik.DownloadOutcomeKind.Incomplete } => "WarningText",
            _ => "TextSecondary",
        });
        if (outcome is { Kind: Hik.DownloadOutcomeKind.Done or Hik.DownloadOutcomeKind.Incomplete })
        {
            row.Bar.Value = 100;
            row.Detail.Text = $"{System.IO.Path.GetFileName(outcome.Path)} · {DownloadEstimate.Text(outcome.Bytes)}" +
                (outcome.Speed is { } speed ? $" · {speed.ToString("0.0", CultureInfo.GetCultureInfo("tr-TR"))}×" : "") +
                (outcome.Kind == Hik.DownloadOutcomeKind.Incomplete ? $"\n{outcome.Message}" : "");
            row.Detail.ToolTip = outcome.Path;
            Message?.Invoke(outcome.Message);
        }
        else if (outcome is not null || IsFinalStatus(item))
        {
            row.Detail.Text = item.Status;
            if (outcome is { Kind: Hik.DownloadOutcomeKind.Failed })
            {
                Message?.Invoke($"{item.Camera.Name}: {item.Status}");
            }
        }
        else if (item.Progress is { } progress)
        {
            row.Bar.Value = progress.Percent;
            row.Detail.Text = DownloadText.Progress(progress) + (progress.FrameTime is { } time ? $" · {time:HH:mm:ss}" : "");
        }
        else
        {
            row.Detail.Text = item.Status;
        }
        AutomationProperties.SetName(row.Bar, $"{item.Camera.Name}: {row.Status.Text}");
    }

    static bool IsFinalStatus(DownloadItem item) =>
        item.Status is DownloadText.NoRecordings or DownloadText.Cancelled || item.Status.StartsWith("İndirilemedi", StringComparison.Ordinal);

    void ShowProgress(DownloadBatch batch)
    {
        FormPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        FormFooter.Visibility = Visibility.Collapsed;
        DoneFooter.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ProgressTitle.Text = batch.Items.Count == 1 ? "1 kamera indiriliyor" : $"{batch.Items.Count} kamera sırayla indiriliyor";
        ProgressRange.Text = $"{batch.Start:dd.MM.yyyy HH:mm:ss}  →  {batch.End:dd.MM.yyyy HH:mm:ss}";
        ProgressList.Children.Clear();
        _progressRows.Clear();
        var number = 0;
        foreach (var item in batch.Items)
        {
            number++;
            var name = new TextBlock
            {
                Text = item.Camera.Name,
                FontSize = (double)FindResource("FontSizeList"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }.WithBrush(TextBlock.ForegroundProperty, "TextStrong");
            var status = new TextBlock
            {
                Text = item.Status,
                FontSize = (double)FindResource("FontSizeTile"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
            Typography.SetNumeralAlignment(status, FontNumeralAlignment.Tabular);
            var head = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            head.Children.Add(name);
            Grid.SetColumn(status, 1);
            head.Children.Add(status);
            var bar = new ProgressBar { Style = (Style)FindResource("ThinProgress"), Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetAutomationId(bar, $"DownloadProgress{number}");
            var detail = new TextBlock
            {
                Text = item.Status,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = (double)FindResource("FontSizeSmall"),
                TextWrapping = TextWrapping.Wrap,
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
            Typography.SetNumeralAlignment(detail, FontNumeralAlignment.Tabular);
            var card = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new StackPanel { Children = { head, bar, detail } },
            }.WithBrush(Border.BackgroundProperty, "WindowBg").WithBrush(Border.BorderBrushProperty, "Border");
            ProgressList.Children.Add(card);
            _progressRows[item] = new ProgressRow(bar, status, detail);
        }
    }

    void ShowForm()
    {
        FormPanel.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
        FormFooter.Visibility = Visibility.Visible;
        DoneFooter.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _batch?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressTitle.Text = "İptal ediliyor…";
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = _batch?.Items.Select(i => i.Outcome).FirstOrDefault(o => o?.HasFile == true)?.Path;
        try
        {
            if (file is not null && System.IO.File.Exists(file))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
            }
            else if (System.IO.Directory.Exists(_batch?.Folder ?? Folder))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{_batch?.Folder ?? Folder}\"");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Message?.Invoke($"Klasör açılamadı: {ex.Message}");
        }
    }

    /// <summary>Back to the form (same cameras and range) for another download.</summary>
    void New_Click(object sender, RoutedEventArgs e)
    {
        _batch = null;
        CancelButton.IsEnabled = true;
        ShowForm();
        Evaluate();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
