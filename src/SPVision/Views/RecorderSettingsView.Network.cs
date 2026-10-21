using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using SPVision.Core;

namespace SPVision.Views;

// Settings › Cihaz ayarları › Ağ (phase 2). Read: ISAPI/System/Network/interfaces, .../Bond, ISAPI/Security/adminAccesses
// (+ /capabilities for the port ranges). Two risky writes, each with its own card: the address of the interface SPVision
// uses (RiskyWrites.NetworkAddress) and the HTTP / HTTPS / RTSP / server port numbers (RiskyWrites.AdminPorts: the
// adminAccesses document just read with only those portNo values changed). Both: fresh GET → edits → the separate
// confirmation with "eski → yeni" → PUT. Afterwards SPVision offers to use the new address / ports.
public partial class RecorderSettingsView
{
    sealed class NetworkData
    {
        public required Read<List<NetworkInterfaceInfo>> Interfaces { get; init; }
        public required Read<BondInfo> Bond { get; init; }
        public required Read<PortSettings> Ports { get; init; }

        /// <summary>The adminAccesses document as read (the ports body is built from a fresh read of the same document).</summary>
        public XDocument? PortsDoc { get; init; }

        public Dictionary<string, PortLimit> PortLimits { get; init; } = [];
        public NetworkInterfaceInfo? Main { get; init; }
        public NetworkAddress? Draft { get; set; }
        public PortSettings? PortsDraft { get; set; }

        /// <summary>Protocols whose box holds no valid port number (never sent).</summary>
        public HashSet<string> PortErrors { get; } = [];
    }

    NetworkData? _network;

    /// <summary>Result line of the Portlar card (the address card uses _results[Tab.Network]).</summary>
    (bool? Ok, string Text)? _portsResult;

    const string NetworkWarning =
        "Yanlış ayar cihazla bağlantıyı koparabilir. Uygulamadan önce eski → yeni değerler ayrı bir pencerede onaylatılır; " +
        "adres değişirse SPVision'daki cihaz adresini güncellemeyi önerir.";

