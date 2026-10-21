using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

// Settings › Cihaz ayarları › Olaylar (phase 2). Read: ISAPI/Event/triggersCap, ISAPI/Event/triggers and per camera
// .../video/videoLoss + .../video/tamperDetection. Written through the ordinary route (ConfigWriter, allowlist):
// the cameras' enabled flags and the triggers' notification methods (beep / center / email).
public partial class RecorderSettingsView
{
    sealed class EventCameraRow
    {
        public required Camera Camera { get; init; }
        public required Read<EventEnable> VideoLoss { get; init; }
        public required Read<EventEnable> Tamper { get; init; }
        public bool? VideoLossOn { get; set; }
        public bool? TamperOn { get; set; }
    }

    sealed class EventsData
    {
        public required List<EventCameraRow> Cameras { get; init; }
        public required Read<EventTriggers> Triggers { get; init; }
        public required EventTriggerCaps Caps { get; init; }

        /// <summary>Reactions the user touched: (event kind, method) → wanted state. Untouched = left as the device has it.</summary>
        public Dictionary<(string Kind, NotifyMethod Method), bool> Wanted { get; } = [];
    }

    EventsData? _events;

    /// <summary>OLAY | SESLİ UYARI | MERKEZE BİLDİR | E-POSTA — the three switch columns line up with the camera table's.</summary>
    static readonly GridLength[] ReactionColumns = [new(1, GridUnitType.Star), new(120), new(120), new(120)];

    static readonly GridLength[] EventCameraColumns = [new(64), new(1, GridUnitType.Star), new(120), new(120)];

