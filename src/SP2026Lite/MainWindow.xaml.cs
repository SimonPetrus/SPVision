using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SP2026Lite.Controls;
using SP2026Lite.Core;
using SP2026Lite.Views;

namespace SP2026Lite;

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
    WindowState _stateBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        Live.StateChanged += LiveStateChanged;
        Live.Message += ShowMessage;
        BuildCameraList();
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

        Closing += (_, _) =>
        {
            _statusTimer.Stop();
            Live.StopAll();
            Playback.Stop();
        };
        Loaded += (_, _) =>
        {
            if (App.Settings.Devices.Count == 0)
            {
                AddDevice();
            }
        };
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
                Foreground = Res<Brush>("TextSecondary"),
                FontSize = Res<double>("FontSizeTile"),
                Margin = new Thickness(10),
            });
            return;
        }
        foreach (var device in App.Settings.Devices)
        {
            DeviceList.Children.Add(DeviceRow(device));
            var list = new ListBox { Padding = new Thickness(0, 4, 0, 4) };
            foreach (var camera in device.Cameras.OrderBy(c => c.Channel))
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
            Foreground = Res<Brush>("TextStrong"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{device.Host} · {online}/{device.Cameras.Count} çevrim içi",
            FontSize = Res<double>("FontSizeSmall"),
            Foreground = Res<Brush>("TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
        };
        grid.Children.Add(new GlyphIcon { Data = Res<Geometry>("IconServer"), Size = 16, Foreground = Res<Brush>("IconFg") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var row = new Border
        {
            Background = Res<Brush>("DeviceRowBg"),
            BorderBrush = Res<Brush>("Border"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(10, 8, 10, 8),
            Child = grid,
            Tag = device,
            ToolTip = $"{device.Name} — çift tıklayın: tüm kameraları canlı izle",
        };
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
            Foreground = Res<Brush>("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
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
            row.Dot.Fill = Res<Brush>(tile?.State switch
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
        if (PlaybackTab.IsChecked == true)
        {
            Playback.SelectCamera(source);
        }
        else
        {
            Live.ShowCamera(source);
        }
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

    // ---- Device management (interim: the gear opens the device dialog) ----

    // TODO: a Settings view (add/edit/remove devices and cameras) will replace this; until then the
    // gear opens the existing device dialog so device management stays reachable.
    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var device = Live.SelectedDevice ?? App.Settings.Devices.FirstOrDefault();
        if (device is null)
        {
            AddDevice();
        }
        else
        {
            EditDevice(device);
        }
    }

    void AddDevice()
    {
        var dialog = new DeviceDialog(null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } device)
        {
            App.Settings.Devices.Add(device);
            App.SaveSettings();
            DevicesChanged();
            ShowLive(device);
        }
    }

    void EditDevice(Device device)
    {
        var dialog = new DeviceDialog(device) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } updated)
        {
            var index = App.Settings.Devices.IndexOf(device);
            App.Settings.Devices[index] = updated;
            App.SaveSettings();
            DevicesChanged();
            Live.ReloadDevice(updated);
        }
    }

    void DevicesChanged()
    {
        BuildCameraList();
        SearchBox_TextChanged(SearchBox, null!);
        Playback.RefreshCameras();
        UpdateStatusBar();
    }

    void ShowLive(Device device)
    {
        SelectTab(live: true);
        Live.ShowDevice(device);
    }

    // ---- Top bar ----

    void LiveStateChanged()
    {
        var layout = Live.LayoutTileCount.ToString(CultureInfo.InvariantCulture);
        foreach (var button in new[] { Layout6, Layout8, Layout25, Layout32 })
        {
            button.IsChecked = (string)button.Tag == layout;
        }
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
    }

    void Layout_Click(object sender, RoutedEventArgs e) => Live.SelectLayout(int.Parse((string)((ToggleButton)sender).Tag, CultureInfo.InvariantCulture));

    void QualityAuto_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(null);

    void QualityHd_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(StreamQuality.Main);

    void QualitySd_Click(object sender, RoutedEventArgs e) => Live.SelectQuality(StreamQuality.Sub);

    void Snapshot_Click(object sender, RoutedEventArgs e) => Live.Snapshot();

    void Record_Click(object sender, RoutedEventArgs e) => Live.ToggleRecording();

    void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    // ---- Status bar ----

    void UpdateStatusBar()
    {
        var tiles = Live.VisibleTiles.Where(t => t.Device is not null).ToList();

        // Connection: the device of the selected tile (or the first one) and how its streams are doing.
        var device = Live.SelectedDevice ?? tiles.FirstOrDefault()?.Device ?? App.Settings.Devices.FirstOrDefault();
        if (device is null)
        {
            ConnectionDot.Fill = Res<Brush>("Offline");
            ConnectionText.Text = "Cihaz yok";
        }
        else
        {
            var own = tiles.Where(t => t.Device!.Id == device.Id).ToList();
            var (dot, state) =
                own.Any(t => t.State == TileState.Playing) ? ("Online", "Bağlı") :
                own.Any(t => t.State == TileState.Connecting) ? ("Warning", "Bağlanıyor…") :
                own.Any(t => t.State == TileState.Failed) ? ("Danger", "Bağlantı yok") :
                ("Offline", "Görüntü açık değil");
            ConnectionDot.Fill = Res<Brush>(dot);
            var others = tiles.Select(t => t.Device!.Id).Distinct().Count(id => id != device.Id);
            ConnectionText.Text = others > 0 ? $"{state} · {device.Name} (+{others} cihaz)" : $"{state} · {device.Name}";
        }

        var total = App.Settings.Devices.Sum(d => d.Cameras.Count);
        var onScreen = tiles.Select(t => t.Source).Distinct().Count();
        OnScreenText.Text = $"{onScreen} / {total} kamera ekranda";

        var reconnecting = tiles.Count(t => t.IsReconnecting);
        ReconnectPanel.Visibility = reconnecting > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReconnectText.Text = $"{reconnecting} kamera yeniden bağlanıyor";

        // Video path actually in use. Both paths decode on the GPU (PlayCtrl decode engine / D3D11VA).
        var sdk = tiles.Count(t => t.UsesSdk);
        var ffmpeg = tiles.Count - sdk;
        VideoPathText.Text =
            sdk > 0 && ffmpeg > 0 ? $"Şifreli akış · SDK ({sdk}) + Şifresiz · FFmpeg ({ffmpeg}) · GPU çözme" :
            sdk > 0 ? "Şifreli akış · SDK · GPU çözme" :
            ffmpeg > 0 ? "Şifresiz akış · FFmpeg · GPU çözme" :
            "";
        VideoPathText.ToolTip = string.IsNullOrEmpty(VideoPathText.Text) ? null : VideoPathText.Text;
    }

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

    void LiveTab_Click(object sender, RoutedEventArgs e) => SelectTab(live: true);

    void PlaybackTab_Click(object sender, RoutedEventArgs e) => SelectTab(live: false);

    void SelectTab(bool live)
    {
        LiveTab.IsChecked = live;
        PlaybackTab.IsChecked = !live;
        LiveTools.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Live.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        Playback.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        if (live)
        {
            Playback.Stop();
        }
    }

    void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        Header.Visibility = SidePanel.Visibility = StatusBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
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
        }
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && _fullScreen))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    // ---- Helpers ----

    T Res<T>(string key) => (T)FindResource(key);

    static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