    const string PortsWarning =
        "Port değişince bu cihaza bağlanan her program yeni portu ister: SPVision, web arayüzü, iVMS-4200, telefon uygulamaları ve " +
        "yönlendiricideki port yönlendirme. Uygulamadan önce eski → yeni portlar ayrı bir pencerede onaylatılır.";

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
                var portsDoc = await RecorderReader.TryAsync(isapi, IsapiPaths.AdminAccesses, d => d, ct);
                var ports = portsDoc.Value is { } doc ? Read<PortSettings>.Success(PortSettings.Parse(doc)) : new Read<PortSettings>(portsDoc.State, null, portsDoc.Error);
                var limits = portsDoc.Value is null ? new Dictionary<string, PortLimit>() : PortPlan.ParseLimits(await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.AdminAccessesCapabilities, ct));
                var main = interfaces.Value is { } list ? NetworkInterfaceInfo.ForHost(list, device.Host) : null;
                if (device == _device)
                {
                    _network = new NetworkData
                    {
                        Interfaces = interfaces, Bond = bond, Ports = ports, PortsDoc = portsDoc.Value, PortLimits = limits,
                        Main = main, Draft = main?.Address, PortsDraft = ports.Value,
                    };
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

        TabContent.Children.Add(Card(PortsPanel(data), new Thickness(0, 16, 0, 0)));

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
        (_network is { Main.Address: { } before, Draft: { } after } && NetworkPlan.Edits(before, after).Count > 0) || PortsPending();

    bool PortsPending() =>
        _network is { Ports.Value: { } before, PortsDraft: { } after } && PortPlan.Changes(before, after).Count > 0;

    // ================================================================ Portlar

    StackPanel PortsPanel(NetworkData data)
    {
        var device = _device!;
        var panel = new StackPanel();
        var editable = data.PortsDoc is not null && data.Ports.Value is not null;
        Button[] buttons = editable
            ? [SettingsButton("Geri al", (_, _) => RevertPorts()), SettingsButton("Cihaza uygula", (_, _) => ApplyPorts(), "SettingsAccentButton")]
            : [];
        panel.Children.Add(CardHeader("Portlar",
            "Kayıt cihazına bağlanılan portlar. Yalnızca port numaraları değişir; HTTPS / TLS ayarlarına dokunulmaz. Değişiklik onaydan sonra hemen geçerli olur.",
            buttons));
        panel.Children.Add(CardWarning(PortsWarning));
        if (_portsResult is { } result)
        {
            panel.Children.Add(Banner(result));
        }
        if (!editable || data.PortsDraft is not { } draft)
        {
            var reason = data.Ports.State == ReadState.Unsupported ? "Bu cihaz desteklemiyor" : RecorderText.Unreadable;
            panel.Children.Add(Row("Portlar", "HTTP, HTTPS, RTSP ve sunucu (SDK) portları.", MutedText(reason), last: true));
            return panel;
        }

        var before = data.Ports.Value!;
        string Uses(string protocol, int? port)
        {
            var used = protocol switch
            {
                PortPlan.Http => !device.UseHttps && device.HttpPort == port,
                PortPlan.Https => device.UseHttps && device.HttpPort == port,
                PortPlan.Rtsp => device.RtspPort == port,
                _ => device.SdkPort == port,
            };
            return used ? " SPVision bu portu kullanıyor." : "";
        }

        UIElement PortBox(string protocol, int? value, string id, Func<PortSettings, int?, PortSettings> set)
        {
            var label = PortPlan.Label(protocol) + " portu";
            if (value is null)
            {
                var missing = MutedText("—");
                missing.ToolTip = $"{label}: cihaz bildirmiyor";
                return missing;
            }
            var limit = data.PortLimits.GetValueOrDefault(protocol);
            var box = new TextBox
            {
                Text = value.Value.ToString(CultureInfo.InvariantCulture),
                MaxLength = 5,
                Width = ControlWidth,
                ToolTip = limit is null ? $"{label} (1–65535)" : string.Create(CultureInfo.InvariantCulture, $"{label} ({limit.Min}–{limit.Max})"),
            };
            box.SetResourceReference(StyleProperty, "RowTextBox");
            AutomationProperties.SetName(box, label);
            AutomationProperties.SetAutomationId(box, id);
            box.TextChanged += (_, _) =>
            {
                if (PortPlan.ParsePort(box.Text) is { } port)
                {
                    box.ClearValue(BorderBrushProperty);
                    data.PortErrors.Remove(protocol);
                    data.PortsDraft = set(data.PortsDraft!, port);
                }
                else
                {
                    box.SetResourceReference(BorderBrushProperty, "DangerText");
                    data.PortErrors.Add(protocol);
                    data.PortsDraft = set(data.PortsDraft!, PortOf(before, protocol)); // an invalid box is never sent
                }
            };
            return box;
        }

        panel.Children.Add(Row("HTTP portu", "Web arayüzü ve ayar trafiği, şifresiz (varsayılan 80)." + Uses(PortPlan.Http, before.Http),
            PortBox(PortPlan.Http, draft.Http, "NetworkPortHttp", (p, v) => p with { Http = v })));
        panel.Children.Add(Row("HTTPS portu", "Web arayüzü ve ayar trafiği, şifreli (varsayılan 443)." + Uses(PortPlan.Https, before.Https),
            PortBox(PortPlan.Https, draft.Https, "NetworkPortHttps", (p, v) => p with { Https = v })));
        panel.Children.Add(Row("RTSP portu", "Şifresiz görüntü akışı: VLC ve benzeri oynatıcılar (varsayılan 554)." + Uses(PortPlan.Rtsp, before.Rtsp),
            PortBox(PortPlan.Rtsp, draft.Rtsp, "NetworkPortRtsp", (p, v) => p with { Rtsp = v })));
        panel.Children.Add(Row("Sunucu (SDK) portu", "iVMS-4200, telefon uygulamaları ve şifreli akış (varsayılan 8000)." + Uses(PortPlan.Server, before.Server),
            PortBox(PortPlan.Server, draft.Server, "NetworkPortServer", (p, v) => p with { Server = v }), last: true));
        return panel;
    }

    static int? PortOf(PortSettings ports, string protocol) => protocol switch
    {
        PortPlan.Http => ports.Http,
        PortPlan.Https => ports.Https,
        PortPlan.Rtsp => ports.Rtsp,
        _ => ports.Server,
    };

    void RevertPorts()
    {
        if (_network is { } data)
        {
            data.PortsDraft = data.Ports.Value;
            data.PortErrors.Clear();
        }
        _portsResult = null;
        Render();
    }

    async void ApplyPorts()
    {
        if (_device is not { } device || _writing || _network is not { Ports.Value: { } before, PortsDraft: { } after, PortsDoc: { } shown } data)
        {
            return;
        }
        if (data.PortErrors.Count > 0)
        {
            _portsResult = (false, string.Join(", ", data.PortErrors.Select(p => PortPlan.Label(p))) + " portu 1–65535 arasında bir sayı olmalı.");
            Render();
            return;
        }
        var changes = PortPlan.Changes(before, after);
        if (changes.Count == 0)
        {
            _portsResult = (null, "Değişiklik yok; cihaza bir şey yazılmadı.");
            Render();
            return;
        }
        if (PortPlan.Validate(shown, changes, data.PortLimits) is { } problem)
        {
            _portsResult = (false, problem);
            Render();
            return;
        }
        var used = new DevicePorts(device.HttpPort, device.RtspPort, device.SdkPort);
        var follow = PortPlan.FollowDevice(device.UseHttps, used, changes);

        // Read the document again right before the confirmation: if the device's ports (or anything else in that
        // document) changed since the page was read, nothing is sent. The confirmed body is this fresh document + the ports.
        RiskyWriteRequest request;
        try
        {
            _writing = true;
            UpdateEnabled();
            using var isapi = new IsapiClient(device, ReadTimeout);
            var fresh = await isapi.GetConfigAsync(IsapiPaths.AdminAccesses);
            if (fresh.Root is null || !XNode.DeepEquals(fresh.Root, shown.Root))
            {
                _portsResult = (false, "Cihazdaki port ayarı bu sayfa açıldıktan sonra değişmiş; yeniden okundu. Değişikliği yeniden yapın.");
                _network = null;
                return;
            }
            request = RiskyWrites.AdminPorts(fresh, changes, follow is not null);
        }
        catch (Exception ex) when (ex is IsapiException or IsapiWriteDeniedException or ConfigEditException or System.Xml.XmlException)
        {
            _portsResult = (false, $"Port ayarı okunamadı: {ex.Message}");
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

        // After the confirmation and right before the PUT: the same check once more (the dialog may have stayed open).
        async Task<string?> StillUnchanged(IsapiClient isapi)
        {
            try
            {
                return request.IsUnchanged(await isapi.GetConfigAsync(IsapiPaths.AdminAccesses))
                    ? null
                    : "Cihazdaki port ayarı onay sırasında değişti; hiçbir şey gönderilmedi. Sayfa yeniden okundu.";
            }
            catch (Exception ex) when (ex is IsapiException or System.Xml.XmlException or System.Net.Http.HttpRequestException)
            {
                return $"Port ayarı gönderimden önce yeniden okunamadı; hiçbir şey gönderilmedi. ({ex.Message})";
            }
        }

        var (outcome, text) = await RunRiskyAsync(Tab.Network, device, request, StillUnchanged, () => _portsResult = (null, "Uygulanıyor…"));
        if (outcome == RiskyOutcome.Cancelled)
        {
            Render();
            return;
        }
        _portsResult = outcome switch
        {
            RiskyOutcome.Done => (true, ("Uygulandı. " + text).Trim()),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; portlar değişmiş olabilir (HTTP/HTTPS portu değiştiyse eski port artık yanıt vermez). ({text})"),
            _ => (false, text),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? $"Portlar uygulandı: {device.Name}" : $"Portlar: {text}");

        // SPVision may now reach the recorder only through the new ports: offer to use them (also when no answer came back).
        if (outcome is RiskyOutcome.Done or RiskyOutcome.NoAnswer && follow is not null &&
            ConfirmDialog.Show(Window.GetWindow(this),
                $"SPVision “{device.Name}” kayıt cihazına bundan sonra yeni portlardan bağlansın mı?",
                [(null, PortPlan.DeviceChangeLines(device.UseHttps, used, follow))], "Portları güncelle", "Hayır"))
        {
            device.HttpPort = follow.Http;
            device.RtspPort = follow.Rtsp;
            device.SdkPort = follow.Sdk;
            DeviceUpdated?.Invoke(device, true);
            _portsResult = (true, "Uygulandı. SPVision artık yeni portları kullanıyor.");
        }
        if (device == _device)
        {
            _network = null; // read again (through the new port if it was updated)
            Render();
        }
    }

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
