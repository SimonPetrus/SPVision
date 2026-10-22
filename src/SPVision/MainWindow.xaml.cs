using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SPVision.Controls;
using SPVision.Core;
using SPVision.Hik;
using SPVision.Updates;
using SPVision.Views;

namespace SPVision;

/// <summary>The three views of the main window (top bar tabs + gear).</summary>
enum MainSection
{
    Live,
    Playback,
    Settings,
}

public partial class MainWindow : Window
{
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>One camera row of the sidebar list; dot and tile number follow the live view.</summary>
    sealed record CameraRow(ListBoxItem Item, Ellipse Dot, TextBlock Number, Camera Camera, CameraRef Source);

    readonly List<CameraRow> _cameraRows = [];
    readonly List<ListBox> _cameraLists = [];
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly System.Diagnostics.Process _process = System.Diagnostics.Process.GetCurrentProcess();
    TimeSpan _lastCpuTime;
    DateTime _lastCpuSample;

    Point? _dragOrigin;
    bool _fullScreen;
    MainSection _section = MainSection.Live;
    MainSection _sectionBeforeSettings = MainSection.Live;
    WindowState _stateBeforeFullScreen;

    /// <summary>"Video indir" downloads: the app's, not Kayıt Oynatma's — they go on across pages and playback changes.</summary>
    readonly DownloadManager _downloads;

    /// <summary>Recorder disk check (read only) behind the status bar's red disk warning.</summary>
    readonly DiskMonitor _disks = new();

    /// <summary>"Uygulama kilidi": the lock screen over the collapsed app; null while unlocked.</summary>
    LockView? _lock;

    /// <summary>
    /// The lock was on at startup: live streams, disk checks, the first-run screen and "Tam ekranda aç" wait for the PIN
    /// (<see cref="StartApp"/>), so nothing connects to a device before it.
    /// </summary>
    bool _startHeld;