    void RenderEvents()
    {
        if (_events is not { } data)
        {
            if (ShowFailed(Tab.Events, () => _events = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Events, async (device, isapi, ct) =>
            {
                var cameras = await CamerasAsync(isapi, ct);
                var capsDoc = await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.EventTriggersCap, ct);
                var triggers = await RecorderReader.TryAsync(isapi, IsapiPaths.EventTriggers, EventTriggers.Parse, ct);
                var rows = new List<EventCameraRow>();
                foreach (var camera in cameras.Value ?? [])
                {
                    var loss = await RecorderReader.TryAsync(isapi, IsapiPaths.VideoLoss(camera.Channel), EventEnable.Parse, ct);
                    var tamper = await RecorderReader.TryAsync(isapi, IsapiPaths.Tamper(camera.Channel), EventEnable.Parse, ct);
                    rows.Add(new EventCameraRow
                    {
                        Camera = camera, VideoLoss = loss, Tamper = tamper, VideoLossOn = loss.Value?.Enabled, TamperOn = tamper.Value?.Enabled,
                    });
                }
                if (device == _device)
                {
                    _events = new EventsData
                    {
                        Cameras = rows, Triggers = triggers, Caps = capsDoc is null ? EventTriggerCaps.None : EventTriggerCaps.Parse(capsDoc),
                    };
                    if (!cameras.Ok)
                    {
                        _results[Tab.Events] = (false, "Kamera listesi okunamadı.");
                    }
                }
            });
            return;
        }

        // Olay tepkileri (system-wide reactions + the cameras' video loss / tampering reactions).
        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Olay tepkileri",
            "Bir olay olduğunda kayıt cihazının ne yapacağı. “Cihaza uygula” bu sekmedeki tüm değişiklikleri (aşağıdaki kameralar dahil) yazar.",
            [SettingsButton("Geri al", (_, _) => RevertEvents()), SettingsButton("Cihaza uygula", (_, _) => ApplyEvents(), "SettingsAccentButton")]));
        if (ResultBanner(Tab.Events) is { } banner)
        {
            panel.Children.Add(banner);
        }
        panel.Children.Add(TableHeader(ReactionColumns, ("OLAY", false), ("SESLİ UYARI", true), ("MERKEZE BİLDİR", true), ("E-POSTA", true)));
        var channels = data.Cameras.Select(r => r.Camera.Channel).ToList();
        var kinds = EventKinds.All;
        for (var i = 0; i < kinds.Count; i++)
        {
            var kind = kinds[i];
            var grid = TableGrid(ReactionColumns);
            var label = new StackPanel();
            label.Children.Add(NameText(kind.Text));
            var mixed = data.Triggers.Value is { } t && EventNotify.All.Any(m => t.State(kind, channels, m) == LinkState.Mixed);
            var help = Text(mixed ? kind.Help + " · bazı kameralarda farklı" : kind.Help, "CellText");
            help.FontSize = 11;
            help.ToolTip = help.Text;
            help.SetResourceReference(TextBlock.ForegroundProperty, mixed ? "WarningText" : "TextSecondary");
            label.Children.Add(help);
            Put(grid, label, 0);
            for (var m = 0; m < EventNotify.All.Count; m++)
            {
                Put(grid, ReactionCell(data, kind, channels, EventNotify.All[m]), m + 1);
            }
            panel.Children.Add(TableRow(grid, last: i == kinds.Count - 1, height: 48));
        }
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(Card(panel));

        var foot = Text("Sesli uyarı: kayıt cihazının kendi ikazı. Merkeze bildir: alarmı izleme yazılımına (iVMS / VMS) gönderir. E-posta: cihazda e-posta ayarı yapılmış olmalı.", "RowHelp");
        foot.Margin = new Thickness(16, 8, 16, 0);
        TabContent.Children.Add(foot);

        // Kamera olayları: on/off per camera.
        var cams = new StackPanel();
        cams.Children.Add(CardHeader("Kamera olayları",
            "Kamera başına görüntü kaybı ve kurcalama algılama. Kurcalama alanı ve hassasiyeti cihazın web arayüzünden ayarlanır.", []));
        cams.Children.Add(TableHeader(EventCameraColumns, ("KANAL", false), ("KAMERA ADI", false), ("GÖRÜNTÜ KAYBI", true), ("KURCALAMA", true)));
        if (data.Cameras.Count == 0)
        {
            var grid = TableGrid(EventCameraColumns);
            Put(grid, MutedText("Kamera yok", HorizontalAlignment.Left), 0, span: 4);
            cams.Children.Add(TableRow(grid, last: true));
        }
        for (var i = 0; i < data.Cameras.Count; i++)
        {
            var row = data.Cameras[i];
            var label = CameraLabel(row.Camera);
            var grid = TableGrid(EventCameraColumns);
            Put(grid, ChannelText(row.Camera.Channel), 0);
            Put(grid, NameText(label), 1);
            Put(grid, row.VideoLossOn is { } loss
                ? Centered(Named(Switch(loss, $"{label} görüntü kaybı", v => row.VideoLossOn = v), $"EventVideoLoss{row.Camera.Channel}"))
                : UnsupportedCell(row.VideoLoss), 2);
            Put(grid, row.TamperOn is { } tamper
                ? Centered(Named(Switch(tamper, $"{label} kurcalama", v => row.TamperOn = v), $"EventTamper{row.Camera.Channel}"))
                : UnsupportedCell(row.Tamper), 3);
            cams.Children.Add(TableRow(grid, last: i == data.Cameras.Count - 1));
        }
        cams.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(Card(cams, new Thickness(0, 16, 0, 0)));
    }

    static T Named<T>(T element, string automationId) where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, automationId);
        return element;
    }

    /// <summary>"—" (centered) with the reason as tooltip: not offered by the device, or not readable.</summary>
    static TextBlock UnsupportedCell<T>(Read<T>? read, string unsupported = "Bu cihaz desteklemiyor")
    {
        var text = MutedText("—", HorizontalAlignment.Center);
        text.ToolTip = Reason(read, unsupported);
        AutomationProperties.SetName(text, text.ToolTip as string);
        return text;
    }

    UIElement ReactionCell(EventsData data, EventKind kind, IReadOnlyList<int> channels, NotifyMethod method)
    {
        var state = data.Triggers.Value?.State(kind, channels, method) ?? LinkState.Missing;
        if (!data.Caps.Supports(kind, method) || state == LinkState.Missing)
        {
            return UnsupportedCell(data.Triggers.Ok ? new Read<EventTriggers>(ReadState.Unsupported, null) : data.Triggers);
        }
        var on = data.Wanted.TryGetValue((kind.Key, method), out var wanted) ? wanted : state == LinkState.On;
        var name = $"{kind.Text} · {EventNotify.Text(method)}";
        var toggle = Switch(on, name, v => data.Wanted[(kind.Key, method)] = v);
        AutomationProperties.SetAutomationId(toggle, $"Reaction_{kind.Key}_{method}");
        if (state == LinkState.Mixed && !data.Wanted.ContainsKey((kind.Key, method)))
        {
            toggle.ToolTip = "Bazı kameralarda açık. Açarsanız tüm kameralarda açılır, kapatırsanız tümünde kapanır.";
        }
        return Centered(toggle);
    }

    List<ConfigWrite> PlanEvents()
    {
        if (_events is not { } data)
        {
            return [];
        }
        var writes = data.Cameras
            .SelectMany(r => EventPlan.Camera(r.Camera.Channel, CameraLabel(r.Camera), r.VideoLoss.Value, r.VideoLossOn, r.Tamper.Value, r.TamperOn))
            .ToList();
        if (data.Triggers.Value is { } triggers && data.Wanted.Count > 0)
        {
            writes.AddRange(EventPlan.Linkage(triggers, data.Wanted, data.Cameras.Select(r => (r.Camera.Channel, CameraLabel(r.Camera))).ToList()));
        }
        return writes;
    }

    void RevertEvents()
    {
        if (_events is { } data)
        {
            data.Wanted.Clear();
            foreach (var row in data.Cameras)
            {
                row.VideoLossOn = row.VideoLoss.Value?.Enabled;
                row.TamperOn = row.Tamper.Value?.Enabled;
            }
        }
        _results.Remove(Tab.Events);
        Render();
    }

    void ApplyEvents() => Apply(Tab.Events, PlanEvents(), () => _events = null);
}
