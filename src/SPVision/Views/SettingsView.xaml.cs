using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SPVision.Controls;
using SPVision.Core;
using SPVision.Core.Updates;
using SPVision.Hik;
using SPVision.Updates;

namespace SPVision.Views;

public enum SettingsPage
{
    Devices,
    App,
    Recorder,
}

/// <summary>
/// Settings view (gear in the top bar): device management (list + inline editor), app settings and recorder
/// settings (<see cref="RecorderSettingsView"/>, the only place that writes to a recorder, after confirmation).
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>Device list columns: chevron | DURUM | CİHAZ ADI | ADRES | TÜR | KAMERA, with 16 px gaps.</summary>
    static readonly GridLength[] ListColumns =
    [
        new(20), new(16), new(110), new(16), new(1, GridUnitType.Star), new(16), new(160), new(16), new(140), new(16), new(100),
    ];

    enum Reach
    {
        Probing,
        Online,
        Offline,
    }

    /// <summary>One device of the list: its row, the cells that change, and the panel holding the editor.</summary>
    sealed class DeviceBlock
    {
        public required Device Device { get; init; }
        public required Border Container { get; init; }
        public required ToggleButton Row { get; init; }
        public required RotateTransform Chevron { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock Status { get; init; }
        public required TextBlock Type { get; init; }
        public required TextBlock Count { get; init; }
        public required StackPanel Body { get; init; }
    }

    readonly Dictionary<Guid, Reach> _reach = [];
    readonly List<DeviceBlock> _blocks = [];
    DeviceEditor? _editor;
    AddDevicePanel? _addPanel;   // "Cihaz ekle": the new device is not in the settings until Kaydet
    CancellationTokenSource? _probeCts;
    bool _loadingSwitches;

    public SettingsView()
    {
        InitializeComponent();
        BuildListHeader();
        RecorderPage.Message += text => Message?.Invoke(text);
        RecorderPage.DeviceUpdated += RecorderPage_DeviceUpdated;
        RecorderPage.CamerasRenamed += device =>
        {
            App.SaveSettings();
            CamerasRenamed?.Invoke(device);
        };
        NavDevices.IsChecked = true;
        UpdateController.Changed += RefreshUpdates;
        RefreshUpdates();
    }

    /// <summary>Short feedback for the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>A device was saved: (device, login data changed). The main window reconnects its tiles.</summary>
    public event Action<Device, bool>? DeviceSaved;

    public event Action<Device>? DeviceRemoved;

    /// <summary>Cihaz ayarları renamed cameras on the recorder (names already saved): refresh lists and tile titles.</summary>
    public event Action<Device>? CamerasRenamed;

    public event Action<bool>? MotionBoxesChanged;

    public event Action<StreamQuality?>? QualitySelected;

    /// <summary>"Donanım hızlandırma" switched: the main window saves it, applies it and reopens the open streams.</summary>
    public event Action<bool>? HardwareDecodingChanged;

    /// <summary>Live state of a device's tiles (best one), so "Bağlı" shows while its video plays.</summary>
    public Func<Guid, TileState?>? LiveStateOf { get; set; }

    public SettingsPage Page =>
        NavApp.IsChecked == true ? SettingsPage.App : NavRecorder.IsChecked == true ? SettingsPage.Recorder : SettingsPage.Devices;

    /// <summary>Called when the view is shown: selects a page and reloads everything from the settings.</summary>
    public void Open(SettingsPage page)
    {
        (page switch { SettingsPage.App => NavApp, SettingsPage.Recorder => NavRecorder, _ => NavDevices }).IsChecked = true;
        ShowPage(page);
        LoadAppSettings();
        CloseEditor();
        CloseAddDevice();
        BuildDeviceList();
        ProbeDevices();
    }

    /// <summary>Called when the view is hidden: drops an unsaved draft, stops discovery and probing.</summary>
    public void Close()
    {
        _probeCts?.Cancel();
        RecorderPage.Close();
        RecorderPage.Visibility = Visibility.Collapsed; // the next Open reads the device again
        CloseEditor();
        CloseAddDevice();
    }

    /// <summary>
    /// "Cihaz ekle": the "Yeni cihaz" card under the list — online devices (SADP inquiry, scanned at once) and the
    /// compact form. An expanded device row is closed (one form at a time).
    /// </summary>
    public void BeginAddDevice()
    {
        NavDevices.IsChecked = true;
        ShowPage(SettingsPage.Devices);
        CloseEditor();
        CloseAddDevice();
        var panel = new AddDevicePanel(AddDeviceMode.Settings);
        panel.Saved += AddPanel_Saved;
        panel.Cancelled += CloseAddDevice;
        _addPanel = panel;
        NewDeviceHost.Content = panel;
        NewDeviceCard.Visibility = Visibility.Visible;
        panel.Loaded += (_, _) => NewDeviceCard.BringIntoView();
    }

    void CloseAddDevice()
    {
        if (_addPanel is null)
        {
            return;
        }
        NewDeviceHost.Content = null; // Unloaded: the panel stops its scan
        NewDeviceCard.Visibility = Visibility.Collapsed;
        _addPanel = null;
    }

    void AddPanel_Saved(Device device)
    {
        App.Settings.Devices.Add(device);
        App.SaveSettings();
        CloseAddDevice();
        BuildDeviceList();
        DeviceSaved?.Invoke(device, true);
        Message?.Invoke($"Kaydedildi: {device.Name}");
        ProbeDevices(device);
    }

    /// <summary>The top bar changed a setting that this view also shows.</summary>
    public void RefreshAppSettings() => LoadAppSettings();

    // ---- Navigation ----

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (DevicesPage is not null) // also raised while InitializeComponent runs
        {
            ShowPage(Page);
        }
    }

    void ShowPage(SettingsPage page)
    {
        DevicesPage.Visibility = page == SettingsPage.Devices ? Visibility.Visible : Visibility.Collapsed;
        AppPage.Visibility = page == SettingsPage.App ? Visibility.Visible : Visibility.Collapsed;
        var recorder = page == SettingsPage.Recorder;
        if (recorder && RecorderPage.Visibility != Visibility.Visible)
        {
            RecorderPage.Visibility = Visibility.Visible;
            RecorderPage.Open(); // reads only; nothing is written on open
        }
        else if (!recorder && RecorderPage.Visibility == Visibility.Visible)
        {
            RecorderPage.Visibility = Visibility.Collapsed;
            RecorderPage.Close();
        }
    }

    // ---- Device list ----

    static Grid ListGrid()
    {
        var grid = new Grid();
        foreach (var width in ListColumns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        return grid;
    }

    static TextBlock Cell(string text, int column, string style = "CellText")
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(StyleProperty, style);
        Grid.SetColumn(block, column);
        return block;
    }

    void BuildListHeader()
    {
        var grid = ListGrid();
        grid.Children.Add(Cell("DURUM", 2, "CaptionText"));
        grid.Children.Add(Cell("CİHAZ ADI", 4, "CaptionText"));
        grid.Children.Add(Cell("ADRES", 6, "CaptionText"));
        grid.Children.Add(Cell("TÜR", 8, "CaptionText"));
        grid.Children.Add(Cell("KAMERA", 10, "CaptionText"));
        foreach (TextBlock cell in grid.Children)
        {
            cell.VerticalAlignment = VerticalAlignment.Center;
        }
        ListHeader.Child = grid;
    }

    void BuildDeviceList()
    {
        DeviceRows.Children.Clear();
        _blocks.Clear();
        var devices = App.Settings.Devices.ToList();
        if (devices.Count == 0)
        {
            var empty = Cell("Henüz cihaz yok. Sağ üstteki “Cihaz ekle” ile ekleyin.", 0);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            DeviceRows.Children.Add(new Border { Height = 44, Padding = new Thickness(16, 0, 16, 0), Child = empty });
            return;
        }
        for (var i = 0; i < devices.Count; i++)
        {
            var block = DeviceRow(devices[i], last: i == devices.Count - 1);
            _blocks.Add(block);
            DeviceRows.Children.Add(block.Container);
        }
    }

    DeviceBlock DeviceRow(Device device, bool last)
    {
        var grid = ListGrid();

        var rotate = new RotateTransform(0);
        var chevron = new GlyphIcon
        {
            Data = (Geometry)FindResource("IconChevronRight"),
            Size = 14,
            RenderTransform = rotate,
            RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        chevron.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSecondary");
        grid.Children.Add(chevron);

        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Margin = new Thickness(0, 0, 6, 0) };
        dot.SetResourceReference(StyleProperty, "StatusDot");
        var statusText = Cell("", 0);
        status.Children.Add(dot);
        status.Children.Add(statusText);
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        var name = Cell(device.Name, 4);
        name.FontWeight = FontWeights.SemiBold;
        name.FontSize = (double)FindResource("FontSizeList");
        name.ToolTip = device.Name;
        grid.Children.Add(name);

        var address = Cell(device.Host, 6);
        grid.Children.Add(address);
        var type = Cell("", 8);
        grid.Children.Add(type);
        var count = Cell("", 10);
        grid.Children.Add(count);

        var row = new ClickToggleButton { Content = grid };
        row.SetResourceReference(StyleProperty, "ListRowToggle");
        System.Windows.Automation.AutomationProperties.SetName(row, $"{device.Name}, {device.Host}");

        var body = new StackPanel();
        body.Children.Add(row);
        var container = new Border { Child = body, BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1) };
        container.SetResourceReference(Border.BorderBrushProperty, "Divider");

        var block = new DeviceBlock
        {
            Device = device,
            Container = container,
            Row = row,
            Chevron = rotate,
            Dot = dot,
            Status = statusText,
            Type = type,
            Count = count,
            Body = body,
        };
        row.Click += (_, _) => Toggle(block);
        row.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                row.IsChecked = row.IsChecked != true;
                Toggle(block);
                e.Handled = true;
            }
        };
        UpdateRow(block);
        return block;
    }

    void UpdateRow(DeviceBlock block)
    {
        var device = block.Device;
        var live =LiveStateOf?.Invoke(device.Id);
        var reach = _reach.GetValueOrDefault(device.Id, Reach.Probing);
        if (live == TileState.Playing || reach == Reach.Online)
        {
            SetStatus(block, "Online", "Bağlı");
        }
        else if (reach == Reach.Probing || live == TileState.Connecting)
        {
            SetStatus(block, "Warning", "Bağlanıyor");
        }
        else
        {
            SetStatus(block, "Offline", "Çevrimdışı");
        }
        block.Type.Text = DeviceKind.Describe(device);
        block.Type.ToolTip = device.Model is { Length: > 0 } model ? $"{block.Type.Text} · {model}" : null;
        block.Count.Text = string.Create(CultureInfo.InvariantCulture, $"{device.ListedCameras().Count()} / {device.Cameras.Count}");
        block.Count.ToolTip = "Listede görünen / toplam kamera";
    }

    static void SetStatus(DeviceBlock block, string dotKey, string text)
    {
        block.Dot.SetResourceReference(Shape.FillProperty, dotKey);
        block.Status.Text = text;
    }

    void Toggle(DeviceBlock block)
    {
        if (block.Row.IsChecked == true)
        {
            Expand(block);
        }
        else
        {
            Collapse(block);
        }
    }

    /// <summary>Accordion: one editor at a time; opening a row drops the other draft and an unsaved "Yeni cihaz" form.</summary>
    void Expand(DeviceBlock block)
    {
        CloseEditor();
        CloseAddDevice();
        var editor = new DeviceEditor(block.Device, isNew: false);
        editor.Saved += Editor_Saved;
        editor.Cancelled += _ => CancelEdit();
        editor.RemoveRequested += Editor_RemoveRequested;
        _editor = editor;
        block.Body.Children.Add(editor);
        block.Row.IsChecked = true;
        block.Chevron.Angle = 90;
    }

    void Collapse(DeviceBlock block)
    {
        if (_editor is not null && block.Body.Children.Contains(_editor))
        {
            CloseEditor();
        }
        block.Row.IsChecked = false;
        block.Chevron.Angle = 0;
    }

    void CloseEditor()
    {
        if (_editor is null)
        {
            return;
        }
        foreach (var block in _blocks.Where(b => b.Body.Children.Contains(_editor)))
        {
            block.Body.Children.Remove(_editor);
            block.Row.IsChecked = false;
            block.Chevron.Angle = 0;
        }
        _editor = null;
    }

    void CancelEdit() => CloseEditor();

    void Editor_Saved(DeviceEditor editor, bool loginChanged)
    {
        var device = editor.Device;
        App.SaveSettings();
        CloseEditor();
        BuildDeviceList();
        DeviceSaved?.Invoke(device, loginChanged);
        Message?.Invoke($"Kaydedildi: {device.Name}");
        ProbeDevices(device);
    }

    /// <summary>
    /// Cihaz ayarları changed the recorder's address or SPVision's account password on the recorder: save the device
    /// (password DPAPI-protected by <see cref="SettingsStore"/>) and reconnect, like Kaydet in the device editor.
    /// </summary>
    void RecorderPage_DeviceUpdated(Device device, bool loginChanged)
    {
        CloseEditor(); // an open editor holds a draft of the old values
        App.SaveSettings();
        BuildDeviceList();
        DeviceSaved?.Invoke(device, loginChanged);
        ProbeDevices(device);
    }

    void Editor_RemoveRequested(DeviceEditor editor)
    {
        var device = editor.Device;
        var answer = MessageBox.Show(Window.GetWindow(this)!,
            $"“{device.Name}” SPVision'dan kaldırılsın mı?\n\nKameraları canlı izlemeden ve listeden çıkar. Kayıt cihazında hiçbir şey değişmez.",
            "Cihazı kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        CloseEditor();
        App.Settings.Devices.Remove(device);
        App.SaveSettings();
        _reach.Remove(device.Id);
        BuildDeviceList();
        DeviceRemoved?.Invoke(device);
        Message?.Invoke($"Kaldırıldı: {device.Name}");
    }

    /// <summary>
    /// Reachability for the status column: one ISAPI GET (deviceInfo) per device. Also fills in the
    /// device type and, when an SDK session is open, the channel capacity for the TÜR column.
    /// </summary>
    async void ProbeDevices(Device? only = null)
    {
        if (only is null)
        {
            _probeCts?.Cancel();
            _probeCts = new CancellationTokenSource();
        }
        var cts = _probeCts ??= new CancellationTokenSource();
        var devices = only is null ? App.Settings.Devices.ToList() : [only];
        foreach (var device in devices)
        {
            _reach[device.Id] = Reach.Probing;
            UpdateBlockOf(device);
        }
        var changed = false;
        await Task.WhenAll(devices.Select(async device =>
        {
            DeviceDetails? details = null;
            try
            {
                using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(5));
                details = await isapi.GetDeviceInfoAsync(cts.Token);
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                return;
            }
            if (cts.IsCancellationRequested)
            {
                return;
            }
            _reach[device.Id] = details is null ? Reach.Offline : Reach.Online;
            if (details?.DeviceType is { Length: > 0 } type && type != device.DeviceType)
            {
                device.DeviceType = type;
                changed = true;
            }
            if (ChannelCapacity(device) is { } channels && channels != device.ChannelCount)
            {
                device.ChannelCount = channels;
                changed = true;
            }
            UpdateBlockOf(device);
        }));
        if (changed && !cts.IsCancellationRequested)
        {
            App.SaveSettings();
        }
    }

    /// <summary>IP channel capacity from an open SDK login (e.g. 32 on a DS-8632NI); null without a session.</summary>
    static int? ChannelCapacity(Device device)
    {
        if (HikSession.Find(device.Id) is not { IsLoggedIn: true } session)
        {
            return null;
        }
        var info = session.DeviceInfo.DeviceV30;
        var ip = info.IpChanNum + 256 * info.HighDChanNum;
        var total = ip > 0 ? ip : info.ChanNum;
        return total > 0 ? total : null;
    }

    void UpdateBlockOf(Device device)
    {
        if (_blocks.FirstOrDefault(b => b.Device == device) is { } block)
        {
            UpdateRow(block);
        }
    }

    void AddDevice_Click(object sender, RoutedEventArgs e) => BeginAddDevice();

    // ---- App settings ----

    void LoadAppSettings()
    {
        _loadingSwitches = true;
        try
        {
            var settings = App.Settings;
            ThemeLight.IsChecked = settings.Theme == AppTheme.Light;
            ThemeDark.IsChecked = settings.Theme == AppTheme.Dark;
            ThemeSystem.IsChecked = settings.Theme == AppTheme.System;
            MotionSwitch.IsChecked = settings.ShowMotionBoxes;
            HardwareDecodingSwitch.IsChecked = settings.HardwareDecoding;
            QualityAuto.IsChecked = settings.LiveQuality is null;
            QualityHd.IsChecked = settings.LiveQuality == StreamQuality.Main;
            QualitySd.IsChecked = settings.LiveQuality == StreamQuality.Sub;
            ShowFolder(SnapshotFolderText, settings.SnapshotFolder);
            ShowFolder(RecordingFolderText, settings.RecordingFolder);
            RestoreSwitch.IsChecked = settings.RestoreLastView;
            AutoStartSwitch.IsChecked = WindowsStartup.IsEnabled();
            FullScreenSwitch.IsChecked = settings.StartFullScreen;
        }
        finally
        {
            _loadingSwitches = false;
        }
        RefreshUpdates();
    }

    // ---- Güncellemeler card ----

    /// <summary>Version, last check, status and the offer's buttons, from <see cref="UpdateController"/>.</summary>
    void RefreshUpdates()
    {
        var phase = UpdateController.Phase;
        var offer = UpdateController.Offer;
        var downloading = phase == UpdatePhase.Downloading;
        UpdateVersionText.Text = $"Yüklü sürüm: SPVision {UpdateController.CurrentVersion}";
        UpdateLastCheckText.Text = phase == UpdatePhase.Disabled
            ? "Bu kopyada güncelleme denetimi kapalı."
            : UpdateText.LastCheck(UpdateController.LastCheckUtc) + " · kendiliğinden en çok 6 saatte bir denetlenir.";
        UpdateCheckButton.IsEnabled = phase != UpdatePhase.Disabled && !UpdateController.IsBusy;
        UpdateCheckButton.ToolTip = phase == UpdatePhase.Disabled
            ? UpdateController.Status
            : "Yeni SPVision sürümü olup olmadığını şimdi denetler; hiçbir şey indirmez";
        ToolTipService.SetShowOnDisabled(UpdateCheckButton, true);

        UpdateStatusText.Text = UpdateController.Status;
        UpdateStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            phase == UpdatePhase.Failed || phase == UpdatePhase.Disabled ? "WarningText" : "TextFg");
        UpdateDetailText.Text = UpdateController.Activity ?? (offer is null ? "" : UpdateText.FirstLine(offer.Notes));
        UpdateDetailText.SetResourceReference(TextBlock.ForegroundProperty, UpdateController.ActivityFailed ? "DangerText" : "TextSecondary");
        UpdateDetailText.Visibility = UpdateDetailText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var hasOffer = offer is not null && UpdateController.Kind != UpdateKind.None;
        UpdateNotesButton.Visibility = hasOffer ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallButton.Visibility = hasOffer ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Visibility = hasOffer && downloading ? Visibility.Visible : Visibility.Collapsed;
        if (downloading)
        {
            var percent = UpdateText.Percent(UpdateController.Progress.Received, UpdateController.Progress.Total);
            UpdateProgress.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
            UpdateProgress.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
        }
        UpdateInstallButton.Content = downloading ? "İptal" : "Güncelle";
        UpdateInstallButton.SetResourceReference(StyleProperty, downloading ? "SettingsButton" : "SettingsAccentButton");
        System.Windows.Automation.AutomationProperties.SetName(UpdateInstallButton, downloading ? "İndirmeyi iptal et" : "Güncelle");
        UpdateInstallButton.IsEnabled = phase is UpdatePhase.Available or UpdatePhase.Downloading;
    }

    async void UpdateCheck_Click(object sender, RoutedEventArgs e) => await UpdateController.CheckNowAsync();

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateController.Offer is { } offer && UpdateDialog.ShowNotes(Window.GetWindow(this), offer) && !UpdateController.IsBusy)
        {
            (Window.GetWindow(this) as MainWindow)?.InstallUpdate();
        }
    }

    void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateController.Phase == UpdatePhase.Downloading)
        {
            UpdateController.CancelDownload();
        }
        else
        {
            (Window.GetWindow(this) as MainWindow)?.InstallUpdate();
        }
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Theme = sender == ThemeLight ? AppTheme.Light : sender == ThemeDark ? AppTheme.Dark : AppTheme.System;
        App.SaveSettings();
        ThemeManager.Apply(); // instant
        LoadAppSettings();
    }

    // The switches act on Checked/Unchecked (mouse, keyboard and UI Automation alike); loading them changes nothing.

    void MotionSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingSwitches)
        {
            MotionBoxesChanged?.Invoke(MotionSwitch.IsChecked == true);
        }
    }

    void HardwareDecodingSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingSwitches)
        {
            HardwareDecodingChanged?.Invoke(HardwareDecodingSwitch.IsChecked == true);
        }
    }

    void Quality_Click(object sender, RoutedEventArgs e)
    {
        QualitySelected?.Invoke(sender == QualityHd ? StreamQuality.Main : sender == QualitySd ? StreamQuality.Sub : null);
        LoadAppSettings();
    }

    void SnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Anlık görüntü klasörü", App.Settings.SnapshotFolder) is { } folder)
        {
            App.Settings.SnapshotFolder = folder;
            App.SaveSettings();
            LoadAppSettings();
        }
    }

    void RecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("Video kayıt klasörü", App.Settings.RecordingFolder) is { } folder)
        {
            App.Settings.RecordingFolder = folder;
            App.SaveSettings();
            LoadAppSettings();
        }
    }

    string? PickFolder(string title, string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (System.IO.Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FolderName : null;
    }

    /// <summary>Known folders are shown by their Turkish name ("Resimler\SPVision"); the tool tip has the full path.</summary>
    static void ShowFolder(TextBlock target, string path)
    {
        var text = path;
        foreach (var (folder, name) in new[]
                 {
                     (Environment.SpecialFolder.MyPictures, "Resimler"),
                     (Environment.SpecialFolder.MyVideos, "Videolar"),
                     (Environment.SpecialFolder.MyDocuments, "Belgeler"),
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == root.Length || path[root.Length] == System.IO.Path.DirectorySeparatorChar))
            {
                text = name + path[root.Length..];
                break;
            }
        }
        target.Text = text;
        target.ToolTip = path;
    }

    void RestoreSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitches)
        {
            return;
        }
        App.Settings.RestoreLastView = RestoreSwitch.IsChecked == true;
        App.SaveSettings();
    }

    void AutoStartSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitches)
        {
            return;
        }
        try
        {
            WindowsStartup.Set(AutoStartSwitch.IsChecked == true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Message?.Invoke($"Başlangıç ayarı yazılamadı: {ex.Message}");
        }
        LoadAppSettings(); // the registry is the truth
    }

    void FullScreenSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitches)
        {
            return;
        }
        App.Settings.StartFullScreen = FullScreenSwitch.IsChecked == true;
        App.SaveSettings();
    }
}
