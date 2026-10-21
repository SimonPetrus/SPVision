using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Settings › Cihaz ayarları (phase 1: Genel bakış, Kameralar, Hareket, Kayıt, Sistem; phase 2: Olaylar, Ağ,
/// Kullanıcılar, yeniden başlatma / disk biçimlendirme; phase 3: Bakım — see the partial files). Every tab reads the recorder with ISAPI
/// GETs when it is first shown; nothing is written on open, tab change or load. "Cihaza uygula" lists the changes
/// ("&lt;kamera&gt; · &lt;alan&gt;: &lt;eski&gt; → &lt;yeni&gt;"), asks for confirmation and then
/// <see cref="ConfigWriter"/> writes them one by one (read-modify-write through the allowlist in
/// <see cref="IsapiWritePolicy"/>), verifies them and stops at the first failure. "Geri al" restores the values last read.
/// Risky operations go through their own confirmation (<see cref="RiskyConfirmDialog"/>) and
/// <see cref="IsapiClient.SendRiskyAsync"/>. Stream encryption and Hik-Connect are shown read-only and are never written.
/// </summary>
public partial class RecorderSettingsView : UserControl
{
    enum Tab
    {
        Overview,
        Cameras,
        Motion,
        Record,
        Events,
        Network,
        Users,
        System,
        Maintenance,
    }

    static readonly (Tab Tab, string Text, string Later)[] Tabs =
    [
        (Tab.Overview, "Genel bakış", ""),
        (Tab.Cameras, "Kameralar", ""),
        (Tab.Motion, "Hareket", ""),
        (Tab.Record, "Kayıt", ""),
        (Tab.Events, "Olaylar", "Görüntü kaybı ve kurcalama gibi durumlarda cihazın tepkisi."),
        (Tab.Network, "Ağ", "Kayıt cihazının ağ adresi ve portları."),
        (Tab.Users, "Kullanıcılar", "Cihaza bağlanabilecek hesaplar ve yetkileri."),
        (Tab.System, "Sistem", ""),
        (Tab.Maintenance, "Bakım", "Kayıt cihazı ve kamera yazılımları, fabrika ayarları."),
    ];

    const double ControlWidth = 240;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);

    // ---- state of the selected device (all reset by LoadDevice) ----

    sealed class OverviewData
    {
        public required Read<DeviceDetails> Info { get; init; }
        public required Read<ChannelUsage> Channels { get; init; }
        public required Read<DeviceTime> Time { get; init; }
        public required Read<List<HddInfo>> Disks { get; init; }
        public required Read<SecurityStatus> Security { get; init; }
        public required DateTimeOffset ReadAt { get; init; }
    }

    sealed class CameraSlot
    {
        public required Camera Camera { get; init; }
        public bool Loaded { get; set; }
        public Read<OverlaySettings>? Overlay { get; set; }
        public Read<ImageColor>? Color { get; set; }
        public Read<StreamSettings>? Main { get; set; }
        public Read<StreamSettings>? Sub { get; set; }
        public StreamOptions? MainOptions { get; set; }
        public StreamOptions? SubOptions { get; set; }
        public CameraSettings Before { get; set; } = new(null, null, null, null, null);
        public CameraSettings Draft { get; set; } = new(null, null, null, null, null);
    }

    sealed class MotionRow
    {
        public required Camera Camera { get; init; }
        public required Read<MotionSettings> Read { get; set; }
        public bool Enabled { get; set; }
        public MotionLevel? Level { get; set; }
    }

    sealed class RecordRow
    {
        public required Camera Camera { get; init; }
        public required Read<RecordSettings> Read { get; set; }
        public bool? Enabled { get; set; }
        public RecordPreset? Preset { get; set; }
        public int? Pre { get; set; }
        public int? Post { get; set; }
    }

    sealed class SystemData
    {
        public required Read<DeviceDetails> Info { get; init; }
        public required Read<DeviceTime> Time { get; init; }
        public required Read<NtpInfo> Ntp { get; init; }
        public required Read<List<HddInfo>> Disks { get; init; }
        public required DateTimeOffset ReadAt { get; init; }
        public string? Name { get; set; }
        public bool SyncTime { get; set; }
    }

    /// <summary>Device selector item; the dot follows the last read (green = answered).</summary>
    sealed class DeviceChoice(Device device) : INotifyPropertyChanged
    {
        string _state = "Probing";

        public Device Device { get; } = device;
        public string Name => Device.Name;

        public string State
        {
            get => _state;
            set
            {
                _state = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly Dictionary<Tab, ToggleButton> _tabButtons = [];
    readonly DispatcherTimer _clockTimer;
    readonly List<(TextBlock Text, TimeSpan Delta, TimeSpan Offset)> _clocks = [];
    readonly Dictionary<Tab, (bool? Ok, string Text)> _results = [];
    readonly HashSet<Tab> _loading = [];
    readonly HashSet<Tab> _attempted = [];

    Tab _tab = Tab.Overview;
    Device? _device;
    CancellationTokenSource? _cts;
    bool _filling;
    bool _writing;
    bool _open;

    Read<List<Camera>>? _cameras;
    OverviewData? _overview;
    readonly Dictionary<int, CameraSlot> _slots = [];
    int _selectedChannel;
    Border? _cameraHost;
    List<MotionRow>? _motion;
    List<RecordRow>? _record;
    SystemData? _system;

    public RecorderSettingsView()
    {
        InitializeComponent();
        DeviceCombo.ItemTemplate = DeviceItemTemplate();
        foreach (var (tab, text, _) in Tabs)
        {
            var button = new ClickToggleButton { Content = text };
            button.SetResourceReference(StyleProperty, "RecorderTab");
            AutomationProperties.SetName(button, text);
            AutomationProperties.SetAutomationId(button, $"RecorderTab{tab}");
            button.Click += (_, _) => SelectTab(tab);
            _tabButtons[tab] = button;
            TabBar.Children.Add(button);
        }
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClocks();
        UpdateTabButtons();
    }

    /// <summary>Short feedback for the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>The page became visible: (re)reads the selected device. Never writes.</summary>
    public void Open()
    {
        _open = true;
        FillDevices();
        _clockTimer.Start();
    }

    /// <summary>The page was hidden: stops reading; unsaved form values are dropped.</summary>
    public void Close()
    {
        _open = false;
        _clockTimer.Stop();
        _cts?.Cancel();
    }

    // ================================================================ device + tabs

    static DataTemplate DeviceItemTemplate()
    {
        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(WidthProperty, 7.0);
        dot.SetValue(HeightProperty, 7.0);
        dot.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        dot.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        dot.SetResourceReference(Shape.FillProperty, "Offline");
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(DeviceChoice.Name)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(dot);
        panel.AppendChild(text);
        var template = new DataTemplate { VisualTree = panel };
        foreach (var (state, brush) in new[] { ("Online", "Online"), ("Probing", "Warning") })
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(DeviceChoice.State)), Value = state };
            trigger.Setters.Add(new Setter(Shape.FillProperty, new DynamicResourceExtension(brush), "Dot"));
            template.Triggers.Add(trigger);
        }
        dot.Name = "Dot";
        return template;
    }

    void FillDevices()
    {
        _filling = true;
        try
        {
            var previous = _device?.Id;
            DeviceCombo.Items.Clear();
            foreach (var device in App.Settings.Devices)
            {
                DeviceCombo.Items.Add(new DeviceChoice(device));
            }
            var choices = DeviceCombo.Items.Cast<DeviceChoice>().ToList();
            DeviceCombo.SelectedItem = choices.FirstOrDefault(c => c.Device.Id == previous)
                                       ?? choices.FirstOrDefault(c => string.Equals(c.Device.DeviceType, "NVR", StringComparison.OrdinalIgnoreCase))
                                       ?? choices.FirstOrDefault();
        }
        finally
        {
            _filling = false;
        }
        LoadDevice((DeviceCombo.SelectedItem as DeviceChoice)?.Device);
    }

    void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || DeviceCombo.SelectedItem is not DeviceChoice choice || choice.Device == _device)
        {
            return;
        }
        if (HasPendingChanges() && !ConfirmDialog.Show(Window.GetWindow(this),
                "Uygulanmamış değişiklikler var. Başka cihaza geçilirse bu değişiklikler silinir.", "Geç", "Vazgeç"))
        {
            _filling = true;
            DeviceCombo.SelectedItem = DeviceCombo.Items.Cast<DeviceChoice>().FirstOrDefault(c => c.Device == _device);
            _filling = false;
            return;
        }
        LoadDevice(choice.Device);
    }

    /// <summary>Forgets everything read from the previous device and reads the current tab of this one.</summary>
    void LoadDevice(Device? device)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _device = device;
        _cameras = null;
        _overview = null;
        _slots.Clear();
        _selectedChannel = 0;
        _motion = null;
        _record = null;
        _system = null;
        _events = null;
        _network = null;
        _users = null;
        ForgetMaintenance();
        _results.Clear();
        _loading.Clear();
        _attempted.Clear();
        WebButton.IsEnabled = device is not null;
        SetChoiceState("Probing");
        Render();
    }

    void SetChoiceState(string state)
    {
        if (DeviceCombo.Items.Cast<DeviceChoice>().FirstOrDefault(c => c.Device == _device) is { } choice)
        {
            choice.State = state;
        }
    }

    void SelectTab(Tab tab)
    {
        _tab = tab;
        UpdateTabButtons();
        Render();
    }

    void UpdateTabButtons()
    {
        foreach (var (tab, button) in _tabButtons)
        {
            button.IsChecked = tab == _tab;
        }
    }

    /// <summary>The recorder's web UI with the scheme and port of the device setting (Core <see cref="WebUi.Address"/>).</summary>
    void Web_Click(object sender, RoutedEventArgs e)
    {
        if (_device is not { } device)
        {
            return;
        }
        string url;
        try
        {
            url = WebUi.Address(device);
        }
        catch (UriFormatException ex)
        {
            Message?.Invoke($"Cihaz adresi geçersiz: {ex.Message}");
            return;
        }
        OpenBrowser(url);
    }

    // ================================================================ rendering

    void Render()
    {
        _clocks.Clear();
        _cameraHost = null;
        TabContent.Children.Clear();
        if (_device is null)
        {
            TabContent.Children.Add(NoticeCard(
                "Henüz cihaz yok",
                "Önce Ayarlar › Cihaz yönetimi'nden kayıt cihazını ekleyin."));
            return;
        }
        switch (_tab)
        {
            case Tab.Overview:
                RenderOverview();
                break;
            case Tab.Cameras:
                RenderCameras();
                break;
            case Tab.Motion:
                RenderMotion();
                break;
            case Tab.Record:
                RenderRecord();
                break;
            case Tab.System:
                RenderSystem();
                break;
            case Tab.Events:
                RenderEvents();
                break;
            case Tab.Network:
                RenderNetwork();
                break;
            case Tab.Users:
                RenderUsers();
                break;
            case Tab.Maintenance:
                RenderMaintenance();
                break;
            default:
                var later = Tabs.First(t => t.Tab == _tab);
                TabContent.Children.Add(NoticeCard("Bu bölüm bir sonraki aşamada eklenecek.",
                    later.Later + " Şimdilik cihazın web arayüzünü kullanın."));
                break;
        }
    }

    void UpdateEnabled()
    {
        TabContent.IsEnabled = !_writing;
        TabBar.IsEnabled = !_writing && !_firmwareBusy;     // a firmware upgrade (Bakım) keeps the page on its device and tab
        DeviceCombo.IsEnabled = !_writing && !_firmwareBusy;
    }

    void UpdateClocks()
    {
        var now = DateTimeOffset.Now;
        foreach (var (text, delta, offset) in _clocks)
        {
            text.Text = RecorderText.Time((now + delta).ToOffset(offset));
        }
    }

    /// <summary>Shows the device's clock and keeps it ticking from the moment it was read.</summary>
    TextBlock Clock(Read<DeviceTime> time, DateTimeOffset readAt)
    {
        var text = ValueText(RecorderText.Unreadable);
        if (time.Value?.Local is { } local)
        {
            _clocks.Add((text, local - readAt, local.Offset));
            text.Text = RecorderText.Time((DateTimeOffset.Now + (local - readAt)).ToOffset(local.Offset));
        }
        return text;
    }

    // ---- small builders (theme brushes only, via resource references) ----

    static Border Card(UIElement content, Thickness? margin = null)
    {
        var card = new Border { Child = content, Margin = margin ?? new Thickness(0) };
        card.SetResourceReference(StyleProperty, "Card");
        return card;
    }

    static TextBlock Text(string text, string style)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    static TextBlock ValueText(string text)
    {
        var block = Text(text, "CellText");
        block.HorizontalAlignment = HorizontalAlignment.Right;
        block.TextAlignment = TextAlignment.Right;
        block.ToolTip = text;
        return block;
    }

    static TextBlock MutedText(string text, HorizontalAlignment align = HorizontalAlignment.Right)
    {
        var block = Text(text, "CellText");
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        block.HorizontalAlignment = align;
        block.ToolTip = text;
        return block;
    }

    static Button SettingsButton(string text, RoutedEventHandler click, string style = "SettingsButton")
    {
        var button = new Button { Content = text };
        button.SetResourceReference(StyleProperty, style);
        button.Click += click;
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Card header: title + description on the left, equal 120×30 buttons on the right (8 px apart).</summary>
    static Border CardHeader(string title, string description, IReadOnlyList<Button> buttons, Geometry? icon = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            var glyph = new GlyphIcon { Data = icon, Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSecondary");
            titleRow.Children.Add(glyph);
        }
        titleRow.Children.Add(Text(title, "CardTitle"));
        left.Children.Add(titleRow);
        if (description.Length > 0)
        {
            var help = Text(description, "RowHelp");
            left.Children.Add(help);
        }
        grid.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < buttons.Count; i++)
        {
            buttons[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            right.Children.Add(buttons[i]);
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var header = new Border { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        return header;
    }

    /// <summary>Settings row: label + help | control (240 px, right aligned).</summary>
    static Border Row(string label, string help, UIElement control, bool last = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ControlWidth) });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        left.Children.Add(Text(label, "RowLabel"));
        if (help.Length > 0)
        {
            left.Children.Add(Text(help, "RowHelp"));
        }
        grid.Children.Add(left);
        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            if (fe.HorizontalAlignment == HorizontalAlignment.Stretch && fe is not Grid)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        var row = new Border { Padding = new Thickness(16, 8, 16, 8), MinHeight = 56, BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "Divider");
        return row;
    }

    /// <summary>Read-only info row of the overview cards: label | value, 44 px.</summary>
    static Border InfoRow(string label, UIElement value, bool last = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Text(label, "CellText");
        name.FontSize = 12.5;
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        grid.Children.Add(name);
        if (value is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.Margin = new Thickness(16, 0, 0, 0);
        }
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        var row = new Border { Height = 44, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "Divider");
        return row;
    }

    enum BadgeKind
    {
        On,
        Off,
        Unknown,
    }

    static Border Badge(string text, BadgeKind kind)
    {
        var label = new TextBlock { Text = text };
        label.SetResourceReference(StyleProperty, "BadgeText");
        label.SetResourceReference(TextBlock.ForegroundProperty, kind == BadgeKind.On ? "Online" : "TextSecondary");
        var badge = new Border { Child = label };
        badge.SetResourceReference(StyleProperty, "Badge");
        badge.SetResourceReference(Border.BackgroundProperty, kind == BadgeKind.On ? "OnlineSoftBg" : "HoverBg");
        return badge;
    }

    static Border BoolBadge(Read<SecurityStatus> read, Func<SecurityStatus, bool?> pick) =>
        read.Value is { } v && pick(v) is { } on ? Badge(RecorderText.OnOff(on), on ? BadgeKind.On : BadgeKind.Off) : Badge(RecorderText.Unreadable, BadgeKind.Unknown);

    /// <summary>
    /// A finished read left the tab without data (unexpected error): show "okunamadı" with a retry instead of
    /// starting another read on every render.
    /// </summary>
    bool ShowFailed(Tab tab, Action forget)
    {
        if (!_attempted.Contains(tab) || _loading.Contains(tab))
        {
            return false;
        }
        TabContent.Children.Add(NoticeCard("Cihazdan okunamadı", "Cihaz yanıt vermedi veya beklenmeyen bir yanıt verdi.", () =>
        {
            forget();
            _attempted.Remove(tab);
            Render();
        }));
        return true;
    }

    /// <summary>One centered card for an empty or later tab; <paramref name="retry"/> adds "Yeniden dene".</summary>
    Border NoticeCard(string title, string text, Action? retry = null)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420 };
        var icon = new GlyphIcon { Data = (Geometry)FindResource("IconClock"), Size = 28, HorizontalAlignment = HorizontalAlignment.Center };
        icon.SetResourceReference(GlyphIcon.ForegroundProperty, "TextSecondary");
        panel.Children.Add(icon);
        var head = Text(title, "CardTitle");
        head.Margin = new Thickness(0, 12, 0, 0);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.TextAlignment = TextAlignment.Center;
        panel.Children.Add(head);
        if (text.Length > 0)
        {
            var help = Text(text, "RowHelp");
            help.TextAlignment = TextAlignment.Center;
            help.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(help);
        }
        if (retry is not null)
        {
            var button = SettingsButton("Yeniden dene", (_, _) => retry());
            button.Margin = new Thickness(0, 16, 0, 0);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(button);
        }
        var card = Card(panel);
        card.Padding = new Thickness(40, 32, 40, 32);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.Margin = new Thickness(0, 32, 0, 0);
        return card;
    }

    Border LoadingCard(string text = "Cihazdan okunuyor…") => NoticeCard(text, "");

    /// <summary>Result line under a card header after "Cihaza uygula".</summary>
    UIElement? ResultBanner(Tab tab)
    {
        if (!_results.TryGetValue(tab, out var result))
        {
            return null;
        }
        var text = Text(result.Text, "CellText");
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.SetResourceReference(TextBlock.ForegroundProperty, result.Ok switch { true => "Online", false => "WarningText", _ => "TextSecondary" });
        var banner = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = text };
        banner.SetResourceReference(Border.BorderBrushProperty, "Border");
        banner.SetResourceReference(Border.BackgroundProperty, result.Ok switch { true => "OnlineSoftBg", false => "WarningSoftBg", _ => "SubtleBg" });
        return banner;
    }

    static CheckBox Switch(bool value, string name, Action<bool> changed)
    {
        var box = new CheckBox { IsChecked = value };
        box.SetResourceReference(StyleProperty, "ToggleSwitch");
        AutomationProperties.SetName(box, name);
        // Checked/Unchecked, not Click: UI Automation toggles without a click. The initial value is set above.
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    static ComboBox Combo<T>(IEnumerable<(T Value, string Text)> items, T selected, double width, string name, Action<T> changed)
    {
        var combo = new ComboBox { Width = width, ToolTip = name };
        AutomationProperties.SetName(combo, name);
        foreach (var (value, text) in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
            if (EqualityComparer<T>.Default.Equals(value, selected))
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T value })
            {
                changed(value);
            }
        };
        return combo;
    }

    /// <summary>Segmented choice (one always selected); <paramref name="selected"/> null = none until the user picks.</summary>
    static Border Segment<T>(IReadOnlyList<(T Value, string Text)> items, T? selected, double buttonWidth, string name, Action<T> changed)
        where T : struct
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        foreach (var (value, text) in items)
        {
            var button = new ClickToggleButton
            {
                Content = text,
                Width = buttonWidth,
                IsChecked = selected is { } s && EqualityComparer<T>.Default.Equals(s, value),
                Margin = new Thickness(buttons.Count == 0 ? 0 : 2, 0, 0, 0),
            };
            button.SetResourceReference(StyleProperty, "SegmentButton");
            AutomationProperties.SetName(button, $"{name}: {text}");
            button.Click += (_, _) =>
            {
                foreach (var other in buttons)
                {
                    other.IsChecked = other == button;
                }
                changed(value);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        var group = new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Center };
        group.SetResourceReference(StyleProperty, "SegmentGroup");
        AutomationProperties.SetName(group, name);
        return group;
    }

    static TextBox RowTextBox(string value, int maxLength, string name, Action<string> changed)
    {
        var box = new TextBox { Text = value, MaxLength = maxLength, Width = ControlWidth };
        box.SetResourceReference(StyleProperty, "RowTextBox");
        AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            var valid = box.Text.Trim().Length > 0;
            if (valid)
            {
                box.ClearValue(BorderBrushProperty);
                changed(box.Text.Trim() == value.Trim() ? value : box.Text.Trim());
            }
            else
            {
                box.SetResourceReference(BorderBrushProperty, "DangerText");
                changed(value); // an empty name is never written
            }
        };
        return box;
    }

    /// <summary>Table header/row grid: 16 px inset, 16 px gaps between the given columns.</summary>
    static Grid TableGrid(params GridLength[] columns)
    {
        var grid = new Grid();
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = columns[i] });
        }
        return grid;
    }

    static Border TableHeader(GridLength[] columns, params (string Text, bool Center)[] cells)
    {
        var grid = TableGrid(columns);
        for (var i = 0; i < cells.Length; i++)
        {
            var text = Text(cells[i].Text, "CaptionText");
            text.VerticalAlignment = VerticalAlignment.Center;
            if (cells[i].Center)
            {
                text.HorizontalAlignment = HorizontalAlignment.Center;
            }
            Grid.SetColumn(text, i * 2);
            grid.Children.Add(text);
        }
        var header = new Border { Height = 36, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        header.SetResourceReference(Border.BackgroundProperty, "SubtleBg");
        header.SetResourceReference(Border.BorderBrushProperty, "Border");
        return header;
    }

    static Border TableRow(Grid grid, bool last, double height = 40)
    {
        var row = new Border { MinHeight = height, Padding = new Thickness(16, 4, 16, 4), BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "Divider");
        return row;
    }

    static void Put(Grid grid, UIElement element, int column, int span = 1)
    {
        Grid.SetColumn(element, column * 2);
        if (span > 1)
        {
            Grid.SetColumnSpan(element, span * 2 - 1);
        }
        if (element is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        grid.Children.Add(element);
    }

    static TextBlock ChannelText(int channel)
    {
        var text = Text(channel.ToString(CultureInfo.InvariantCulture), "CellText");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return text;
    }

    static TextBlock NameText(string name)
    {
        var text = Text(name, "CellText");
        text.FontSize = 12.5;
        text.FontWeight = FontWeights.Medium;
        text.ToolTip = name;
        return text;
    }

    static string Reason<T>(Read<T>? read, string unsupported = RecorderText.Unsupported) =>
        read?.State == ReadState.Unsupported ? unsupported : RecorderText.Unreadable;

    // ================================================================ loading helpers

    /// <summary>Runs a read for the current device; drops the result if the device changed or the page closed.</summary>
    async void Load(Tab tab, Func<Device, IsapiClient, CancellationToken, Task> read)
    {
        if (_device is not { } device || _cts is not { } cts || !_loading.Add(tab))
        {
            return;
        }
        _attempted.Add(tab);
        try
        {
            using var isapi = new IsapiClient(device, ReadTimeout);
            await read(device, isapi, cts.Token);
            if (!cts.IsCancellationRequested && device == _device)
            {
                SetChoiceState(isapi.Answered ? "Online" : "Offline");
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IsapiException or System.Xml.XmlException or System.Net.Http.HttpRequestException)
        {
            // Each part already shows "okunamadı"; nothing else to do.
        }
        finally
        {
            if (device == _device && !cts.IsCancellationRequested)
            {
                _loading.Remove(tab);
            }
        }
        if (!cts.IsCancellationRequested && device == _device && _open && _tab == tab && !_writing)
        {
            Render();
        }
    }

    async Task<Read<List<Camera>>> CamerasAsync(IsapiClient isapi, CancellationToken ct)
    {
        if (_cameras is { Ok: true } cached)
        {
            return cached;
        }
        var read = await RecorderReader.TryCallAsync(async () => (await isapi.GetCamerasAsync(ct)).OrderBy(c => c.Channel).ToList());
        _cameras = read;
        return read;
    }

    static string CameraLabel(Camera camera) => string.IsNullOrWhiteSpace(camera.Name)
        ? string.Create(CultureInfo.InvariantCulture, $"Kamera {camera.Channel}")
        : camera.Name;

    // ================================================================ Genel bakış

    void RenderOverview()
    {
        if (_overview is not { } data)
        {
            if (ShowFailed(Tab.Overview, () => _overview = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Overview, async (device, isapi, ct) =>
            {
                var info = await RecorderReader.TryCallAsync<DeviceDetails>(async () => await isapi.GetDeviceInfoAsync(ct));
                var cameras = await CamerasAsync(isapi, ct);
                var caps = await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.InputProxyCapabilities, ct);
                var total = (caps is null ? null : ChannelUsage.ParseTotal(caps)) ?? device.ChannelCount;
                var channels = cameras.Value is { } list
                    ? Read<ChannelUsage>.Success(new ChannelUsage(list.Count, total))
                    : new Read<ChannelUsage>(cameras.State, null, cameras.Error);
                var time = await RecorderReader.TryAsync(isapi, IsapiPaths.Time, DeviceTime.Parse, ct);
                var readAt = DateTimeOffset.Now;
                var disks = await RecorderReader.TryAsync(isapi, IsapiPaths.Hdd, HddInfo.ParseList, ct);
                var security = await RecorderReader.TryAsync(isapi, IsapiPaths.Ezviz, SecurityStatus.Parse, ct);
                if (device == _device)
                {
                    _overview = new OverviewData { Info = info, Channels = channels, Time = time, Disks = disks, Security = security, ReadAt = readAt };
                }
            });
            return;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Güvenlik · kilitli (read-only badges; SPVision never changes these).
        var security = new StackPanel();
        security.Children.Add(CardHeader("Güvenlik · kilitli", "", [], (Geometry)FindResource("IconLock")));
        security.Children.Add(InfoRow("Akış şifreleme", BoolBadge(data.Security, s => s.StreamEncryption)));
        security.Children.Add(InfoRow("Hik-Connect (platform erişimi)", BoolBadge(data.Security, s => s.HikConnect)));
        var note = Text("Güvenlik ayarları SPVision'dan değiştirilemez; yalnızca durumları gösterilir.", "RowHelp");
        note.Margin = new Thickness(16, 12, 16, 12);
        security.Children.Add(note);
        grid.Children.Add(Card(security));

        // Sistem bilgisi.
        var info = data.Info.Value;
        var system = new StackPanel();
        system.Children.Add(CardHeader("Sistem bilgisi", "", [], (Geometry)FindResource("IconInfo")));
        system.Children.Add(InfoRow("Model", ValueText(info?.Model ?? RecorderText.Unreadable)));
        system.Children.Add(InfoRow("Cihaz adı", ValueText(info?.Name is { Length: > 0 } n ? n : RecorderText.Unreadable)));
        system.Children.Add(InfoRow("Kanallar", ValueText(data.Channels.Value?.Text ?? RecorderText.Unreadable)));
        system.Children.Add(InfoRow("Yazılım sürümü", ValueText(info?.Firmware ?? RecorderText.Unreadable)));
        system.Children.Add(InfoRow("Cihaz saati", Clock(data.Time, data.ReadAt), last: true));
        var systemCard = Card(system);
        Grid.SetColumn(systemCard, 2);
        grid.Children.Add(systemCard);
        TabContent.Children.Add(grid);

        // Disk özeti: four equal cells.
        var disks = new StackPanel();
        disks.Children.Add(CardHeader("Diskler", "Ayrıntılar Sistem sekmesinde.", [], (Geometry)FindResource("IconServer")));
        var cells = new UniformGrid { Rows = 1, Columns = 4, Margin = new Thickness(16, 12, 16, 14) };
        var summary = data.Disks.Value is { } list ? DiskSummary.Of(list) : null;
        void Cell(string caption, string value)
        {
            var cell = new StackPanel();
            cell.Children.Add(Text(caption, "CaptionText"));
            var text = Text(value, "CardTitle");
            text.Margin = new Thickness(0, 4, 0, 0);
            text.ToolTip = value;
            cell.Children.Add(text);
            cells.Children.Add(cell);
        }
        Cell("DİSK", summary?.Count.ToString(CultureInfo.InvariantCulture) ?? RecorderText.Unreadable);
        Cell("KAPASİTE", summary is null ? RecorderText.Unreadable : RecorderText.Size(summary.CapacityMb));
        Cell("BOŞ ALAN", summary is null ? RecorderText.Unreadable : RecorderText.Size(summary.FreeMb));
        Cell("DURUM", summary?.Status ?? RecorderText.Unreadable);
        disks.Children.Add(cells);
        TabContent.Children.Add(Card(disks, new Thickness(0, 16, 0, 0)));
    }

    // ================================================================ Kameralar

    void RenderCameras()
    {
        if (_cameras is not { } cameras)
        {
            if (ShowFailed(Tab.Cameras, () => _cameras = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Cameras, async (_, isapi, ct) => await CamerasAsync(isapi, ct));
            return;
        }
        if (cameras.Value is not { Count: > 0 } list)
        {
            TabContent.Children.Add(NoticeCard("Kamera listesi okunamadı", cameras.Error ?? "", () =>
            {
                _cameras = null;
                _attempted.Remove(Tab.Cameras);
                Render();
            }));
            return;
        }
        foreach (var camera in list)
        {
            if (!_slots.ContainsKey(camera.Channel))
            {
                _slots[camera.Channel] = new CameraSlot { Camera = camera };
            }
        }
        if (!_slots.ContainsKey(_selectedChannel))
        {
            _selectedChannel = list[0].Channel;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Camera picker (left): same height as the settings card next to it.
        var picker = new ListBox { BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(picker, "Kamera seç");
        ScrollViewer.SetVerticalScrollBarVisibility(picker, ScrollBarVisibility.Auto);
        foreach (var camera in list)
        {
            var item = new ListBoxItem { Tag = camera.Channel, ToolTip = CameraLabel(camera) };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(ChannelText(camera.Channel));
            var name = Text(CameraLabel(camera), "CellText");
            name.FontSize = 12.5;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            item.Content = row;
            picker.Items.Add(item);
            if (camera.Channel == _selectedChannel)
            {
                picker.SelectedItem = item;
            }
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ListBoxItem { Tag: int channel } && channel != _selectedChannel)
            {
                _selectedChannel = channel;
                RenderCameraCard();
            }
        };
        var pickerPanel = new DockPanel();
        var pickerHeader = new Border { Height = 36, Padding = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(0, 0, 0, 1) };
        pickerHeader.SetResourceReference(Border.BackgroundProperty, "SubtleBg");
        pickerHeader.SetResourceReference(Border.BorderBrushProperty, "Border");
        var pickerCaption = Text(string.Create(CultureInfo.InvariantCulture, $"KAMERALAR · {list.Count}"), "CaptionText");
        pickerCaption.VerticalAlignment = VerticalAlignment.Center;
        pickerHeader.Child = pickerCaption;
        DockPanel.SetDock(pickerHeader, Dock.Top);
        pickerPanel.Children.Add(pickerHeader);
        pickerPanel.Children.Add(picker);
        var pickerCard = Card(pickerPanel);
        pickerCard.VerticalAlignment = VerticalAlignment.Top;
        pickerPanel.SetValue(RoundedClip.RadiusProperty, 7.0);
        grid.Children.Add(pickerCard);

        _cameraHost = new Border { VerticalAlignment = VerticalAlignment.Top, MinHeight = 480 };
        Grid.SetColumn(_cameraHost, 2);
        grid.Children.Add(_cameraHost);
        pickerCard.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = _cameraHost });

        TabContent.Children.Add(grid);
        RenderCameraCard();
    }

    void RenderCameraCard()
    {
        if (_cameraHost is not { } host || !_slots.TryGetValue(_selectedChannel, out var slot))
        {
            return;
        }
        if (!slot.Loaded)
        {
            host.Child = LoadingCardInline(CameraLabel(slot.Camera));
            LoadCamera(slot);
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(CardHeader(
            CameraLabel(slot.Camera),
            string.Create(CultureInfo.InvariantCulture, $"Kanal {slot.Camera.Channel} · ad, ekran yazıları, görüntü ve akış ayarları. “Cihaza uygula” tüm kameralardaki değişiklikleri yazar."),
            [SettingsButton("Geri al", (_, _) => RevertCameras()), SettingsButton("Cihaza uygula", (_, _) => ApplyCameras(), "SettingsAccentButton")]));
        if (ResultBanner(Tab.Cameras) is { } banner)
        {
            panel.Children.Add(banner);
        }

        var draft = slot.Draft;
        // Read only: the recorder stores the name in the camera's connection record (address, user, password), whose
        // password a GET never returns — writing the record back could drop the camera. See RecorderPlan.Camera.
        var cameraName = ValueText(CameraLabel(slot.Camera));
        AutomationProperties.SetAutomationId(cameraName, "CameraName");
        panel.Children.Add(Row("Kamera adı",
            "Yalnızca gösterilir. Kayıt cihazı adı kameranın bağlantı kaydıyla (adres, kullanıcı, şifre) birlikte yazar; SPVision o şifreyi okuyamadığı için yazarsa kamera bağlantısı kopabilir. Adı cihazın web arayüzünden değiştirin, sonra Cihaz yönetimi › Kameraları yenile.",
            cameraName));

        UIElement OverlaySwitch(Func<OverlaySettings, bool?> pick, Func<OverlaySettings, bool, OverlaySettings> set, string name) =>
            draft.Overlay is { } o && pick(o) is { } on
                ? Switch(on, name, v => slot.Draft = slot.Draft with { Overlay = set(slot.Draft.Overlay!, v) })
                : MutedText(Reason(slot.Overlay));
        panel.Children.Add(Row("Ekranda tarih / saat", "Görüntünün üstüne cihazın yazdığı tarih ve saat (OSD).",
            OverlaySwitch(o => o.DateTime, (o, v) => o with { DateTime = v }, "Ekranda tarih / saat")));
        panel.Children.Add(Row("Ekranda kamera adı", "Görüntünün üstüne cihazın yazdığı kamera adı (OSD).",
            OverlaySwitch(o => o.ChannelName, (o, v) => o with { ChannelName = v }, "Ekranda kamera adı")));

        panel.Children.Add(Row("Parlaklık · Kontrast · Doygunluk",
            draft.Color is { } range ? $"Kameranın görüntü ayarları ({range.Range.Min}–{range.Range.Max})." : "Kameranın görüntü ayarları.",
            ColorControls(slot)));

        panel.Children.Add(Row("Ana akış", "Çözünürlük · kare hızı / bit hızı · sıkıştırma (cihazın sunduğu seçenekler).",
            StreamControls(slot, StreamQuality.Main)));
        panel.Children.Add(Row("Alt akış", "Küçük karelerde kullanılan düşük çözünürlüklü akış.",
            StreamControls(slot, StreamQuality.Sub), last: true));

        host.Child = Card(panel);
    }

    Border LoadingCardInline(string title)
    {
        var panel = new StackPanel();
        panel.Children.Add(CardHeader(title, "Cihazdan okunuyor…", []));
        return Card(panel);
    }

    UIElement ColorControls(CameraSlot slot)
    {
        if (slot.Draft.Color is not { } color)
        {
            return MutedText(Reason(slot.Color));
        }
        // Three equal boxes: 72 + 12 + 72 + 12 + 72 = 240.
        var grid = new Grid { Width = ControlWidth };
        foreach (var width in new[] { 72.0, 12, 72, 12, 72 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }
        void Box(int column, int? value, string name, Func<ImageColor, int, ImageColor> set)
        {
            UIElement element;
            if (value is { } v)
            {
                var box = new TextBox { Text = v.ToString(CultureInfo.InvariantCulture), MaxLength = 3, ToolTip = name, TextAlignment = TextAlignment.Center };
                box.SetResourceReference(StyleProperty, "RowTextBox");
                AutomationProperties.SetName(box, name);
                box.TextChanged += (_, _) =>
                {
                    if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= color.Range.Min && n <= color.Range.Max)
                    {
                        box.ClearValue(BorderBrushProperty);
                        slot.Draft = slot.Draft with { Color = set(slot.Draft.Color!, n) };
                    }
                    else
                    {
                        box.SetResourceReference(BorderBrushProperty, "DangerText");
                        slot.Draft = slot.Draft with { Color = set(slot.Draft.Color!, v) }; // invalid input is never written
                    }
                };
                element = box;
            }
            else
            {
                element = MutedText("—", HorizontalAlignment.Center);
                ((TextBlock)element).ToolTip = $"{name}: {RecorderText.Unsupported}";
            }
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        Box(0, color.Brightness, "Parlaklık", (c, n) => c with { Brightness = n });
        Box(2, color.Contrast, "Kontrast", (c, n) => c with { Contrast = n });
        Box(4, color.Saturation, "Doygunluk", (c, n) => c with { Saturation = n });
        return grid;
    }

    UIElement StreamControls(CameraSlot slot, StreamQuality quality)
    {
        var main = quality == StreamQuality.Main;
        var stream = main ? slot.Draft.Main : slot.Draft.Sub;
        var options = main ? slot.MainOptions : slot.SubOptions;
        if (stream is null || options is null)
        {
            return MutedText(Reason(main ? slot.Main : slot.Sub));
        }
        var name = main ? "Ana akış" : "Alt akış";
        void Set(Func<StreamSettings, StreamSettings> change)
        {
            if (main)
            {
                slot.Draft = slot.Draft with { Main = change(slot.Draft.Main!) };
            }
            else
            {
                slot.Draft = slot.Draft with { Sub = change(slot.Draft.Sub!) };
            }
        }

        // 2×2 equal drop-downs: 116 + 8 + 116 = 240.
        var grid = new Grid { Width = ControlWidth, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        Place(Combo(options.Resolutions.Select(r => (r, StreamSettings.ResolutionTextOf(r.Width, r.Height))),
            (stream.Width, stream.Height), 116, $"{name} çözünürlüğü", r => Set(s => s with { Width = r.Width, Height = r.Height })), 0, 0);
        Place(Combo(options.FrameRates.Select(f => (f, StreamSettings.FrameRateText(f))),
            stream.FrameRate, 116, $"{name} kare hızı", f => Set(s => s with { FrameRate = f })), 0, 2);
        Place(Combo(options.Bitrates.Select(b => (b, StreamSettings.BitrateText(b))),
            stream.Bitrate, 116, $"{name} bit hızı (üst sınır)", b => Set(s => s with { Bitrate = b })), 2, 0);
        Place(Combo(options.Codecs.Select(c => (c, c)),
            stream.Codec, 116, $"{name} sıkıştırma", c => Set(s => s with { Codec = c })), 2, 2);
        return grid;
    }

    async void LoadCamera(CameraSlot slot)
    {
        if (_device is not { } device || _cts is not { } cts || !_loading.Add(Tab.Cameras))
        {
            return;
        }
        try
        {
            using var isapi = new IsapiClient(device, ReadTimeout);
            var ct = cts.Token;
            var channel = slot.Camera.Channel;
            slot.Overlay = await RecorderReader.TryAsync(isapi, IsapiPaths.Overlays(channel), OverlaySettings.Parse, ct);
            var imageCaps = await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.ImageCapabilities(channel), ct);
            slot.Color = await RecorderReader.TryAsync(isapi, IsapiPaths.Image(channel), d => ImageColor.Parse(d, imageCaps), ct);
            slot.Main = await RecorderReader.TryAsync(isapi, IsapiPaths.Stream(channel, StreamQuality.Main), StreamSettings.Parse, ct);
            if (slot.Main.Value is { } main)
            {
                slot.MainOptions = StreamOptions.Parse(await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.StreamCapabilities(channel, StreamQuality.Main), ct), main);
            }
            slot.Sub = await RecorderReader.TryAsync(isapi, IsapiPaths.Stream(channel, StreamQuality.Sub), StreamSettings.Parse, ct);
            if (slot.Sub.Value is { } sub)
            {
                slot.SubOptions = StreamOptions.Parse(await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.StreamCapabilities(channel, StreamQuality.Sub), ct), sub);
            }
            slot.Before = new CameraSettings(slot.Camera.Name, slot.Overlay.Value, slot.Color.Value, slot.Main.Value, slot.Sub.Value);
            slot.Draft = slot.Before;
            slot.Loaded = true;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (device == _device && !cts.IsCancellationRequested)
            {
                _loading.Remove(Tab.Cameras);
            }
        }
        if (device == _device && !cts.IsCancellationRequested && _open && _tab == Tab.Cameras && !_writing)
        {
            if (slot.Camera.Channel == _selectedChannel)
            {
                RenderCameraCard();
            }
            else if (_slots.TryGetValue(_selectedChannel, out var selected) && !selected.Loaded)
            {
                RenderCameraCard(); // the user picked another camera meanwhile
            }
        }
    }

    List<ConfigWrite> PlanCameras() =>
        _slots.Values.Where(s => s.Loaded).OrderBy(s => s.Camera.Channel)
            .SelectMany(s => RecorderPlan.Camera(s.Camera.Channel, CameraLabel(s.Camera), s.Before, s.Draft)).ToList();

    void RevertCameras()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Draft = slot.Before;
        }
        _results.Remove(Tab.Cameras);
        RenderCameraCard();
    }

    void ApplyCameras() => Apply(Tab.Cameras, PlanCameras(), () =>
    {
        _cameras = null;
        _slots.Clear();
    });

    // ================================================================ Hareket

    static readonly (MotionLevel, string)[] Levels =
        [(MotionLevel.Low, "Düşük"), (MotionLevel.Medium, "Orta"), (MotionLevel.High, "Yüksek")];

    static readonly GridLength[] MotionColumns =
        [new(64), new(1, GridUnitType.Star), new(136), new(220)];

    void RenderMotion()
    {
        if (_motion is not { } rows)
        {
            if (ShowFailed(Tab.Motion, () => _motion = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Motion, async (device, isapi, ct) =>
            {
                var cameras = await CamerasAsync(isapi, ct);
                var list = new List<MotionRow>();
                foreach (var camera in cameras.Value ?? [])
                {
                    var caps = await RecorderReader.TryCapabilitiesAsync(isapi, IsapiPaths.MotionCapabilities(camera.Channel), ct);
                    var read = await RecorderReader.TryAsync(isapi, IsapiPaths.Motion(camera.Channel), d => MotionSettings.Parse(d, caps), ct);
                    list.Add(new MotionRow { Camera = camera, Read = read, Enabled = read.Value?.Enabled ?? false, Level = read.Value?.Level });
                }
                if (device == _device)
                {
                    _motion = list;
                    if (!cameras.Ok)
                    {
                        _results[Tab.Motion] = (false, "Kamera listesi okunamadı.");
                    }
                }
            });
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Hareket algılama", "Kayıt cihazının her kamerada hareket arayıp aramayacağı ve ne kadar hassas olacağı.",
        [
            SettingsButton("Tümünü aç", (_, _) => SetAllMotion(true)),
            SettingsButton("Tümünü kapat", (_, _) => SetAllMotion(false)),
            SettingsButton("Geri al", (_, _) => RevertMotion()),
            SettingsButton("Cihaza uygula", (_, _) => ApplyMotion(), "SettingsAccentButton"),
        ]));
        if (ResultBanner(Tab.Motion) is { } banner)
        {
            panel.Children.Add(banner);
        }
        panel.Children.Add(TableHeader(MotionColumns, ("KANAL", false), ("KAMERA ADI", false), ("ALGILAMA", true), ("HASSASİYET", true)));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var grid = TableGrid(MotionColumns);
            Put(grid, ChannelText(row.Camera.Channel), 0);
            Put(grid, NameText(CameraLabel(row.Camera)), 1);
            if (row.Read.Value is { } settings)
            {
                var segment = settings.Sensitivity is null
                    ? (UIElement)MutedText(RecorderText.Unsupported, HorizontalAlignment.Center)
                    : Segment(Levels, row.Level, 64, $"{CameraLabel(row.Camera)} hassasiyet", level => row.Level = level);
                if (segment is FrameworkElement fe)
                {
                    fe.Opacity = row.Enabled ? 1 : 0.4;
                    fe.HorizontalAlignment = HorizontalAlignment.Center;
                }
                var toggle = Switch(row.Enabled, $"{CameraLabel(row.Camera)} hareket algılama", on =>
                {
                    row.Enabled = on;
                    if (segment is FrameworkElement s)
                    {
                        s.Opacity = on ? 1 : 0.4;
                    }
                });
                toggle.HorizontalAlignment = HorizontalAlignment.Center;
                Put(grid, toggle, 2);
                Put(grid, segment, 3);
            }
            else
            {
                Put(grid, MutedText(Reason(row.Read), HorizontalAlignment.Center), 2, span: 2);
            }
            panel.Children.Add(TableRow(grid, last: i == rows.Count - 1));
        }
        var card = Card(panel);
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(card);
    }

    void SetAllMotion(bool on)
    {
        foreach (var row in _motion ?? [])
        {
            if (row.Read.Ok)
            {
                row.Enabled = on;
            }
        }
        Render();
    }

    List<ConfigWrite> PlanMotion() =>
        (_motion ?? []).Where(r => r.Read.Ok)
            .Select(r => RecorderPlan.Motion(r.Camera.Channel, CameraLabel(r.Camera), r.Read.Value!, r.Enabled, r.Level))
            .OfType<ConfigWrite>().ToList();

    void RevertMotion()
    {
        foreach (var row in _motion ?? [])
        {
            row.Enabled = row.Read.Value?.Enabled ?? false;
            row.Level = row.Read.Value?.Level;
        }
        _results.Remove(Tab.Motion);
        Render();
    }

    void ApplyMotion() => Apply(Tab.Motion, PlanMotion(), () => _motion = null);

    // ================================================================ Kayıt

    static readonly GridLength[] RecordColumns =
        [new(64), new(1, GridUnitType.Star), new(96), new(180), new(100), new(100)];

    const string CustomNote = "Özel program (cihazın web arayüzünden düzenleyin)";

    void RenderRecord()
    {
        if (_record is not { } rows)
        {
            if (ShowFailed(Tab.Record, () => _record = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.Record, async (device, isapi, ct) =>
            {
                var cameras = await CamerasAsync(isapi, ct);
                var list = new List<RecordRow>();
                foreach (var camera in cameras.Value ?? [])
                {
                    var read = await RecorderReader.TryAsync(isapi, IsapiPaths.RecordTrack(camera.Channel), RecordSettings.Parse, ct);
                    var v = read.Value;
                    list.Add(new RecordRow { Camera = camera, Read = read, Enabled = v?.Enabled, Preset = v?.Preset, Pre = v?.PreSeconds, Post = v?.PostSeconds });
                }
                if (device == _device)
                {
                    _record = list;
                    if (!cameras.Ok)
                    {
                        _results[Tab.Record] = (false, "Kamera listesi okunamadı.");
                    }
                }
            });
            return;
        }

        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Kayıt",
            "Kameranın kaydı ve tüm hafta için kayıt modu. Haftalık programın ayrıntısı cihazın web arayüzünden düzenlenir.",
            [SettingsButton("Geri al", (_, _) => RevertRecord()), SettingsButton("Cihaza uygula", (_, _) => ApplyRecord(), "SettingsAccentButton")]));
        if (ResultBanner(Tab.Record) is { } banner)
        {
            panel.Children.Add(banner);
        }
        panel.Children.Add(TableHeader(RecordColumns, ("KANAL", false), ("KAMERA ADI", false), ("KAYIT", true), ("MOD", false),
            ("ÖNCE", false), ("SONRA", false)));
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var grid = TableGrid(RecordColumns);
            Put(grid, ChannelText(row.Camera.Channel), 0);
            var label = CameraLabel(row.Camera);
            var settings = row.Read.Value;
            var nameCell = new StackPanel();
            nameCell.Children.Add(NameText(label));
            if (settings?.Preset == RecordPreset.Custom)
            {
                var note = Text(CustomNote, "CellText");
                note.FontSize = 11;
                note.ToolTip = CustomNote;
                note.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
                nameCell.Children.Add(note);
            }
            Put(grid, nameCell, 1);

            if (settings is null)
            {
                Put(grid, MutedText(Reason(row.Read), HorizontalAlignment.Center), 2, span: 4);
            }
            else
            {
                Put(grid, row.Enabled is { } on
                    ? Centered(Switch(on, $"{label} kayıt", v => row.Enabled = v))
                    : MutedText("—", HorizontalAlignment.Center), 2);

                if (settings.Preset is { } current)
                {
                    var presets = new List<(RecordPreset, string)>
                    {
                        (RecordPreset.Continuous, RecordSchedule.Text(RecordPreset.Continuous)),
                        (RecordPreset.MotionOnly, RecordSchedule.Text(RecordPreset.MotionOnly)),
                        (RecordPreset.Off, RecordSchedule.Text(RecordPreset.Off)),
                    };
                    if (current == RecordPreset.Custom)
                    {
                        presets.Add((RecordPreset.Custom, RecordSchedule.Text(RecordPreset.Custom)));
                    }
                    var combo = Combo(presets, row.Preset ?? current, 180, $"{label} kayıt modu", p => row.Preset = p);
                    if (current == RecordPreset.Custom)
                    {
                        combo.ToolTip = CustomNote + ". Bir mod seçerseniz program o modla değiştirilir.";
                    }
                    Put(grid, combo, 3);
                }
                else
                {
                    Put(grid, MutedText(RecorderText.Unsupported, HorizontalAlignment.Left), 3);
                }

                Put(grid, row.Pre is { } pre
                    ? Combo(RecordSettings.WithCurrent(RecordSettings.PreOptions, settings.PreSeconds).Select(s => (s, RecorderText.Seconds(s))),
                        pre, 100, $"{label} olay öncesi kayıt", s => row.Pre = s)
                    : MutedText("—", HorizontalAlignment.Left), 4);
                Put(grid, row.Post is { } post
                    ? Combo(RecordSettings.WithCurrent(RecordSettings.PostOptions, settings.PostSeconds).Select(s => (s, RecorderText.Seconds(s))),
                        post, 100, $"{label} olay sonrası kayıt", s => row.Post = s)
                    : MutedText("—", HorizontalAlignment.Left), 5);
            }
            panel.Children.Add(TableRow(grid, last: i == rows.Count - 1, height: 44));
        }
        var card = Card(panel);
        panel.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(card);

        var foot = Text("ÖNCE / SONRA: hareket gibi bir olaydan önce ve sonra kaç saniye kaydedileceği.", "RowHelp");
        foot.Margin = new Thickness(16, 8, 16, 0);
        TabContent.Children.Add(foot);
    }

    static Border Centered(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border { Child = element };
    }

    List<ConfigWrite> PlanRecord() =>
        (_record ?? []).Where(r => r.Read.Ok)
            .Select(r => RecorderPlan.Record(r.Camera.Channel, CameraLabel(r.Camera), r.Read.Value!, r.Enabled, r.Preset, r.Pre, r.Post))
            .OfType<ConfigWrite>().ToList();

    void RevertRecord()
    {
        foreach (var row in _record ?? [])
        {
            var v = row.Read.Value;
            row.Enabled = v?.Enabled;
            row.Preset = v?.Preset;
            row.Pre = v?.PreSeconds;
            row.Post = v?.PostSeconds;
        }
        _results.Remove(Tab.Record);
        Render();
    }

    void ApplyRecord() => Apply(Tab.Record, PlanRecord(), () => _record = null);

    // ================================================================ Sistem

    static readonly GridLength[] DiskColumns =
        [new(1, GridUnitType.Star), new(100), new(140), new(120), new(120), new(120)];

    void RenderSystem()
    {
        if (_system is not { } data)
        {
            if (ShowFailed(Tab.System, () => _system = null))
            {
                return;
            }
            TabContent.Children.Add(LoadingCard());
            Load(Tab.System, async (device, isapi, ct) =>
            {
                var info = await RecorderReader.TryCallAsync<DeviceDetails>(async () => await isapi.GetDeviceInfoAsync(ct));
                var time = await RecorderReader.TryAsync(isapi, IsapiPaths.Time, DeviceTime.Parse, ct);
                var readAt = DateTimeOffset.Now;
                var ntp = await RecorderReader.TryAsync(isapi, IsapiPaths.NtpServers, NtpInfo.Parse, ct);
                var disks = await RecorderReader.TryAsync(isapi, IsapiPaths.Hdd, HddInfo.ParseList, ct);
                if (device == _device)
                {
                    _system = new SystemData { Info = info, Time = time, Ntp = ntp, Disks = disks, ReadAt = readAt, Name = info.Value?.Name };
                }
            });
            return;
        }

        // Sistem: device name + clock.
        var panel = new StackPanel();
        panel.Children.Add(CardHeader("Sistem", "Kayıt cihazının adı ve saati.",
            [SettingsButton("Geri al", (_, _) => RevertSystem()), SettingsButton("Cihaza uygula", (_, _) => ApplySystem(), "SettingsAccentButton")]));
        if (ResultBanner(Tab.System) is { } banner)
        {
            panel.Children.Add(banner);
        }
        panel.Children.Add(Row("Cihaz adı", "Kayıt cihazının kendi adı (web arayüzünde ve istemcilerde görünür).",
            data.Info.Value is { } info
                ? RowTextBox(data.Name ?? info.Name, 32, "Cihaz adı", v => data.Name = v)
                : MutedText(RecorderText.Unreadable)));

        UIElement clockControl;
        if (data.Time.Value is not null)
        {
            var clockGrid = new Grid { Width = ControlWidth };
            clockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            clockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var clock = Clock(data.Time, data.ReadAt);
            clock.HorizontalAlignment = HorizontalAlignment.Left;
            clock.VerticalAlignment = VerticalAlignment.Center;
            clockGrid.Children.Add(clock);
            var sync = new ToggleButton { Content = "Bilgisayarla eşitle", IsChecked = data.SyncTime, ToolTip = "Uygulanınca cihaz saati bu bilgisayarın saati ve saat dilimiyle eşitlenir" };
            sync.SetResourceReference(StyleProperty, "SettingsToggleButton");
            AutomationProperties.SetName(sync, "Bilgisayarla eşitle");
            sync.Checked += (_, _) => data.SyncTime = true; // not Click: UI Automation toggles without one
            sync.Unchecked += (_, _) => data.SyncTime = false;
            Grid.SetColumn(sync, 1);
            clockGrid.Children.Add(sync);
            clockControl = clockGrid;
        }
        else
        {
            clockControl = MutedText(RecorderText.Unreadable);
        }
        panel.Children.Add(Row("Tarih ve saat",
            "“Bilgisayarla eşitle” seçilip uygulanınca cihaz saati bu bilgisayarın saatine ve saat dilimine ayarlanır; saat modu “Elle” olur (NTP kapanır).",
            clockControl));

        var zone = data.Time.Value is { } t
            ? (HikTimeZone.Offset(t.TimeZone) is { } offset ? RecorderText.Utc(offset) : t.TimeZone ?? RecorderText.Unreadable)
            : RecorderText.Unreadable;
        panel.Children.Add(Row("Saat dilimi", "Cihazın saat dilimi; bu bilgisayarınki " + RecorderText.Utc(DateTimeOffset.Now.Offset) + ".", ValueText(zone)));
        panel.Children.Add(Row("NTP", "Saatin internetten otomatik eşitlenmesi (yalnızca gösterilir).",
            ValueText(data.Ntp.Value is { } ntp ? ntp.Describe(data.Time.Value) : RecorderText.Unreadable), last: true));
        TabContent.Children.Add(Card(panel));

        // Diskler; "Biçimlendir…" per disk is a risky operation (typed confirmation).
        var disks = new StackPanel();
        disks.Children.Add(CardHeader("Diskler", "Durum, kapasite ve boş alan. Biçimlendirme diskteki TÜM kayıtları siler; ayrıca yazarak onay istenir.", []));
        disks.Children.Add(TableHeader(DiskColumns, ("DİSK", false), ("TÜR", false), ("DURUM", false), ("KAPASİTE", false), ("BOŞ ALAN", false), ("İŞLEM", true)));
        if (data.Disks.Value is { Count: > 0 } list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var disk = list[i];
                var grid = TableGrid(DiskColumns);
                Put(grid, NameText(disk.Name.Length > 0 ? disk.Name : string.Create(CultureInfo.InvariantCulture, $"Disk {disk.Id}")), 0);
                Put(grid, Text(disk.Type.Length > 0 ? disk.Type : "—", "CellText"), 1);
                var status = Text(disk.StatusText, "CellText");
                status.SetResourceReference(TextBlock.ForegroundProperty, disk.Healthy ? "TextFg" : "WarningText");
                Put(grid, status, 2);
                Put(grid, Text(RecorderText.Size(disk.CapacityMb), "CellText"), 3);
                Put(grid, Text(RecorderText.Size(disk.FreeMb), "CellText"), 4);
                var name = disk.Name.Length > 0 ? disk.Name : string.Create(CultureInfo.InvariantCulture, $"Disk {disk.Id}");
                var format = SettingsButton("Biçimlendir…", (_, _) => FormatDisk(disk), "SettingsDangerButton");
                AutomationProperties.SetName(format, $"{name} biçimlendir");
                AutomationProperties.SetAutomationId(format, string.Create(CultureInfo.InvariantCulture, $"DiskFormat{disk.Id}"));
                if (string.Equals(disk.Status, "formatting", StringComparison.OrdinalIgnoreCase) || disk.Id <= 0)
                {
                    format.IsEnabled = false;
                }
                Put(grid, format, 5);
                disks.Children.Add(TableRow(grid, last: i == list.Count - 1, height: 44));
            }
        }
        else
        {
            var grid = TableGrid(DiskColumns);
            Put(grid, MutedText(data.Disks.Ok ? "Disk yok" : RecorderText.Unreadable, HorizontalAlignment.Left), 0, span: 6);
            disks.Children.Add(TableRow(grid, last: true));
        }
        disks.SetValue(RoundedClip.RadiusProperty, 7.0);
        TabContent.Children.Add(Card(disks, new Thickness(0, 16, 0, 0)));

        // Yeniden başlatma (risky: its own confirmation).
        var maintenance = new StackPanel();
        maintenance.Children.Add(CardHeader("Yeniden başlatma", "Kayıt cihazını uzaktan yeniden başlatır.", []));
        var reboot = SettingsButton("Yeniden başlat…", (_, _) => Reboot());
        AutomationProperties.SetAutomationId(reboot, "RecorderReboot");
        maintenance.Children.Add(Row("Yeniden başlat", "Kayıt birkaç dakika durur, canlı görüntü ve oynatma kesilir; ayrıca onay istenir.", reboot, last: true));
        TabContent.Children.Add(Card(maintenance, new Thickness(0, 16, 0, 0)));
    }

    List<ConfigWrite> PlanSystem() =>
        _system is { } s
            ? RecorderPlan.System(s.Info.Value?.Name, s.Name, s.Time.Value, s.SyncTime, () => DateTimeOffset.Now)
            : [];

    void RevertSystem()
    {
        if (_system is { } s)
        {
            s.Name = s.Info.Value?.Name;
            s.SyncTime = false;
        }
        _results.Remove(Tab.System);
        Render();
    }

    void ApplySystem() => Apply(Tab.System, PlanSystem(), () =>
    {
        _system = null;
        _overview = null; // name and clock are shown there too
    });

    // ================================================================ apply

    bool HasPendingChanges() =>
        PlanCameras().Count + PlanMotion().Count + PlanRecord().Count + PlanSystem().Count + PlanEvents().Count > 0 || NetworkPending();

    /// <summary>
    /// Confirmation → sequential writes (stop at the first failure) → result → re-read from the device.
    /// <paramref name="forget"/> drops the tab's data so the next render reads the device again.
    /// </summary>
    async void Apply(Tab tab, List<ConfigWrite> writes, Action forget)
    {
        if (_device is not { } device || _writing)
        {
            return;
        }
        var owner = Window.GetWindow(this);
        if (writes.Count == 0)
        {
            _results[tab] = (null, "Değişiklik yok; cihaza bir şey yazılmadı.");
            Render();
            return;
        }
        var lines = writes.SelectMany(w => w.Descriptions).ToList();
        var question = lines.Count == 1
            ? $"Bu değişiklik “{device.Name}” kayıt cihazına yazılsın mı?"
            : string.Create(CultureInfo.InvariantCulture, $"Bu {lines.Count} değişiklik “{device.Name}” kayıt cihazına sırayla yazılsın mı? Bir hata olursa kalanlar yazılmaz.");
        if (!ConfirmDialog.Show(owner, question, [(null, lines)], "Cihaza uygula", "Vazgeç"))
        {
            return;
        }

        _writing = true;
        _results[tab] = (null, "Uygulanıyor…");
        Render();
        UpdateEnabled();
        ConfigWriteReport report;
        try
        {
            using var isapi = new IsapiClient(device, WriteTimeout);
            report = await ConfigWriter.ApplyAsync(isapi, writes);
        }
        catch (IsapiWriteDeniedException ex)
        {
            report = new ConfigWriteReport([], null, ex.Message, lines);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            report = new ConfigWriteReport([], null,
                $"Beklenmeyen hata: {ex.Message} Hangi değişikliklerin uygulandığını yeniden okunan değerlerden kontrol edin.", lines);
        }
        finally
        {
            _writing = false;
            UpdateEnabled();
        }

        if (report.Success)
        {
            _results[tab] = (true, report.Applied.Count == 1 ? "Uygulandı." : string.Create(CultureInfo.InvariantCulture, $"Uygulandı · {report.Applied.Count} değişiklik."));
            Message?.Invoke($"Cihaza uygulandı: {device.Name}");
        }
        else
        {
            _results[tab] = (false, report.FailureMessage!);
            Message?.Invoke($"Cihaza uygulanamadı: {report.FailureMessage}");
            if (device == _device)
            {
                ConfirmDialog.Show(owner, report.FailureMessage!,
                    [("UYGULANDI", report.Applied), ("UYGULANMADI", report.NotApplied)], "Tamam", null);
            }
        }
        if (device == _device)
        {
            forget(); // the form shows what the device holds now
            Render();
        }
    }
}
