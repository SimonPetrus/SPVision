using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Edit panel of one device (Settings › Cihaz yönetimi, under the expanded row). Works on a draft
/// (<see cref="DeviceDraft"/>): nothing changes until Kaydet. Only ISAPI GET calls are made (device info, cameras,
/// stream key). Stored password / stream key are never shown; an empty box keeps the stored value.
/// </summary>
public partial class DeviceEditor : UserControl, IDeviceFormHost
{
    const string StoredPlaceholder = "Kayıtlı · değiştirmek için yazın";

    readonly DeviceDraft _draft;
    bool _busy;

    /// <summary>device: the saved device to edit, or a fresh one (isNew) that is added on Kaydet.</summary>
    public DeviceEditor(Device device, bool isNew)
    {
        InitializeComponent();
        _draft = new DeviceDraft(device, isNew);

        NameBox.Text = device.Name;
        HostBox.Text = device.Host;
        // A new device starts on HTTPS (443); a saved one shows exactly its stored values.
        HttpPortBox.Text = (isNew ? HttpsPort.Https : device.HttpPort).ToString(CultureInfo.InvariantCulture);
        RtspPortBox.Text = device.RtspPort.ToString(CultureInfo.InvariantCulture);
        SdkPortBox.Text = device.SdkPort.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = device.UserName;
        HttpsBox.IsChecked = isNew || device.UseHttps;
        // After the stored value is shown: from here on any change (mouse, keyboard, UI Automation) moves the port.
        HttpsBox.Checked += Https_Changed;
        HttpsBox.Unchecked += Https_Changed;
        UpdatePlaceholders();
        UpdateCertificate();

        // A new device has nothing to remove; keep the slot so the right-hand buttons do not move.
        RemoveButton.Visibility = isNew ? Visibility.Hidden : Visibility.Visible;
        ResultText.Text = isNew ? "IP adresi ve kullanıcı bilgilerini girip bağlantıyı test edin." : "";
        ResultText.ToolTip = ResultText.Text;
        BuildCameraTable();
        Loaded += (_, _) => (isNew ? HostBox : NameBox).Focus();
    }

    public Device Device => _draft.Device;

    public bool IsNew => _draft.IsNew;

    /// <summary>Kaydet: the device (same object for an existing one) and whether login data changed.</summary>
    public event Action<DeviceEditor, bool>? Saved;

    public event Action<DeviceEditor>? Cancelled;

    public event Action<DeviceEditor>? RemoveRequested;

    // ---- Form ----

    void Secret_Changed(object sender, RoutedEventArgs e) => UpdatePlaceholders();

