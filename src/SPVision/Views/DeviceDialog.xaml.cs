using System.Windows;
using SPVision.Core;

namespace SPVision.Views;

public partial class DeviceDialog : Window
{
    readonly Device _device;
    List<Camera>? _fetchedCameras;
    DeviceDetails? _details;

    /// <summary>Edits a copy; <see cref="Result"/> holds the saved device.</summary>
    public DeviceDialog(Device? device)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        _device = device ?? new Device();
        Title = device is null ? "Cihaz ekle" : "Cihazı düzenle";
        NameBox.Text = _device.Name;
        HostBox.Text = _device.Host;
        HttpPortBox.Text = _device.HttpPort.ToString();
        RtspPortBox.Text = _device.RtspPort.ToString();
        HttpsBox.IsChecked = _device.UseHttps;
        UserBox.Text = _device.UserName;
        PasswordBox.Password = _device.Password;
        StreamKeyBox.Password = _device.StreamKey ?? "";
        if (device is not null)
        {
            ResultText.Text = $"{_device.Cameras.Count} kamera kayıtlı.";
        }
        Loaded += (_, _) => (string.IsNullOrEmpty(_device.Host) ? HostBox : NameBox).Focus();
    }

    public Device? Result { get; private set; }

    Device? ReadForm()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ResultText.Text = "IP adresi gerekli.";
            return null;
        }
        if (!int.TryParse(HttpPortBox.Text, out var httpPort) || !int.TryParse(RtspPortBox.Text, out var rtspPort))
        {
            ResultText.Text = "Port numaraları geçersiz.";
            return null;
        }
        return new Device
        {
            Id = _device.Id,
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            HttpPort = httpPort,
            RtspPort = rtspPort,
            UseHttps = HttpsBox.IsChecked == true,
            UserName = UserBox.Text.Trim(),
            Password = PasswordBox.Password,
            StreamKey = string.IsNullOrEmpty(StreamKeyBox.Password) ? null : StreamKeyBox.Password,
            Model = _device.Model,
            SerialNumber = _device.SerialNumber,
            Cameras = _device.Cameras,
        };
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } device)
        {
            return;
        }
        IsEnabled = false;
        ResultText.Text = "Bağlanılıyor…";
        try
        {
            using var isapi = new IsapiClient(device);
            _details = await isapi.GetDeviceInfoAsync();
            _fetchedCameras = await isapi.GetCamerasAsync();
            var online = _fetchedCameras.Count(c => c.Online);
            ResultText.Text = $"✓ {_details.Model ?? _details.Name} — {_fetchedCameras.Count} kamera ({online} çevrimiçi)";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = _details.Name;
            }
        }
        catch (IsapiException ex)
        {
            ResultText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    async void FetchKey_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } device)
        {
            return;
        }
        FetchKeyButton.IsEnabled = false;
        ResultText.Text = "Akış anahtarı okunuyor…";
        try
        {
            using var isapi = new IsapiClient(device);
            var enc = await isapi.GetStreamEncryptionAsync();
            if (enc.VerificationCode is { Length: > 0 } code)
            {
                StreamKeyBox.Password = code;
                ResultText.Text = enc.Enabled
                    ? "✓ Akış anahtarı cihazdan okundu."
                    : "✓ Anahtar okundu; cihazda akış şifreleme kapalı (anahtar gerekmez).";
            }
            else
            {
                ResultText.Text = enc.Enabled
                    ? "✗ Anahtar cihazdan okunamadı; Hik-Connect doğrulama kodunu elle girin."
                    : "Cihazda akış şifreleme kapalı; anahtar gerekmiyor.";
            }
        }
        catch (Exception ex)
        {
            ResultText.Text = $"✗ {ex.Message}";
        }
        finally
        {
            FetchKeyButton.IsEnabled = true;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } device)
        {
            return;
        }
        if (_fetchedCameras is not null)
        {
            // Keep keys the user already entered per camera.
            foreach (var camera in _fetchedCameras)
            {
                camera.StreamKey = _device.Cameras.FirstOrDefault(c => c.Channel == camera.Channel)?.StreamKey;
            }
            device.Cameras = _fetchedCameras;
        }
        if (_details is not null)
        {
            device.Model = _details.Model;
            device.SerialNumber = _details.SerialNumber;
        }
        if (string.IsNullOrWhiteSpace(device.Name))
        {
            device.Name = device.Host;
        }
        if (device.Cameras.Count == 0 &&
            MessageBox.Show(this, "Kamera listesi alınmadı. Yine de kaydedilsin mi?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            return;
        }
        Result = device;
        DialogResult = true;
    }
}
