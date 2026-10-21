using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

// Settings › Cihaz ayarları › Ağ (phase 2). Read: ISAPI/System/Network/interfaces, .../Bond, ISAPI/Security/adminAccesses
// (ports, read only). The address of the interface SPVision uses is a risky write (RiskyWrites.NetworkAddress): fresh
// GET → edits → the separate confirmation with "eski → yeni" → PUT. After a change SPVision offers to use the new address.
public partial class RecorderSettingsView
{
    sealed class NetworkData
    {
        public required Read<List<NetworkInterfaceInfo>> Interfaces { get; init; }
        public required Read<BondInfo> Bond { get; init; }
        public required Read<PortSettings> Ports { get; init; }
        public NetworkInterfaceInfo? Main { get; init; }
        public NetworkAddress? Draft { get; set; }
    }

    NetworkData? _network;

    const string NetworkWarning =
        "Yanlış ayar cihazla bağlantıyı koparabilir. Uygulamadan önce eski → yeni değerler ayrı bir pencerede onaylatılır; " +
        "adres değişirse SPVision'daki cihaz adresini güncellemeyi önerir.";

    void RenderNetwork()
    {
        if (_network is not { } data)
        {
            if (ShowFailed(Tab.Network, () => _network = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Network, async (device, isapi, ct) =>
            {
                var interfaces = await RecorderReader.TryAsync(isapi, IsapiPaths.Interfaces, NetworkInterfaceInfo.ParseList, ct);
                var bond = await RecorderReader.TryAsync(isapi, IsapiPaths.Bond, BondInfo.Parse, ct);
                var ports = await RecorderReader.TryAsync(isapi, IsapiPaths.AdminAccesses, PortSettings.Parse, ct);
                var main = interfaces.Value is { } list ? NetworkInterfaceInfo.ForHost(list, device.Host) : null;
                if (device == _device)
                {
                    _network = new NetworkData { Interfaces = interfaces, Bond = bond, Ports = ports, Main = main, Draft = main?.Address };
                }
            });
            return;
        }

        var main = data.Main;
        var editable = main?.Address is not null && data.Bond.Value?.Enabled != true;
        var panel = new StackPanel();
        var title = main is null ? "Ağ adresi" : string.Create(CultureInfo.InvariantCulture, $"Ağ adresi · LAN {main.Id}");
        var description = main is null
            ? "Kayıt cihazının ağ adresi."
            : "SPVision kayıt cihazına bu arabirimden bağlanıyor. Değişiklik onaydan sonra hemen geçerli olur.";
        Button[] buttons = editable
            ? [SettingsButton("Geri al", (_, _) => RevertNetwork()), SettingsButton("Cihaza uygula", (_, _) => ApplyNetwork(), "SettingsAccentButton")]
            : [];
        panel.Children.Add(CardHeader(title, description, buttons));
        panel.Children.Add(CardWarning(NetworkWarning));
        if (ResultBanner(Tab.Network) is { } banner)
        {
            panel.Children.Add(banner);
        }

        if (main?.Address is not { } current || data.Draft is not { } draft)
        {
            var reason = data.Interfaces.State == ReadState.Unsupported ? "Bu cihaz desteklemiyor" : RecorderText.Unreadable;
            panel.Children.Add(Row("Ağ ayarları", "IPv4 adresi, maske, ağ geçidi ve DNS.", MutedText(reason), last: true));
        }
        else
        {
            if (data.Bond.Value?.Enabled == true)
            {
                var note = Text("Ağ birleştirme (bond) açık: adres cihazın web arayüzünden değiştirilir. Değerler yalnızca gösterilir.", "RowHelp");
                note.Margin = new Thickness(16, 10, 16, 10);
                note.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
                panel.Children.Add(note);
            }
            var fields = new List<TextBox>();
            void SetFieldsEnabled(bool staticAddress)
            {
                foreach (var field in fields)
                {
                    field.IsEnabled = editable && staticAddress;
                }
            }

            var dhcp = Switch(draft.Dhcp, "DHCP ile otomatik adres", on =>
            {
                data.Draft = data.Draft! with { Dhcp = on };
                SetFieldsEnabled(!on);
            });
            dhcp.IsEnabled = editable;
            AutomationProperties.SetAutomationId(dhcp, "NetworkDhcp");
            panel.Children.Add(Row("DHCP (otomatik adres)",
                "Açıkken adresi yönlendirici verir; SPVision yeni adresi bilemez, Cihaz yönetimi'nde elle girmeniz gerekir.",
                dhcp));

            TextBox Address(string value, string name, string id, Func<NetworkAddress, string, NetworkAddress> set, double width = ControlWidth)
            {
                var box = new TextBox { Text = value, MaxLength = 15, Width = width, ToolTip = name };
                box.SetResourceReference(StyleProperty, "RowTextBox");
                AutomationProperties.SetName(box, name);
                AutomationProperties.SetAutomationId(box, id);
                box.TextChanged += (_, _) =>
                {
                    var text = box.Text.Trim();
                    data.Draft = set(data.Draft!, text);
                    if (Ipv4.TryParse(text, out _))
                    {
                        box.ClearValue(BorderBrushProperty);
                    }
                    else
                    {
                        box.SetResourceReference(BorderBrushProperty, "DangerText");
                    }
                };
                fields.Add(box);
                return box;
            }

            panel.Children.Add(Row("IP adresi", "Kayıt cihazının yerel ağdaki adresi. Değişince cihaza yalnızca yeni adresten ulaşılır.",
                Address(draft.Ip, "IP adresi", "NetworkIp", (d, v) => d with { Ip = v })));
            panel.Children.Add(Row("Alt ağ maskesi", "Yönlendiricinin ayarıyla aynı olmalı (çoğunlukla 255.255.255.0).",
                Address(draft.Mask, "Alt ağ maskesi", "NetworkMask", (d, v) => d with { Mask = v })));
            panel.Children.Add(Row("Ağ geçidi", "Yönlendiricinin adresi; IP adresiyle aynı alt ağda olmalı.",
                Address(draft.Gateway, "Ağ geçidi", "NetworkGateway", (d, v) => d with { Gateway = v })));

            // DNS: two equal boxes, 116 + 8 + 116 = 240 (like the stream drop-downs of Kameralar).
            var dns = new Grid { Width = ControlWidth };
            dns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            dns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            dns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            var dns1 = Address(draft.Dns1, "Birincil DNS", "NetworkDns1", (d, v) => d with { Dns1 = v }, 116);
            var dns2 = Address(draft.Dns2, "İkincil DNS", "NetworkDns2", (d, v) => d with { Dns2 = v }, 116);
            Grid.SetColumn(dns2, 2);
            dns.Children.Add(dns1);
            dns.Children.Add(dns2);
            panel.Children.Add(Row("DNS sunucuları", "Birincil · İkincil (yoksa 0.0.0.0). Alan adı çözümleme için.", dns, last: true));
            SetFieldsEnabled(!draft.Dhcp); // with DHCP the server gives the address, mask, gateway and DNS
        }
        TabContent.Children.Add(Card(panel));

        // Portlar (read only): four equal cells like the disk summary of Genel bakış.
        var ports = new StackPanel();
        ports.Children.Add(CardHeader("Portlar",
            "Yalnızca gösterilir: portlar cihazın HTTPS / TLS güvenlik ayarlarıyla aynı belgede durduğu için SPVision'dan değiştirilmez. Cihazın web arayüzünde değiştirirseniz Cihaz yönetimi'nde de güncelleyin.",
            [], (System.Windows.Media.Geometry)FindResource("IconLock")));
        var cells = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(16, 12, 16, 14) };
        void Cell(string caption, int? port)
        {
            var cell = new StackPanel();
            cell.Children.Add(Text(caption, "CaptionText"));
            var value = Text(data.Ports.Ok ? PortSettings.Text(port) : RecorderText.Unreadable, "CardTitle");
            value.Margin = new Thickness(0, 4, 0, 0);
            AutomationProperties.SetName(value, $"{caption}: {value.Text}");
            cell.Children.Add(value);
            cells.Children.Add(cell);
        }
        var p = data.Ports.Value;
        Cell("HTTP", p?.Http);
        Cell("HTTPS", p?.Https);
        Cell("RTSP", p?.Rtsp);
        Cell("SUNUCU (SDK)", p?.Server);
        ports.Children.Add(cells);
        TabContent.Children.Add(Card(ports, new Thickness(0, 16, 0, 0)));

        // Other interfaces (read only), e.g. the camera-side LAN of an NVR.
        var others = (data.Interfaces.Value ?? []).Where(i => i != main).ToList();
        if (others.Count > 0)
        {
            var other = new StackPanel();
            other.Children.Add(CardHeader("Diğer ağ arabirimleri", "Yalnızca gösterilir. Kayıt cihazının kameralara bakan ağı genellikle buradadır.", []));
            for (var i = 0; i < others.Count; i++)
            {
                var nic = others[i];
                var value = nic.Address is { } a
                    ? $"{a.Ip} / {a.Mask} · {NetworkAddress.ModeText(a.Dhcp)}"
                    : RecorderText.Unreadable;
                other.Children.Add(InfoRow(string.Create(CultureInfo.InvariantCulture, $"LAN {nic.Id}"), ValueText(value), last: i == others.Count - 1));
            }
            TabContent.Children.Add(Card(other, new Thickness(0, 16, 0, 0)));
        }
    }

