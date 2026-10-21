using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Edit panel of one device (Settings › Cihaz yönetimi, under the expanded row). Works on a draft:
/// nothing changes until Kaydet. Only ISAPI GET calls are made (device info, cameras, stream key).
/// Stored password / stream key are never shown; an empty box keeps the stored value.
/// </summary>
public partial class DeviceEditor : UserControl
{
    const string StoredPlaceholder = "Kayıtlı · değiştirmek için yazın";

    readonly Device _device;
    readonly bool _isNew;
    List<Camera> _cameras;       // draft copies (name, online, hidden, key)
    DeviceDetails? _details;
    bool _busy;

    /// <summary>device: the saved device to edit, or a fresh one (isNew) that is added on Kaydet.</summary>
    public DeviceEditor(Device device, bool isNew)
    {
        InitializeComponent();
        _isNew = isNew;
        _device = device;
        _cameras = _device.Cameras.Select(Copy).ToList();

        NameBox.Text = _device.Name;
        HostBox.Text = _device.Host;
        // A new device starts on HTTPS (443); a saved one shows exactly its stored values.
        HttpPortBox.Text = (_isNew ? HttpsPort.Https : _device.HttpPort).ToString(CultureInfo.InvariantCulture);
        RtspPortBox.Text = _device.RtspPort.ToString(CultureInfo.InvariantCulture);
        SdkPortBox.Text = _device.SdkPort.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = _device.UserName;
        HttpsBox.IsChecked = _isNew || _device.UseHttps;
        // After the stored value is shown: from here on any change (mouse, keyboard, UI Automation) moves the port.
        HttpsBox.Checked += Https_Changed;
        HttpsBox.Unchecked += Https_Changed;
        UpdatePlaceholders();

        // A new device has nothing to remove; keep the slot so the right-hand buttons do not move.
        RemoveButton.Visibility = _isNew ? Visibility.Hidden : Visibility.Visible;
        ResultText.Text = _isNew ? "IP adresi ve kullanıcı bilgilerini girip bağlantıyı test edin." : "";
        ResultText.ToolTip = ResultText.Text;
        BuildCameraTable();
        Loaded += (_, _) => (_isNew ? HostBox : NameBox).Focus();
    }

    public Device Device => _device;

    public bool IsNew => _isNew;

    /// <summary>Kaydet: the device (same object for an existing one) and whether login data changed.</summary>
    public event Action<DeviceEditor, bool>? Saved;

    public event Action<DeviceEditor>? Cancelled;

    public event Action<DeviceEditor>? RemoveRequested;

    // ---- Form ----

    void Secret_Changed(object sender, RoutedEventArgs e) => UpdatePlaceholders();