    /// <summary>"HTTPS kullan" changed: a standard port follows it (80 ↔ 443); a custom port stays.</summary>
    void Https_Changed(object sender, RoutedEventArgs e)
    {
        HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, HttpsBox.IsChecked == true);
        UpdateCertificate();
    }

    /// <summary>"Sertifika parmak izi: AB:CD:… (ilk bağlantıda kaydedildi)"; full details in the tool tip.</summary>
    void UpdateCertificate()
    {
        var text = CertificateText.EditorLine(HttpsBox.IsChecked == true, _draft.Pin);
        CertificateLine.Text = text;
        CertificateLine.ToolTip = _draft.Pin is { } pin ? CertificateText.Details(pin) : text;
        System.Windows.Automation.AutomationProperties.SetName(CertificateLine, text);
        ResetCertificateButton.IsEnabled = !_busy && _draft.Pin is not null;
    }

    /// <summary>
    /// "Sertifikayı sıfırla" (short confirmation): forgets the pin at once (saved device included, settings saved), so the
    /// next HTTPS connection stores the device's current certificate again.
    /// </summary>
    void ResetCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _draft.Pin is null || !ConfirmDialog.Show(Window.GetWindow(this),
                "Kayıtlı sertifika parmak izi unutulsun mu? Bir sonraki HTTPS bağlantısında cihazın o anki sertifikası yeniden kaydedilir. " +
                "Bunu yalnızca cihazın sertifikasının neden değiştiğini biliyorsanız yapın.", "Sıfırla", "Vazgeç"))
        {
            return;
        }
        _draft.ResetPin();
        UpdateCertificate();
        ShowResult("Sertifika parmak izi silindi; bir sonraki bağlantıda yeniden kaydedilir.");
    }

    void UpdatePlaceholders()
    {
        var device = _draft.Device;
        PasswordPlaceholder.Text = !IsNew && PasswordBox.Password.Length == 0 && device.Password.Length > 0 ? StoredPlaceholder : "";
        StreamKeyPlaceholder.Text = !IsNew && StreamKeyBox.Password.Length == 0 && !string.IsNullOrEmpty(device.StreamKey) ? StoredPlaceholder : "";
    }

    /// <summary>Form values on a scratch device (for test calls and for saving); null with a message when invalid.</summary>
    public Device? ReadForm()
    {
        var form = _draft.Read(new DeviceFormInput(NameBox.Text, HostBox.Text, HttpPortBox.Text, RtspPortBox.Text, SdkPortBox.Text,
            HttpsBox.IsChecked == true, UserBox.Text, PasswordBox.Password, StreamKeyBox.Password), out var error);
        if (error is not null)
        {
            ShowResult(error);
        }
        return form;
    }

    public void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.ToolTip = text;
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        TestButton.IsEnabled = FetchKeyButton.IsEnabled = RefreshButton.IsEnabled = SaveButton.IsEnabled = RemoveButton.IsEnabled = !busy;
        ResetCertificateButton.IsEnabled = !busy && _draft.Pin is not null;
    }

    void IDeviceFormHost.SwitchToHttp()
    {
        HttpsBox.IsChecked = false;
        HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, useHttps: false);
    }

    void IDeviceFormHost.TakePin(Device form)
    {
        _draft.TakePin(form);
        UpdateCertificate();
    }

    // ---- Device reads (ISAPI GET only) ----

    async Task ReadDeviceAsync(string progress, Func<IsapiClient, Task> read)
    {
        if (!_busy)
        {
            await DeviceReads.RunAsync(this, Window.GetWindow(this), progress, read);
        }
    }

    async void Test_Click(object sender, RoutedEventArgs e) =>
        await ReadDeviceAsync("Bağlanılıyor…", async isapi =>
        {
            var details = await isapi.GetDeviceInfoAsync();
            _draft.Details = details;
            var cameras = await isapi.GetCamerasAsync();
            TakeCameras(cameras);
            ShowResult($"✓ {details.Model ?? details.Name} · {cameras.Count} kamera ({cameras.Count(c => c.Online)} çevrim içi)");
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = details.Name;
            }
        });

    async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await ReadDeviceAsync("Kameralar okunuyor…", async isapi =>
        {
            var cameras = await isapi.GetCamerasAsync();
            TakeCameras(cameras);
            ShowResult($"✓ {cameras.Count} kamera ({cameras.Count(c => c.Online)} çevrim içi). Kaydet ile saklanır.");
        });

    async void FetchKey_Click(object sender, RoutedEventArgs e) =>
        await ReadDeviceAsync("Akış anahtarı okunuyor…", async isapi =>
        {
            var enc = await isapi.GetStreamEncryptionAsync();
            if (enc.VerificationCode is { Length: > 0 } code)
            {
                StreamKeyBox.Password = code;
                ShowResult(enc.Enabled
                    ? "✓ Akış anahtarı cihazdan okundu."
                    : "✓ Anahtar okundu; cihazda akış şifreleme kapalı (anahtar gerekmez).");
            }
            else
            {
                ShowResult(enc.Enabled
                    ? "✗ Anahtar cihazdan okunamadı; Hik-Connect doğrulama kodunu elle girin."
                    : "Cihazda akış şifreleme kapalı; anahtar gerekmiyor.");
            }
        });

    /// <summary>Fetched list replaces the draft; per-camera keys and "listede göster" choices are kept.</summary>
    void TakeCameras(List<Camera> fetched)
    {
        _draft.TakeCameras(fetched);
        BuildCameraTable();
    }

    // ---- Save / cancel / remove ----

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadForm() is not { } form)
        {
            return;
        }
        if (_draft.Cameras.Count == 0 &&
            MessageBox.Show(Window.GetWindow(this)!, "Kamera listesi alınmadı. Yine de kaydedilsin mi?", "SPVision",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        var loginChanged = _draft.Save(form);
        Saved?.Invoke(this, loginChanged);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this);

    void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);

    // ---- Camera table ----

    /// <summary>Columns KANAL 64 | KAMERA ADI * | DURUM 160 | LİSTEDE GÖSTER 136, 16 px gaps and side insets.</summary>
    static Grid TableGrid()
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(64), new GridLength(16), new GridLength(1, GridUnitType.Star), new GridLength(16),
                     new GridLength(160), new GridLength(16), new GridLength(136) })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        return grid;
    }

    static TextBlock Cell(string text, int column, string style = "CellText")
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, style);
        Grid.SetColumn(block, column);
        return block;
    }

    void BuildCameraTable()
    {
        CameraTable.Children.Clear();

        var header = TableGrid();
        header.Children.Add(Cell("KANAL", 0, "CaptionText"));
        header.Children.Add(Cell("KAMERA ADI", 2, "CaptionText"));
        header.Children.Add(Cell("DURUM", 4, "CaptionText"));
        var listed = Cell("LİSTEDE GÖSTER", 6, "CaptionText");
        listed.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(listed);
        foreach (TextBlock t in header.Children)
        {
            t.VerticalAlignment = VerticalAlignment.Center;
        }
        var headerBorder = new Border
        {
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Child = header,
        };
        headerBorder.SetResourceReference(Border.BackgroundProperty, "SubtleBg");
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        CameraTable.Children.Add(headerBorder);

        var rows = new StackPanel();
        if (_draft.Cameras.Count == 0)
        {
            var empty = Cell("Kamera listesi yok: “Bağlantıyı test et” ya da “Kameraları yenile” ile cihazdan okuyun.", 0);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            rows.Children.Add(new Border { Height = 36, Padding = new Thickness(16, 0, 16, 0), Child = empty });
        }
        for (var i = 0; i < _draft.Cameras.Count; i++)
        {
            rows.Children.Add(CameraRow(_draft.Cameras[i], last: i == _draft.Cameras.Count - 1));
        }

        // About 7 rows, then the table scrolls; at its ends the wheel moves the page instead.
        var scroller = new ScrollViewer
        {
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rows,
            Focusable = false,
        };
        scroller.PreviewMouseWheel += ForwardWheelAtEnds;
        CameraTable.Children.Add(scroller);
    }

    Border CameraRow(Camera camera, bool last)
    {
        var grid = TableGrid();
        var channel = Cell(camera.Channel.ToString(CultureInfo.InvariantCulture), 0);
        channel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        grid.Children.Add(channel);
        var name = Cell(camera.Name, 2);
        name.FontWeight = FontWeights.Medium;
        name.ToolTip = camera.Name;
        grid.Children.Add(name);

        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse { Margin = new Thickness(0, 0, 6, 0) };
        dot.SetResourceReference(StyleProperty, "StatusDot");
        dot.SetResourceReference(Shape.FillProperty, camera.Online ? "Online" : "Offline");
        status.Children.Add(dot);
        status.Children.Add(Cell(camera.Online ? "Çevrim içi" : "Çevrimdışı", 0));
        Grid.SetColumn(status, 4);
        grid.Children.Add(status);

        var toggle = new CheckBox
        {
            IsChecked = !camera.Hidden,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        toggle.SetResourceReference(StyleProperty, "ToggleSwitch");
        System.Windows.Automation.AutomationProperties.SetName(toggle, $"{camera.Name} listede göster");
        Grid.SetColumn(toggle, 6);
        grid.Children.Add(toggle);

        var row = new Border
        {
            Height = 36,
            Padding = new Thickness(16, 0, 16, 0),
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
            Child = grid,
        };
        row.SetResourceReference(Border.BorderBrushProperty, "Divider");
        void Changed(object sender, RoutedEventArgs e)
        {
            camera.Hidden = toggle.IsChecked != true;
            // Dim the row but not the switch itself.
            name.Opacity = channel.Opacity = status.Opacity = camera.Hidden ? 0.55 : 1;
        }
        toggle.Checked += Changed; // not Click: UI Automation toggles without one
        toggle.Unchecked += Changed;
        name.Opacity = channel.Opacity = status.Opacity = camera.Hidden ? 0.55 : 1;
        return row;
    }

    static void ForwardWheelAtEnds(object sender, MouseWheelEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        var atTop = scroller.VerticalOffset <= 0;
        var atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight;
        if ((e.Delta > 0 && atTop) || (e.Delta < 0 && atBottom))
        {
            e.Handled = true;
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = scroller };
            (scroller.Parent as UIElement ?? scroller).RaiseEvent(forwarded);
        }
    }
}
