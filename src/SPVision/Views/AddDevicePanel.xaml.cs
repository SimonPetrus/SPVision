using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

public enum AddDeviceMode
{
    /// <summary>Settings › Cihaz yönetimi › Cihaz ekle: form column on the left, İptal / Kaydet (120 px).</summary>
    Settings,

    /// <summary>First-run screen: form column centred, Sonra ekle / Bağlan ve devam et (160 px); connecting tests, then saves.</summary>
    FirstRun,
}

/// <summary>
/// Adds a device: the "Çevrim içi cihazlar" list (Hikvision SADP <b>inquiry</b> only — read-only, see
/// <see cref="SadpScanner"/>) above a compact connection form. Picking a row fills IP, ports and HTTPS (and the name
/// when empty); the user types the user name and password. Same draft, validation, reads and Kaydet as the device editor
/// (<see cref="DeviceDraft"/>, <see cref="DeviceReads"/>, <see cref="DeviceForm"/>); only ISAPI GET calls are made.
/// One control for Settings and the first-run screen.
/// </summary>
public partial class AddDevicePanel : UserControl, IDeviceFormHost
{
    /// <summary>List columns: MODEL 210 | IP ADRESİ 150 | PORT (HTTP / SDK) 170 | YAZILIM * | DURUM 120.</summary>
    static readonly GridLength[] ListColumns =
    [
        new(210), new(150), new(170), new(1, GridUnitType.Star), new(120),
    ];

    const string StartHint = "Listeden bir cihaz seçin ya da bilgileri elle yazın; sonra bağlantıyı test edin.";
    const string PickedHint = "Kullanıcı adı ve şifreyi girip bağlantıyı test edin.";

    readonly AddDeviceMode _mode;
    DeviceDraft _draft = NewDraft();
    string _streamKey = "";        // read from the device by the test (never typed here); "" = none
    bool _busy;
    bool _closed;                  // cancelled / removed: a read still running must not save anything

    CancellationTokenSource? _scanCts;
    bool _scanning;
    IReadOnlyList<DiscoveredDevice> _found = [];
    string? _scanProblem;
    string? _pickedKey;
    string? _pickedHost;           // host written by the last pick (a new pick of another host starts a new draft)
    string? _nameFromPick;         // name written by the last pick (replaced by the next pick)
    readonly Storyboard _spin = new() { RepeatBehavior = RepeatBehavior.Forever };
    readonly Dictionary<string, (ClickToggleButton Row, TextBlock Model)> _rows = [];

    public AddDevicePanel(AddDeviceMode mode)
    {
        InitializeComponent();
        _mode = mode;
        BuildListHeader();

        var device = _draft.Device;
        HttpsBox.IsChecked = true; // a new device starts on HTTPS (443), as in the device editor
        HttpPortBox.Text = HttpsPort.Https.ToString(CultureInfo.InvariantCulture);
        RtspPortBox.Text = device.RtspPort.ToString(CultureInfo.InvariantCulture);
        SdkPortBox.Text = device.SdkPort.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = device.UserName;
        HttpsBox.Checked += Https_Changed; // after the initial value: from here on the port follows (80 ↔ 443)
        HttpsBox.Unchecked += Https_Changed;
        OnlyRecordersSwitch.Checked += (_, _) => RenderList();
        OnlyRecordersSwitch.Unchecked += (_, _) => RenderList();
        ShowResult(StartHint);

        if (mode == AddDeviceMode.FirstRun)
        {
            FormColumn.HorizontalAlignment = HorizontalAlignment.Center;
            ListHint.Text += " Kameralarınız kayıt cihazına bağlıysa yalnızca kayıt cihazını eklemeniz yeterli.";
            CancelButton.Content = "Sonra ekle";
            SaveButton.Content = "Bağlan ve devam et";
            CancelButton.Width = SaveButton.Width = 160;
            SaveButton.ToolTip = "Bağlantıyı test eder, cihazı kaydeder ve canlı izlemeye geçer";
        }
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, (string)CancelButton.Content);
        System.Windows.Automation.AutomationProperties.SetName(SaveButton, (string)SaveButton.Content);