    /// <summary>"HTTPS kullan" changed: a standard port follows it (80 ↔ 443); a custom port stays.</summary>
    void Https_Changed(object sender, RoutedEventArgs e) => HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, HttpsBox.IsChecked == true);

    void UpdatePlaceholders()
    {
        PasswordPlaceholder.Text = !_isNew && PasswordBox.Password.Length == 0 && _device.Password.Length > 0 ? StoredPlaceholder : "";
        StreamKeyPlaceholder.Text = !_isNew && StreamKeyBox.Password.Length == 0 && !string.IsNullOrEmpty(_device.StreamKey) ? StoredPlaceholder : "";
    }

    /// <summary>Form values on a scratch device (for test calls and for saving); null with a message when invalid.</summary>
    Device? ReadForm()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ShowResult("IP adresi gerekli.");
            return null;
        }
        if (!TryPort(HttpPortBox.Text, out var httpPort) || !TryPort(RtspPortBox.Text, out var rtspPort) ||
            !TryPort(SdkPortBox.Text, out var sdkPort))
        {
            ShowResult("Port numaraları geçersiz (1-65535).");
            return null;
        }
        return new Device
        {
            Id = _device.Id,
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            HttpPort = httpPort,
            RtspPort = rtspPort,
            SdkPort = sdkPort,
            UseHttps = HttpsBox.IsChecked == true,
            UserName = UserBox.Text.Trim(),
            // Empty box = keep what is stored (the stored value is never put into the box).
            Password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : _device.Password,
            StreamKey = StreamKeyBox.Password.Length > 0 ? StreamKeyBox.Password : _device.StreamKey,
        };
    }

    static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.ToolTip = text;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        TestButton.IsEnabled = FetchKeyButton.IsEnabled = RefreshButton.IsEnabled = SaveButton.IsEnabled = RemoveButton.IsEnabled = !busy;
    }

    // ---- Device reads (ISAPI GET only) ----

    /// <summary>
    /// Runs one device read with the form values. When HTTPS was on and the device could not be reached at all
    /// (TCP/TLS, not a 401 or other answer), asks before switching the form to HTTP and retries once; never silently.
    /// </summary>
    async Task ReadDeviceAsync(string progress, Func<IsapiClient, Task> read)
    {
        if (_busy || ReadForm() is not { } form)
        {
            return;
        }
        SetBusy(true);
        ShowResult(progress);
        IsapiClient? isapi = null;
        try
        {
            try
            {
                isapi = new IsapiClient(form);
                await read(isapi);
            }
            catch (IsapiException ex) when (HttpsPort.OfferHttpFallback(form.UseHttps, isapi?.Answered == true, ex))
            {
                ShowResult($"✗ {ex.Message}");
                if (!ConfirmDialog.Show(Window.GetWindow(this), "Bu cihazda HTTPS kapalı olabilir. HTTP (port 80) ile denensin mi?",
                        "HTTP ile dene", "İptal"))
                {
                    return;
                }
                HttpsBox.IsChecked = false;
                HttpPortBox.Text = HttpsPort.Follow(HttpPortBox.Text, useHttps: false);
                if (ReadForm() is not { } http)
                {
                    return;
                }
                ShowResult(progress);
                isapi?.Dispose();
                isapi = new IsapiClient(http);
                await read(isapi);
            }
        }
        catch (Exception ex)
        {
            ShowResult($"✗ {ex.Message}");
        }
        finally
        {
            isapi?.Dispose();
            SetBusy(false);
        }
    }

    async void Test_Click(object sender, RoutedEventArgs e) =>
        await ReadDeviceAsync("Bağlanılıyor…", async isapi =>
        {
            _details = await isapi.GetDeviceInfoAsync();
            var cameras = await isapi.GetCamerasAsync();
            TakeCameras(cameras);
            ShowResult($"✓ {_details.Model ?? _details.Name} · {cameras.Count} kamera ({cameras.Count(c => c.Online)} çevrim içi)");
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = _details.Name;
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
        _cameras = CameraList.Merge(_cameras, fetched);
        BuildCameraTable();
    }

    // ---- Save / cancel / remove ----

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ReadForm() is not { } form)
        {
            return;
        }
        if (_cameras.Count == 0 &&
            MessageBox.Show(Window.GetWindow(this)!, "Kamera listesi alınmadı. Yine de kaydedilsin mi?", "SPVision",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        var loginChanged = _isNew ||
            form.Host != _device.Host || form.HttpPort != _device.HttpPort || form.RtspPort != _device.RtspPort ||
            form.SdkPort != _device.SdkPort || form.UseHttps != _device.UseHttps || form.UserName != _device.UserName ||
            form.Password != _device.Password || form.StreamKey != _device.StreamKey;

        _device.Name = string.IsNullOrWhiteSpace(form.Name) ? form.Host : form.Name;
        _device.Host = form.Host;
        _device.HttpPort = form.HttpPort;
        _device.RtspPort = form.RtspPort;
        _device.SdkPort = form.SdkPort;
        _device.UseHttps = form.UseHttps;
        _device.UserName = form.UserName;
        _device.Password = form.Password;
        _device.StreamKey = form.StreamKey;
        if (_details is not null)
        {
            _device.Model = _details.Model;
            _device.SerialNumber = _details.SerialNumber;
            _device.DeviceType = _details.DeviceType ?? _device.DeviceType;
        }

        // Keep the saved camera objects (open tiles point at them); take name, state and choices from the draft.
        var saved = _device.Cameras.ToDictionary(c => c.Channel);
        _device.Cameras = _cameras.Select(draft =>
        {
            var camera = saved.TryGetValue(draft.Channel, out var old) ? old : new Camera { Channel = draft.Channel };
            camera.Name = draft.Name;
            camera.Online = draft.Online;
            camera.Hidden = draft.Hidden;
            camera.StreamKey = draft.StreamKey;
            return camera;
        }).ToList();

        Saved?.Invoke(this, loginChanged);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this);

    void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);

    // ---- Camera table ----

    static Camera Copy(Camera c) => new()
    {
        Channel = c.Channel,
        Name = c.Name,
        Online = c.Online,
        Hidden = c.Hidden,
        StreamKey = c.StreamKey,
    };

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
        if (_cameras.Count == 0)
        {
            var empty = Cell("Kamera listesi yok: “Bağlantıyı test et” ya da “Kameraları yenile” ile cihazdan okuyun.", 0);
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            rows.Children.Add(new Border { Height = 36, Padding = new Thickness(16, 0, 16, 0), Child = empty });
        }
        for (var i = 0; i < _cameras.Count; i++)
        {
            rows.Children.Add(CameraRow(_cameras[i], last: i == _cameras.Count - 1));
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