    /// <summary>"İşlem yapılmazsa kilitle": system-wide idle time checked once a second.</summary>
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        _downloads = new DownloadManager(Dispatcher);
        HookDownloads();
        Live.StateChanged += LiveStateChanged;
        Live.GroupChanged += GroupsChanged;
        Live.Message += ShowMessage;
        LivePicker.LayoutSelected += Live.SelectLayout;
        PlaybackPicker.SetLayouts(GridLayout.PlaybackStandard, GridLayout.PlaybackBigTile);
        PlaybackPicker.Current = Playback.Layout;
        PlaybackPicker.LayoutSelected += Playback.SelectLayout;
        Playback.StateChanged += PlaybackStateChanged;
        Playback.Message += ShowMessage;
        SettingsPanel.Message += ShowMessage;
        SettingsPanel.LiveStateOf = DeviceLiveState;
        SettingsPanel.DeviceSaved += DeviceSaved;
        SettingsPanel.DeviceRemoved += DeviceRemoved;
        SettingsPanel.CamerasRenamed += device =>
        {
            DevicesChanged();
            Live.RefreshDeviceCameras(device); // same camera objects: titles are refreshed, streams keep running
        };
        SettingsPanel.MotionBoxesChanged += SetMotionBoxes;
        SettingsPanel.QualitySelected += quality => Live.SelectQuality(quality);
        SettingsPanel.HardwareDecodingChanged += SetHardwareDecoding;
        _disks.Changed += UpdateDiskWarning;
        MotionToggle.IsChecked = App.Settings.ShowMotionBoxes;
        UpdateController.Changed += UpdateBannerState;
        UpdateController.Message += ShowMessage;
        UpdateController.MandatoryRequired += ShowMandatoryUpdate;
        BuildCameraList();
        BuildGroupList();
        Playback.RefreshCameras();

        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuSample = DateTime.UtcNow;
        _statusTimer.Tick += (_, _) => UpdateClockAndCpu();
        _statusTimer.Start();
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            MessageText.Visibility = Visibility.Collapsed;
        };
        UpdateClockAndCpu();
        UpdateStatusBar();

        Closing += OnClosing;
        _idleTimer.Tick += (_, _) => LockWhenIdle();
        if (AppLock.Enabled)
        {
            // Lock screen first: the live view does not restore its streams and nothing reads a device until the PIN.
            _startHeld = true;
            Live.StartHeld = true;
            ShowLock();
        }
        Loaded += (_, _) =>
        {
            if (!_startHeld)
            {
                StartApp();
            }
            UpdateController.Start(); // remembered mandatory update now, automatic check after 15 s
            _idleTimer.Start();
        };
    }

    /// <summary>Startup after the window loaded, or after the PIN when the lock held it back (<see cref="_startHeld"/>).</summary>
    void StartApp()
    {
        Live.Start(); // a held live view restores its layout and opens its streams now
        if (App.Settings.Devices.Count == 0)
        {
            ShowFirstRun(); // welcome card: online devices + compact form instead of an empty live view
        }
        else if (App.Settings.StartFullScreen && !_fullScreen)
        {
            ToggleFullScreen();
        }
        _disks.Start(App.Settings.Devices); // first disk read after 5 s, then every 5 min
    }

    /// <summary>Streams and recordings get this long to finish before the app exits anyway (the watchdog allows 8 s in all).</summary>
    static readonly TimeSpan ReleaseLimit = TimeSpan.FromSeconds(5);

    bool _closeStarted;
    bool _closeDone;

    /// <summary>
    /// Closing (X, Alt+F4, CloseMainWindow): the window disappears at once and the close is held back until every
    /// recording is finalized and every stream released (all tiles in parallel, off the UI thread, at most
    /// <see cref="ReleaseLimit"/>); then the app shuts down explicitly, which cleans the SDK up (App.OnExit). The exit
    /// no longer depends on WPF noticing the main window's destruction (the old close blocked the UI thread on the
    /// SDK stops of every tile, one after another, and the process could stay alive without a window). A watchdog
    /// ends the process if anything still hangs after 8 s.
    /// </summary>
    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeDone)
        {
            return; // Application.Shutdown closing the window for good
        }
        e.Cancel = true;
        if (_closeStarted)
        {
            return;
        }
        if (Views.RecorderSettingsView.FirmwareBusy && !Views.ConfirmDialog.Show(this,
                "Bir yazılım güncellemesi sürüyor. SPVision kapanırsa dosyanın gönderimi yarıda kesilir. Cihazın elektriğini kesmeyin. Yine de kapatılsın mı?",
                "Yine de kapat", "Vazgeç"))
        {
            return; // stays open; the upgrade goes on
        }
        _closeStarted = true;
        _disks.Stop();
        _idleTimer.Stop();
        App.StartExitWatchdog();
        App.LogShutdown("Kapanış başladı");
        _statusTimer.Stop();
        _messageTimer.Stop();
        Hide();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var release = Task.WhenAll(Live.ShutdownAsync(), Playback.ShutdownAsync(), DownloadManager.ShutdownAsync());
            var released = await Task.WhenAny(release, Task.Delay(ReleaseLimit)) == release;
            var left = ReleaseLimit - watch.Elapsed;
            var finished = await Recordings.WhenAllFinished(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            App.LogShutdown($"Akışlar {(released ? "bırakıldı" : "zamanında bırakılamadı")}, kayıtlar " +
                $"{(finished ? "tamamlandı" : "tamamlanamadı")} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            App.LogShutdown($"Kapanışta hata: {ex.GetType().Name}: {ex.Message}");
        }
        _closeDone = true;
        Application.Current.Shutdown();
    }

    // ---- Downloads ("Video indir"): the pill in the middle of the top bar, on every page ----

    void HookDownloads()
    {
        Playback.Downloads = _downloads;
        _downloads.Started += DownloadPillView.ShowProgress;
        _downloads.Changed += (batch, _) =>
        {
            if (ReferenceEquals(_downloads.Current, batch))
            {
                DownloadPillView.ShowProgress(batch);
            }
        };
        _downloads.Finished += DownloadPillView.ShowResult;
        _downloads.Message += ShowMessage;
        DownloadPillView.CancelRequested += _downloads.Cancel;
        DownloadPillView.Message += ShowMessage;
        // The pill's room follows the bar: window width, page tools (live / playback), tab texts.
        HeaderLayer.SizeChanged += (_, _) => FitDownloadPill();
        TabsPanel.SizeChanged += (_, _) => FitDownloadPill();
        ToolsPanel.SizeChanged += (_, _) => FitDownloadPill();
    }

    /// <summary>
    /// 440 px, exactly centred in the window (the bar's insets are equal, so its centre is the window's); narrower when the
    /// tabs on the left or the tools on the right would be closer than 12 px; hidden when not even 200 px fit.
    /// </summary>
    void FitDownloadPill()
    {
        if (HeaderLayer.ActualWidth <= 0)
        {
            return;
        }
        var leftEnd = TabsPanel.TranslatePoint(new Point(TabsPanel.ActualWidth, 0), HeaderLayer).X;
        var rightStart = ToolsPanel.TranslatePoint(new Point(0, 0), HeaderLayer).X;
        DownloadPillView.PillWidth = Core.DownloadPill.Width(HeaderLayer.ActualWidth, leftEnd, rightStart);
    }

    // ---- In-app update (banner for an optional update, blocking dialog for a mandatory one) ----

    bool _mandatoryOpen;
    DispatcherTimer? _mandatoryRetry;

    /// <summary>
    /// Banner text, progress and buttons follow <see cref="UpdateController"/>. Three equal 120 px slots: Neler yeni ·
    /// Sonra (progress bar while downloading) · Güncelle (İptal while downloading).
    /// </summary>
    void UpdateBannerState()
    {
        var show = UpdateController.ShowBanner;
        UpdateBanner.Visibility = show && !_fullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (!show || UpdateController.Offer is not { } offer)
        {
            return;
        }
        var phase = UpdateController.Phase;
        var downloading = phase == UpdatePhase.Downloading;

        UpdateBannerText.Inlines.Clear();
        if (UpdateController.Activity is { } activity)
        {
            UpdateBannerText.Inlines.Add(new System.Windows.Documents.Run($"SPVision {offer.Version}") { FontWeight = FontWeights.SemiBold });
            var detail = new System.Windows.Documents.Run(" · " + activity);
            detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, UpdateController.ActivityFailed ? "DangerText" : "TextFg");
            UpdateBannerText.Inlines.Add(detail);
            UpdateBannerText.ToolTip = activity;
        }
        else
        {
            UpdateBannerText.Inlines.Add(new System.Windows.Documents.Run($"Yeni sürüm var: {offer.Version}") { FontWeight = FontWeights.SemiBold });
            var installed = new System.Windows.Documents.Run($" · yüklü sürüm {UpdateController.CurrentVersion}");
            installed.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondary");
            UpdateBannerText.Inlines.Add(installed);
            UpdateBannerText.ToolTip = Core.Updates.UpdateText.FirstLine(offer.Notes);
        }
        AutomationPropertiesName(UpdateBannerText, string.Concat(UpdateBannerText.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)));

        UpdateLaterButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        UpdateBannerProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        if (downloading)
        {
            var percent = Core.Updates.UpdateText.Percent(UpdateController.Progress.Received, UpdateController.Progress.Total);
            UpdateBannerProgress.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
            UpdateBannerProgress.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
        }
        UpdateNowButton.Content = downloading ? "İptal" : "Güncelle";
        UpdateNowButton.SetResourceReference(StyleProperty, downloading ? "SettingsButton" : "SettingsAccentButton");
        AutomationPropertiesName(UpdateNowButton, downloading ? "İndirmeyi iptal et" : "Güncelle");
        UpdateNowButton.IsEnabled = phase is UpdatePhase.Available or UpdatePhase.Downloading;
        UpdateLaterButton.IsEnabled = phase is UpdatePhase.Available or UpdatePhase.Failed or UpdatePhase.Checking;
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateController.Offer is { } offer && UpdateDialog.ShowNotes(this, offer) && !UpdateController.IsBusy)
        {
            InstallUpdate();
        }
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateController.Dismiss();

    void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateController.Phase == UpdatePhase.Downloading)
        {
            UpdateController.CancelDownload();
        }
        else
        {
            InstallUpdate();
        }
    }

    /// <summary>Güncelle (banner, "Neler yeni", Settings): download + verify + elevated installer, then the normal close.</summary>
    public async void InstallUpdate()
    {
        if (await UpdateController.InstallAsync())
        {
            Close(); // OnClosing: streams released, recordings finalized, SDK cleaned up, so Inno Setup can replace the files
        }
    }

    /// <summary>
    /// Mandatory update: a blocking dialog (Güncelle / Çıkış) — the app cannot be used without updating. Postponed while a
    /// recorder firmware upgrade runs (closing would cut it off). Either way the app closes when the dialog ends.
    /// </summary>
    void ShowMandatoryUpdate()
    {
        if (_mandatoryOpen || _closeStarted || UpdateController.Kind != Core.Updates.UpdateKind.Mandatory)
        {
            return;
        }
        if (RecorderSettingsView.FirmwareBusy)
        {
            if (_mandatoryRetry is null)
            {
                _mandatoryRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _mandatoryRetry.Tick += (_, _) =>
                {
                    _mandatoryRetry!.Stop();
                    _mandatoryRetry = null;
                    ShowMandatoryUpdate();
                };
                _mandatoryRetry.Start();
            }
            return;
        }
        _mandatoryOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            UpdateDialog.ShowMandatory(this); // true: installer started; false: Çıkış
            Close();
        });
    }

    // ---- Sidebar: device rows + camera list ----

    void BuildCameraList()
    {
        DeviceList.Children.Clear();
        _cameraRows.Clear();
        _cameraLists.Clear();
        if (App.Settings.Devices.Count == 0)
        {
            DeviceList.Children.Add(new TextBlock
            {
                Text = "Henüz cihaz yok. Sağ üstteki Ayarlar düğmesiyle ekleyin.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = Res<double>("FontSizeTile"),
                Margin = new Thickness(10),
            }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
            return;
        }
        foreach (var device in App.Settings.Devices)
        {
            DeviceList.Children.Add(DeviceRow(device));
            var list = new ListBox { Padding = new Thickness(0, 4, 0, 4) };
            // Cameras switched off under Settings > "Listede göster" are left out.
            foreach (var camera in device.ListedCameras())
            {
                list.Items.Add(CameraItem(device, camera));
            }
            list.MouseDoubleClick += CameraList_MouseDoubleClick;
            list.SelectionChanged += CameraList_SelectionChanged;
            // The outer ScrollViewer scrolls; the list itself must not swallow the wheel.
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.PreviewMouseWheel += (_, e) =>
            {
                e.Handled = true;
                var scroller = (ScrollViewer)DeviceList.Parent;
                scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 3.0);
            };
            _cameraLists.Add(list);
            DeviceList.Children.Add(list);
        }
        UpdateCameraRows();
    }

    /// <summary>Device name, address and how many of its cameras the recorder reports online. Display only.</summary>
    Border DeviceRow(Device device)
    {
        var online = device.Cameras.Count(c => c.Online);
        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = device.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = Res<double>("FontSizeList"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.WithBrush(TextBlock.ForegroundProperty, "TextStrong"));
        text.Children.Add(new TextBlock
        {
            Text = $"{device.Host} · {online}/{device.Cameras.Count} çevrim içi",
            FontSize = Res<double>("FontSizeSmall"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
        };
        grid.Children.Add(new GlyphIcon { Data = Res<Geometry>("IconServer"), Size = 16 }.WithBrush(GlyphIcon.ForegroundProperty, "IconFg"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var row = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = grid,
            Tag = device,
            ToolTip = $"{device.Name} — çift tıklayın: tüm kameraları canlı izle",
        };
        row.SetResourceReference(Border.BackgroundProperty, "DeviceRowBg");
        row.SetResourceReference(Border.BorderBrushProperty, "Border");
        AutomationPropertiesName(row, $"{device.Name}, {device.Host}, {online}/{device.Cameras.Count} çevrim içi");
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                ShowLive(device);
                e.Handled = true;
            }
        };
        return row;
    }

    ListBoxItem CameraItem(Device device, Camera camera)
    {
        var dot = new Ellipse { Style = Res<Style>("StatusDot"), Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBlock
        {
            Text = camera.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var number = new TextBlock
        {
            FontSize = Res<double>("FontSizeSmall"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        grid.Children.Add(dot);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(number, 2);
        grid.Children.Add(number);

        var source = new CameraRef(device.Id, camera.Channel);
        var item = new ListBoxItem
        {
            Content = grid,
            Tag = source,
            ToolTip = camera.Online ? $"{camera.Name} · Kanal {camera.Channel}" : $"{camera.Name} · Kanal {camera.Channel} — çevrimdışı",
        };
        AutomationPropertiesName(item, camera.Name);
        _cameraRows.Add(new CameraRow(item, dot, number, camera, source));
        return item;
    }

    /// <summary>Dot = live state when the camera is on screen, otherwise what the recorder reports; number = its tile.</summary>
    void UpdateCameraRows()
    {
        foreach (var row in _cameraRows)
        {
            var tile = Live.TileOf(row.Source);
            row.Dot.SetResourceReference(Shape.FillProperty, tile?.State switch
            {
                TileState.Playing => "Online",
                TileState.Connecting => "Warning",
                TileState.Failed => "Danger",
                TileState.Stopped => "Offline",
                _ => row.Camera.Online ? "Online" : "Offline",
            });
            row.Number.Text = Live.TileNumberOf(row.Source)?.ToString(CultureInfo.InvariantCulture) ?? "";
            row.Item.Opacity = row.Camera.Online ? 1 : 0.6;
        }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        foreach (var row in _cameraRows)
        {
            var match = query.Length == 0 ||
                Turkish.CompareInfo.IndexOf(row.Camera.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
            row.Item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
        {
            return;
        }
        foreach (var list in _cameraLists.Where(l => l != sender))
        {
            list.SelectedItem = null;
        }
    }

    void CameraList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.Tag is not CameraRef source)
        {
            return;
        }
        Live.ShowCamera(source); // this list belongs to the live view; playback has its own
        e.Handled = true;
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragOrigin = e.GetPosition(DeviceList);

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragOrigin is not { } origin)
        {
            return;
        }
        var delta = e.GetPosition(DeviceList) - origin;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        var item = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.Tag is CameraRef source)
        {
            _dragOrigin = null;
            DragDrop.DoDragDrop(item, new DataObject(VideoTile.DragFormat, source), DragDropEffects.Copy);
        }
    }

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as T;
    }

    // ---- Device management (Settings > Cihaz yönetimi) ----

    /// <summary>Gear: opens the Settings view; pressed again it returns to the previous view.</summary>
    void Settings_Click(object sender, RoutedEventArgs e) =>
        SelectSection(_section == MainSection.Settings ? _sectionBeforeSettings : MainSection.Settings);

    FirstRunView? _firstRun;

    /// <summary>
    /// First run (no saved device): the welcome card covers the window (the app behind it is collapsed, so no video
    /// window can draw over it). "Sonra ekle" leaves the normal empty app; "Bağlan ve devam et" saves the tested device
    /// and opens its cameras in live view.
    /// </summary>
    void ShowFirstRun()
    {
        var view = new FirstRunView();
        view.Later += CloseFirstRun;
        view.Connected += device =>
        {
            App.Settings.Devices.Add(device);
            App.SaveSettings();
            CloseFirstRun();
            ShowMessage($"Kaydedildi: {device.Name}");
            DeviceSaved(device, loginChanged: true); // first device on an empty wall: live view shows it
        };
        _firstRun = view;
        AppShell.Visibility = Visibility.Collapsed;
        RootGrid.Children.Add(view);
    }

    // ---- App lock ("Uygulama kilidi"; AppLock, Core AppLockSettings) ----

    /// <summary>
    /// The lock screen covers the window. The app behind it is collapsed — like under the first-run screen — so no SDK or
    /// Flyleaf video window can draw over it; streams, recordings and downloads go on unseen. Popups (their own windows) are
    /// closed; keyboard shortcuts are off while it shows.
    /// </summary>
    void ShowLock()
    {
        if (_lock is not null || _closeStarted)
        {
            return;
        }
        var view = new LockView();
        view.Unlocked += Unlock;
        view.ResetConfirmed += ResetApp;
        _lock = view;
        ClosePopups();
        AppShell.Visibility = Visibility.Collapsed;
        if (_firstRun is not null)
        {
            _firstRun.Visibility = Visibility.Collapsed;
        }
        RootGrid.Children.Add(view);
        AppLock.SetLocked(true);
    }

    /// <summary>The right PIN: the app (or the first-run screen) is shown again; a held startup goes on now.</summary>
    void Unlock()
    {
        if (_lock is null)
        {
            return;
        }
        RootGrid.Children.Remove(_lock);
        _lock = null;
        if (_firstRun is not null)
        {
            _firstRun.Visibility = Visibility.Visible;
        }
        else
        {
            AppShell.Visibility = Visibility.Visible;
        }
        AppLock.SetLocked(false);
        if (_startHeld)
        {
            _startHeld = false;
            StartApp();
        }
    }

    /// <summary>
    /// Kilit ekranı › "PIN'i unuttum" › Sıfırla: the saved devices with their passwords, stream keys and certificate pins,
    /// saved views, camera groups and the PIN are forgotten (Core <see cref="AppReset"/>, through the settings store); app
    /// preferences stay. Their streams stop and SDK sessions close, downloads go on; then the first-run screen. Nothing is
    /// sent to a recorder.
    /// </summary>
    void ResetApp()
    {
        if (_lock is null)
        {
            return;
        }
        var devices = App.Settings.Devices.ToList();
        AppReset.ForgetDevices(App.Settings);
        App.SaveSettings();
        foreach (var device in devices)
        {
            DeviceRemoved(device); // tiles release their streams, SDK session closed, disk checks stop
        }
        Live.ShowGroup(null); // out of a camera group on screen: "Tüm kameralar", now empty
        // Removing the devices saved the (now empty) live and playback views again: forget those too.
        AppReset.ForgetDevices(App.Settings);
        App.SaveSettings();
        DevicesChanged(); // camera and group lists of the sidebar
        SettingsPanel.RefreshAppSettings();

        RootGrid.Children.Remove(_lock);
        _lock = null;
        if (_fullScreen)
        {
            ToggleFullScreen();
        }
        if (_startHeld)
        {
            _startHeld = false;
            Live.Start(); // nothing left to open; the live view is ready for the first device
        }
        if (_firstRun is not null)
        {
            RootGrid.Children.Remove(_firstRun);
            _firstRun = null;
        }
        ShowFirstRun();
        AppLock.SetLocked(false);
    }

    void LockWhenIdle()
    {
        if (_lock is null && !_closeStarted && AppLock.IdleMinutes > 0 &&
            IdleLock.IsDue(AppLock.SystemIdleTime(), AppLock.IdleMinutes))
        {
            ShowLock();
        }
    }

    /// <summary>Closes every open Popup of the app (layout picker, "Saate git", the playback tile bar…): they are windows.</summary>
    void ClosePopups()
    {
        var pending = new Stack<DependencyObject>([AppShell]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Popup { IsOpen: true } popup)
            {
                popup.IsOpen = false;
            }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    void CloseFirstRun()
    {
        if (_firstRun is null)
        {
            return;
        }
        RootGrid.Children.Remove(_firstRun); // Unloaded: its scan stops
        _firstRun = null;
        AppShell.Visibility = Visibility.Visible;
        SelectSection(MainSection.Live);
    }

    /// <summary>Kaydet in the device editor: new login data needs a new SDK session, then the tiles reconnect.</summary>
    void DeviceSaved(Device device, bool loginChanged)
    {
        if (loginChanged)
        {
            HikSession.Find(device.Id)?.Dispose();
        }
        // Checked first: ReloadDevice already puts newly listed cameras on the pages.
        var wallWasEmpty = Live.VisibleTiles.All(t => t.Camera is null);
        DevicesChanged();
        Live.ReloadDevice(device);
        Playback.ReloadDevice(device);
        _disks.Restart(device); // address or login may have changed: read its disks again now
        if (App.Settings.Devices.Count == 1 && wallWasEmpty)
        {
            ShowLive(device); // first device (e.g. first run): show its cameras right away
        }
    }

    void DeviceRemoved(Device device)
    {
        Live.RemoveDevice(device.Id);
        Playback.RemoveDevice(device.Id);
        HikSession.Find(device.Id)?.Dispose();
        _disks.Remove(device.Id);
        DevicesChanged();
    }

    void DevicesChanged()
    {
        BuildCameraList();
        BuildGroupList(); // camera counts follow the list
        SearchBox_TextChanged(SearchBox, null!);
        Playback.RefreshCameras();
        UpdateStatusBar();
    }

    /// <summary>Best live state among the device's tiles (Playing over Connecting over the rest).</summary>
    TileState? DeviceLiveState(Guid deviceId)
    {
        var states = Live.VisibleTiles.Where(t => t.Device?.Id == deviceId).Select(t => t.State).ToList();
        return states.Contains(TileState.Playing) ? TileState.Playing :
            states.Contains(TileState.Connecting) ? TileState.Connecting :
            states.Count > 0 ? states[0] : null;
    }

    /// <summary>Top bar toggle and Settings switch share one setting; both stay in sync.</summary>
    void SetMotionBoxes(bool show)
    {
        App.Settings.ShowMotionBoxes = show;
        App.SaveSettings();
        MotionToggle.IsChecked = show;
        Live.ApplyMotionBoxes(show);
        Playback.ApplyMotionBoxes(show);
        SettingsPanel.RefreshAppSettings();
    }

    /// <summary>
    /// Ayarlar › "Donanım hızlandırma": saved and applied at once, no restart. SDK display ports and Flyleaf players read
    /// the setting when a stream opens, so the open live tiles reconnect and a running playback re-seeks to its shared
    /// time; streams carrying a recording or clip are kept until it is stopped (never cut). Recording, clip and
    /// download ports decode in software either way. The status bar follows as the tiles report their decoder.
    /// </summary>
    void SetHardwareDecoding(bool hardware)
    {
        App.Settings.HardwareDecoding = hardware;
        App.SaveSettings();
        App.ApplyHardwareDecoding();
        var live = Live.ApplyHardwareDecoding(hardware);
        var playback = Playback.ApplyHardwareDecoding();
        ShowMessage(DecoderSwitch.Notice(hardware, live.Reopened + playback.Reopened, live.Kept + playback.Kept));
        UpdateStatusBar();
    }

    void MotionToggle_Click(object sender, RoutedEventArgs e) => SetMotionBoxes(MotionToggle.IsChecked == true);

    void ShowLive(Device device)
    {
        SelectSection(MainSection.Live);
        Live.ShowDevice(device);
    }

    // ---- Top bar ----

    void LiveStateChanged()
    {
        LivePicker.Current = Live.Layout;
        var quality = Live.GlobalQuality;
        QualityAuto.IsChecked = quality is null;
        QualityHd.IsChecked = quality == StreamQuality.Main;
        QualitySd.IsChecked = quality == StreamQuality.Sub;

        var recording = Live.SelectedIsRecording;
        RecordIcon.Data = Res<Geometry>(recording ? "IconStopSquare" : "IconRecord");
        RecordIcon.FillData = recording ? Res<Geometry>("IconStopSquare") : Res<Geometry>("IconRecordDot");
        RecordButton.ToolTip = recording ? "Kaydı durdur" : "Kayıt (seçili kare, MP4)";
        System.Windows.Automation.AutomationProperties.SetName(RecordButton, recording ? "Kaydı durdur" : "Seçili kamerayı kaydet");

        UpdateCameraRows();
        UpdateStatusBar();
        UpdatePageControl();
        foreach (var id in Live.VisibleTiles.Where(t => t.State == TileState.Playing && t.Device is not null)
                     .Select(t => t.Device!.Id).Distinct())
        {
            _disks.Connected(id); // first disk read right after the recorder connects, if the startup read failed
        }
    }

    void PlaybackStateChanged()
    {
        PlaybackPicker.Current = Playback.Layout;
        if (_section == MainSection.Playback)
        {
            UpdateStatusBar();
        }
    }

    void QualityAuto_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(null);

    void QualityHd_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(StreamQuality.Main);

    void QualitySd_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(StreamQuality.Sub);

    void Snapshot_Click(object sender, RoutedEventArgs e) => Live.Snapshot();

    void Record_Click(object sender, RoutedEventArgs e) => Live.ToggleRecording();

    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---- Status bar ----

    /// <summary>What the status bar needs of one tile, live or playback.</summary>
    readonly record struct TileInfo(Device Device, CameraRef Source, TileState State, bool UsesSdk, bool IsReconnecting, bool? Gpu);

    /// <summary>
    /// Connection, cameras on screen and video path of the shown view. Settings shows only the connection (of the live
    /// wall, which keeps running) on the left; CPU, clock and messages on the right are the same in every view.
    /// </summary>
    void UpdateStatusBar()
    {
        var playback = _section == MainSection.Playback;
        var viewItems = _section == MainSection.Settings ? Visibility.Collapsed : Visibility.Visible;
        OnScreenText.Visibility = viewItems;
        VideoPathText.Visibility = viewItems;
        UpdateGroupStatus();
        var tiles = playback
            ? Playback.VisibleTiles.Where(t => t.Device is not null)
                .Select(t => new TileInfo(t.Device!, t.Source!, t.State, t.UsesSdk, false, t.GpuDecoding)).ToList()
            : Live.VisibleTiles.Where(t => t.Device is not null)
                .Select(t => new TileInfo(t.Device!, t.Source!, t.State, t.UsesSdk, t.IsReconnecting, t.GpuDecoding)).ToList();

        // Connection: the device of the selected tile (or the first one) and how its streams are doing.
        var device = (playback ? null : Live.SelectedDevice) ?? tiles.FirstOrDefault().Device ?? App.Settings.Devices.FirstOrDefault();
        if (device is null)
        {
            ConnectionDot.SetResourceReference(Shape.FillProperty, "Offline");
            ConnectionText.Text = "Cihaz yok";
        }
        else
        {
            var own = tiles.Where(t => t.Device.Id == device.Id).ToList();
            var (dot, state) =
                own.Any(t => t.State == TileState.Playing) ? ("Online", "Bağlı") :
                own.Any(t => t.State == TileState.Connecting) ? ("Warning", "Bağlanıyor…") :
                own.Any(t => t.State == TileState.Failed) ? ("Danger", "Bağlantı yok") :
                ("Offline", "Görüntü açık değil");
            ConnectionDot.SetResourceReference(Shape.FillProperty, dot);
            var others = tiles.Select(t => t.Device.Id).Distinct().Count(id => id != device.Id);
            ConnectionText.Text = others > 0 ? $"{state} · {device.Name} (+{others} cihaz)" : $"{state} · {device.Name}";
        }

        var onScreen = tiles.Select(t => t.Source).Distinct().Count();
        var total = App.Settings.Devices.Sum(d => d.Cameras.Count);
        OnScreenText.Text = playback
            ? $"{onScreen} kamera eş zamanlı · {Playback.Day.ToString("dd.MM.yyyy", Turkish)}"
            : $"{onScreen} / {total} kamera ekranda";

        var reconnecting = tiles.Count(t => t.IsReconnecting);
        ReconnectPanel.Visibility = reconnecting > 0 && viewItems == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ReconnectText.Text = $"{reconnecting} kamera yeniden bağlanıyor";

        // Video path actually in use, and the decoder each player reports (GPU only when confirmed).
        VideoPathText.Text = Core.VideoPathText.Describe(tiles.Select(t => new Core.VideoPathText.Tile(t.UsesSdk, t.Gpu)));
        VideoPathText.ToolTip = string.IsNullOrEmpty(VideoPathText.Text) ? null : VideoPathText.Text;
    }

    /// <summary>
    /// Red "Disk sorunu — kayıt yapılmıyor olabilir" (every view): a recorder whose last successful disk read showed no disk or
    /// a disk that is not healthy; the tool tip lists recorder, disk and status. Hidden again when all are healthy.
    /// </summary>
    void UpdateDiskWarning()
    {
        var watch = _disks.Watch;
        DiskWarningButton.Visibility = watch.HasProblem ? Visibility.Visible : Visibility.Collapsed;
        DiskWarningText.Text = DiskWatch.WarningText;
        DiskWarningButton.ToolTip = watch.HasProblem ? watch.ToolTip : null;
        AutomationPropertiesName(DiskWarningButton, watch.HasProblem
            ? DiskWatch.WarningText + ": " + string.Join(", ", watch.Problems.Select(p => p.Text))
            : DiskWatch.WarningText);
    }

    /// <summary>Disk warning clicked: Ayarlar › Cihaz ayarları › Sistem (Diskler card) of the first recorder with a problem.</summary>
    void DiskWarning_Click(object sender, RoutedEventArgs e)
    {
        if (_disks.Watch.FirstProblemDevice is not { } id || App.Settings.Devices.FirstOrDefault(d => d.Id == id) is not { } device)
        {
            return;
        }
        SettingsPanel.ShowRecorderDisks(device); // before the view opens, so its first read is this recorder
        if (_section != MainSection.Settings)
        {
            SelectSection(MainSection.Settings);
        }
    }

    /// <summary>Status bar centre: "‹ Sayfa 1 / 3 ›" of the live view; hidden with a single page and outside the live view.</summary>
    void UpdatePageControl()
    {
        var pages = Live.PageCount;
        PagePanel.Visibility = _section == MainSection.Live && pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = string.Format(Turkish, "Sayfa {0} / {1}", Live.Page + 1, pages);
    }

    void PreviousPage_Click(object sender, RoutedEventArgs e) => Live.StepPage(-1);

    void NextPage_Click(object sender, RoutedEventArgs e) => Live.StepPage(1);

    /// <summary>This process's CPU share of the whole machine, sampled every 2 s, plus the clock.</summary>
    void UpdateClockAndCpu()
    {
        var now = DateTime.UtcNow;
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var wall = (now - _lastCpuSample).TotalMilliseconds;
        if (wall > 0 && now != _lastCpuSample)
        {
            var percent = (cpu - _lastCpuTime).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100;
            CpuText.Text = string.Format(Turkish, "CPU %{0:0.0}", Math.Clamp(percent, 0, 100));
        }
        _lastCpuTime = cpu;
        _lastCpuSample = now;
        ClockText.Text = DateTime.Now.ToString("dd.MM.yyyy HH:mm", Turkish);
    }

    void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.ToolTip = text;
        MessageText.Visibility = Visibility.Visible;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    // ---- Tabs / full screen ----

    void LiveTab_Click(object sender, RoutedEventArgs e) => SelectSection(MainSection.Live);

    void PlaybackTab_Click(object sender, RoutedEventArgs e) => SelectSection(MainSection.Playback);

    /// <summary>
    /// Shows one of the three views. The live wall keeps streaming behind the other two (it is only hidden);
    /// playback stops when it is left and resumes (same cameras, day and time) when it is shown again. The camera
    /// sidebar belongs to the live view; playback has its own (calendar + camera list), Settings its own nav.
    /// </summary>
    void SelectSection(MainSection section)
    {
        var previous = _section;
        if (section == MainSection.Settings && previous != MainSection.Settings)
        {
            _sectionBeforeSettings = previous;
        }
        _section = section;
        LiveTab.IsChecked = section == MainSection.Live;
        PlaybackTab.IsChecked = section == MainSection.Playback;
        SettingsButton.IsChecked = section == MainSection.Settings;
        LiveTools.Visibility = section == MainSection.Live ? Visibility.Visible : Visibility.Collapsed;
        PlaybackTools.Visibility = section == MainSection.Playback ? Visibility.Visible : Visibility.Collapsed;
        SidePanel.Visibility = section != MainSection.Live || _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Live.Visibility = section == MainSection.Live ? Visibility.Visible : Visibility.Collapsed;
        Playback.Visibility = section == MainSection.Playback ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == MainSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        if (previous == MainSection.Playback && section != MainSection.Playback)
        {
            Playback.Stop();
        }
        else if (section == MainSection.Playback && previous != MainSection.Playback)
        {
            Playback.Activate();
        }
        if (section == MainSection.Settings && previous != MainSection.Settings)
        {
            SettingsPanel.Open(SettingsPanel.Page);
        }
        else if (previous == MainSection.Settings && section != MainSection.Settings)
        {
            SettingsPanel.Close();
        }
        UpdateStatusBar();
        UpdatePageControl();
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        Header.Visibility = StatusBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        SidePanel.Visibility = _fullScreen || _section != MainSection.Live ? Visibility.Collapsed : Visibility.Visible;
        Playback.SidebarVisible = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
            DarkTitleBar.Reapply(this); // the theme may have changed while the caption was hidden
        }
        UpdateBannerState(); // hidden in full screen, like the top bar
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_firstRun is not null || _lock is not null)
        {
            return; // the welcome and lock screens have no camera wall to show full screen
        }
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _fullScreen))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    /// <summary>
    /// PageUp/PageDown page the live view (with wrap-around, like the status bar arrows). Preview, so a focused
    /// list or button does not take the keys first; never while typing in a text box.
    /// </summary>
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown) || Keyboard.Modifiers != ModifierKeys.None || _lock is not null ||
            _section != MainSection.Live || Live.PageCount <= 1 ||
            Keyboard.FocusedElement is TextBoxBase or PasswordBox)
        {
            return;
        }
        Live.StepPage(e.Key == Key.PageDown ? 1 : -1);
        e.Handled = true;
    }

    /// <summary>
    /// A second SPVision was started (App, Core SingleInstance): this window comes to the front, restored if minimized
    /// (to maximized if it was). False while closing, so the newcomer waits for this process to end and takes over.
    /// While locked the lock screen stays over the app; the activation gives its PIN boxes the keyboard.
    /// </summary>
    public bool BringToFront()
    {
        if (_closeStarted)
        {
            return false;
        }
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(this);
        }
        Activate();
        return true;
    }

    // ---- Helpers ----

    T Res<T>(string key) => (T)FindResource(key);

    static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