    bool NetworkPending() =>
        _network is { Main.Address: { } before, Draft: { } after } && NetworkPlan.Edits(before, after).Count > 0;

    void RevertNetwork()
    {
        if (_network is { } data)
        {
            data.Draft = data.Main?.Address;
        }
        _results.Remove(Tab.Network);
        Render();
    }

    async void ApplyNetwork()
    {
        if (_device is not { } device || _writing || _network is not { Main: { Address: { } before } main, Draft: { } after })
        {
            return;
        }
        if (NetworkPlan.Validate(before, after) is { } problem)
        {
            _results[Tab.Network] = (false, problem);
            Render();
            return;
        }
        if (NetworkPlan.Edits(before, after).Count == 0)
        {
            _results[Tab.Network] = (null, "Değişiklik yok; cihaza bir şey yazılmadı.");
            Render();
            return;
        }

        // Read the document again right before the confirmation: the confirmed body is this document + the edits.
        RiskyWriteRequest request;
        try
        {
            _writing = true;
            UpdateEnabled();
            using var isapi = new IsapiClient(device, ReadTimeout);
            var doc = await isapi.GetConfigAsync(IsapiPaths.InterfaceAddress(main.Id));
            if (NetworkAddress.ParseDocument(doc) != before)
            {
                _results[Tab.Network] = (false, "Cihazdaki ağ ayarı bu sayfa açıldıktan sonra değişmiş; yeniden okundu. Değişikliği yeniden yapın.");
                _network = null;
                return;
            }
            request = RiskyWrites.NetworkAddress(main.Id, doc, NetworkPlan.Edits(before, after));
        }
        catch (Exception ex) when (ex is IsapiException or IsapiWriteDeniedException or ConfigEditException or System.Xml.XmlException)
        {
            _results[Tab.Network] = (false, $"Ağ ayarı okunamadı: {ex.Message}");
            return;
        }
        finally
        {
            _writing = false;
            UpdateEnabled();
            if (device == _device)
            {
                Render();
            }
        }

        var (outcome, text) = await RunRiskyAsync(Tab.Network, device, request);
        if (outcome == RiskyOutcome.Cancelled)
        {
            Render();
            return;
        }
        _results[Tab.Network] = outcome switch
        {
            RiskyOutcome.Done => (true, after.Dhcp
                ? "Uygulandı. Cihaz adresini DHCP sunucusundan alacak; yeni adresi öğrenip Ayarlar › Cihaz yönetimi'nde girin."
                : ("Uygulandı. " + text).Trim()),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; ayar uygulanmış olabilir (adres değiştiyse eski adres artık yanıt vermez). ({text})"),
            _ => (false, text),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? $"Ağ ayarı uygulandı: {device.Name}" : $"Ağ ayarı: {text}");

        // The recorder may now answer only at the new address: offer to use it (also when no answer came back).
        if (outcome is RiskyOutcome.Done or RiskyOutcome.NoAnswer && NetworkPlan.NewHost(device.Host, before, after) is { } host &&
            ConfirmDialog.Show(Window.GetWindow(this),
                $"SPVision “{device.Name}” kayıt cihazına bundan sonra {host} adresinden bağlansın mı? (Şu an {device.Host}.)",
                "Adresi güncelle", "Hayır"))
        {
            device.Host = host;
            DeviceUpdated?.Invoke(device, true);
            _results[Tab.Network] = (true, $"Uygulandı. SPVision artık {host} adresini kullanıyor.");
        }
        if (device == _device)
        {
            _network = null; // read again (at the new address if it was updated)
            Render();
        }
    }
}