        foreach (var icon in new[] { RescanIcon, EmptySpinner })
        {
            var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)));
            Storyboard.SetTarget(animation, icon);
            Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _spin.Children.Add(animation);
        }

        Loaded += (_, _) =>
        {
            _closed = false;
            HostBox.Focus();
            Scan(); // a scan starts as soon as the form opens
        };
        Unloaded += (_, _) =>
        {
            _closed = true;
            _scanCts?.Cancel();
            _spin.Stop(this);
        };
    }

    /// <summary>
    /// Kaydet (Settings) / Bağlan ve devam et (first run): the new device, already filled in (not yet in the settings —
    /// the owner adds and saves it).
    /// </summary>
    public event Action<Device>? Saved;

    /// <summary>İptal (Settings) / Sonra ekle (first run).</summary>
    public event Action? Cancelled;

    static DeviceDraft NewDraft() => new(new Device(), isNew: true);

    // ---- Discovery list ----

    void BuildListHeader()
    {
        var grid = ListGrid();
        grid.Children.Add(Cell("MODEL", 0, "CaptionText"));
        grid.Children.Add(Cell("IP ADRESİ", 1, "CaptionText"));
        grid.Children.Add(Cell("PORT (HTTP / SDK)", 2, "CaptionText"));
        grid.Children.Add(Cell("YAZILIM", 3, "CaptionText"));
        var status = Cell("DURUM", 4, "CaptionText");
        status.HorizontalAlignment = HorizontalAlignment.Center;
        status.Margin = new Thickness(0); // centred over the badges, like them
        grid.Children.Add(status);
        ListHeader.Child = grid;
    }

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
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        block.SetResourceReference(StyleProperty, style);
        Grid.SetColumn(block, column);
        return block;
    }

    void Rescan_Click(object sender, RoutedEventArgs e) => Scan();

    /// <summary>One SADP inquiry on every interface, off the UI thread; a new scan supersedes a running one.</summary>
    async void Scan()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        _scanning = true;
        RenderList();
        SadpScanResult result;
        try
        {
            result = await Task.Run(() => SadpScanner.ScanAsync(SadpScanner.DefaultListen, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return; // superseded or closed
        }
        catch (Exception ex) // e.g. no socket could be made: the list stays empty, the address can be typed
        {
            result = new SadpScanResult([], 0, ex.Message);
        }
        if (cts != _scanCts || _closed)
        {
            return;
        }
        _found = result.Devices;
        _scanProblem = result.Problem;
        _scanning = false;
        RenderList();
    }

    void RenderList()
    {
        var visible = DiscoveryRules.Visible(_found, OnlyRecordersSwitch.IsChecked == true);
        CountRun.Text = " · " + DiscoveryRules.CountText(_scanning, visible.Count);
        RescanButton.IsEnabled = !_scanning;
        if (_scanning)
        {
            _spin.Begin(this, isControllable: true);
        }
        else
        {
            _spin.Stop(this);
            RescanSpin.Angle = EmptySpin.Angle = 0;
        }

        Rows.Children.Clear();
        _rows.Clear();
        if (!_scanning)
        {
            var saved = App.Settings.Devices.Select(d => d.Host).ToList();
            foreach (var device in visible)
            {
                Rows.Children.Add(Row(device, DiscoveryRules.Status(device, saved)));
            }
        }
        var empty = _scanning || visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RowsScroller.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptySpinner.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _scanning ? DiscoveryRules.Scanning
            : _found.Count > 0 ? DiscoveryRules.NoRecorder
            : DiscoveryRules.NotFound;
        EmptyText.ToolTip = !_scanning && _found.Count == 0 && _scanProblem is not null ? _scanProblem : null;
    }

    ClickToggleButton Row(DiscoveredDevice device, DiscoveryStatus status)
    {
        var grid = ListGrid();

        var model = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = new GlyphIcon
        {
            Data = (Geometry)FindResource(device.IsRecorder ? "IconServer" : "IconVideo"),
            Size = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSecondary");
        model.Children.Add(icon);
        var modelText = Cell(DiscoveryRules.Model(device), 0);
        modelText.FontWeight = device.Key == _pickedKey ? FontWeights.SemiBold : FontWeights.Medium;
        modelText.ToolTip = DiscoveryRules.Model(device);
        modelText.MaxWidth = 210 - 14 - 8 - 12;
        model.Children.Add(modelText);
        grid.Children.Add(model);

        grid.Children.Add(Cell(device.Address, 1));
        var ports = Cell(DiscoveryRules.Ports(device), 2);
        ports.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        grid.Children.Add(ports);
        var software = Cell(DiscoveryRules.Software(device), 3);
        software.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        software.ToolTip = software.Text;
        grid.Children.Add(software);

        var badgeText = new TextBlock
        {
            Text = DiscoveryRules.Badge(status),
            FontSize = (double)FindResource("FontSizeStatus"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Height = 22,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = badgeText,
        };
        var (background, foreground) = status switch
        {
            DiscoveryStatus.Added => ("RangeBg", "TextFg"),
            DiscoveryStatus.Inactive => ("WarningSoftBg", "WarningText"),
            _ => ("OnlineSoftBg", "Online"),
        };
        badge.SetResourceReference(Border.BackgroundProperty, background);
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        Grid.SetColumn(badge, 4);
        grid.Children.Add(badge);

        var row = new ClickToggleButton { Content = grid, IsChecked = device.Key == _pickedKey };
        row.SetResourceReference(StyleProperty, "DiscoveryRow");
        System.Windows.Automation.AutomationProperties.SetName(row,
            $"{DiscoveryRules.Model(device)}, {device.Address}, {DiscoveryRules.Badge(status)}");
        System.Windows.Automation.AutomationProperties.SetHelpText(row, "Seçince adres ve portlar forma yazılır");
        row.Click += (_, _) => Pick(device, status);
        _rows[device.Key] = (row, modelText);
        return row;
    }

    /// <summary>At the ends of the list the wheel scrolls the page instead (as in the device editor's camera table).</summary>
    void Rows_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var atTop = RowsScroller.VerticalOffset <= 0;
        var atBottom = RowsScroller.VerticalOffset >= RowsScroller.ScrollableHeight;
        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = RowsScroller };
            RaiseEvent(forwarded);
        }
    }

    /// <summary>A row picked: address, ports and HTTPS go into the form (name too when empty); the user types the login.</summary>
    void Pick(DiscoveredDevice device, DiscoveryStatus status)
    {
        if (_busy)
        {
            ShowPicked(); // keep the old pick shown
            return;
        }
        _pickedKey = device.Key;
        var fill = DiscoveryRules.Fill(device);
        if (_pickedHost is not null && _pickedHost != fill.Host)
        {
            // Another device: what was read from the previous one (cameras, key, certificate) does not belong to it.
            _draft = NewDraft();
            _streamKey = "";
        }
        _pickedHost = fill.Host;
        HostBox.Text = fill.Host;
        HttpsBox.IsChecked = fill.UseHttps; // moves a standard port; the reported one is written right after
        HttpPortBox.Text = fill.HttpPort.ToString(CultureInfo.InvariantCulture);
        RtspPortBox.Text = fill.RtspPort.ToString(CultureInfo.InvariantCulture);
        SdkPortBox.Text = fill.SdkPort.ToString(CultureInfo.InvariantCulture);
        if (fill.Model.Length > 0 && (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _nameFromPick))
        {
            NameBox.Text = _nameFromPick = fill.Model;
        }
        ShowPicked();

        switch (status)
        {
            case DiscoveryStatus.Inactive:
                ShowResult(DiscoveryRules.InactiveHint, warning: true);
                break;
            case DiscoveryStatus.Added:
                ShowResult(DiscoveryRules.AlreadyAdded, warning: true);
                break;
            default:
                ShowResult(PickedHint);
                break;
        }
        (PasswordBox.Password.Length == 0 ? (Control)PasswordBox : TestButton).Focus();
    }

    /// <summary>Only the picked row is checked (and its model semibold); clicking it again keeps it picked.</summary>
    void ShowPicked()
    {
        foreach (var (key, (row, model)) in _rows)
        {
            var picked = key == _pickedKey;
            row.IsChecked = picked;
            model.FontWeight = picked ? FontWeights.SemiBold : FontWeights.Medium;
        }
    }

    // ---- Form ----

    void Password_Changed(object sender, RoutedEventArgs e) =>
        PasswordPlaceholder.Visibility = PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Https_Changed(object sender, RoutedEventArgs e) =>
        HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, HttpsBox.IsChecked == true);

    public Device? ReadForm()
    {
        var form = _draft.Read(new DeviceFormInput(NameBox.Text, HostBox.Text, HttpPortBox.Text, RtspPortBox.Text, SdkPortBox.Text,
            HttpsBox.IsChecked == true, UserBox.Text, PasswordBox.Password, _streamKey), out var error);
        if (error is not null)
        {
            ShowResult(error, warning: true);
        }
        return form;
    }

    public void ShowResult(string text) => ShowResult(text, warning: false);

    void ShowResult(string text, bool warning)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningText" : "TextSecondary");
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        TestButton.IsEnabled = SaveButton.IsEnabled = !busy;
    }

    void IDeviceFormHost.SwitchToHttp()
    {
        HttpsBox.IsChecked = false;
        HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, useHttps: false);
    }

    void IDeviceFormHost.TakePin(Device form) => _draft.TakePin(form);

    // ---- Test / save (ISAPI GET only) ----

    /// <summary>
    /// Device info, camera list and — so live view can open an encrypted stream right away, as "Cihazdan oku" does in the
    /// device editor — the stream key (GET ISAPI/System/Network/EZVIZ; a device without it is fine). True when the
    /// device answered.
    /// </summary>
    async Task<bool> TestAsync()
    {
        if (_busy)
        {
            return false;
        }
        return await DeviceReads.RunAsync(this, Window.GetWindow(this), "Bağlanılıyor…", async isapi =>
        {
            var details = await isapi.GetDeviceInfoAsync();
            _draft.Details = details;
            var cameras = await isapi.GetCamerasAsync();
            _draft.TakeCameras(cameras);
            var keyNote = "";
            try
            {
                var encryption = await isapi.GetStreamEncryptionAsync();
                if (encryption.Enabled && encryption.VerificationCode is { Length: > 0 } code)
                {
                    _streamKey = code;
                    keyNote = " · akış anahtarı okundu";
                }
                else if (encryption.Enabled)
                {
                    keyNote = " · akış anahtarı okunamadı (Cihaz yönetimi'nden girilebilir)";
                }
            }
            catch (Exception)
            {
                // Not every device has the EZVIZ document; the key can still be typed in the device editor.
            }
            ShowResult($"✓ {details.Model ?? details.Name} · {cameras.Count} kamera ({cameras.Count(c => c.Online)} çevrim içi){keyNote}");
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = details.Name;
            }
        });
    }

    async void Test_Click(object sender, RoutedEventArgs e) => await TestAsync();

    /// <summary>
    /// Settings: Kaydet, exactly like the device editor's (asks when no camera list was read). First run: tests the
    /// connection first and saves only when the device answered.
    /// </summary>
    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        if (_mode == AddDeviceMode.FirstRun && (!await TestAsync() || _closed))
        {
            return;
        }
        if (ReadForm() is not { } form)
        {
            return;
        }
        if (_draft.Cameras.Count == 0 &&
            MessageBox.Show(Window.GetWindow(this)!, "Kamera listesi alınmadı. Yine de kaydedilsin mi?", "SPVision",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _draft.Save(form); // a new device: login data always "changed"
        Saved?.Invoke(_draft.Device);
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _closed = true;
        _scanCts?.Cancel();
        Cancelled?.Invoke();
    }
}
