using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// "OLAYLAR": the events of this run (Core <see cref="EventCenter"/>, newest first) right of the video wall, opened with the
/// bell of the top bar. A filter (Tümü / Hareket / Bağlantı / Disk), one row per event — coloured icon (danger: lost camera,
/// disk; warning: smart, tamper; neutral: motion), title, time, camera or recorder, and for a known camera two equal buttons
/// "Canlı izle" / "Kaydı oynat"; unread rows bold on a lighter background — and "Tümünü okundu say". The header's settings
/// button turns the panel into "BİLDİRİM AYARLARI" (<see cref="EventNotificationSettings"/>: kinds, pop-up, sound, interval),
/// saved at once. Nothing here talks to a device.
/// </summary>
public partial class EventPanel : UserControl
{
    sealed record KindRow(AlertKind Kind, string Name, string Help);

    static readonly KindRow[] Kinds =
    [
        new(AlertKind.CameraOffline, "Kamera bağlantısı koptu", "Kamera veya kayıt cihazı yanıt vermezse"),
        new(AlertKind.Disk, "Disk sorunu", "Disk hatalı ya da takılı değilse"),
        new(AlertKind.Motion, "Hareket algılama", "Kamerada hareket algılama açıksa"),
        new(AlertKind.Smart, "Çizgi geçme / alan ihlali", "Akıllı algılama destekleyen kameralarda"),
        new(AlertKind.Tamper, "Görüntü kapatma", "Kameranın önü kapatılırsa"),
    ];

    EventCenter? _center;
    EventFilter _filter = EventFilter.All;
    bool _dirty = true;

    public EventPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _dirty)
            {
                Refresh();
            }
        };
    }

    /// <summary>"Canlı izle" of a row: the main window shows that camera live, alone.</summary>
    public event Action<DeviceEvent>? LiveRequested;

    /// <summary>"Kaydı oynat" of a row: Kayıt Oynatma on that camera, 10 s before the event.</summary>
    public event Action<DeviceEvent>? PlaybackRequested;

    public event Action? CloseRequested;

    /// <summary>Rows were marked read: the bell's badge follows.</summary>
    public event Action? ReadChanged;

    /// <summary>A notification setting changed (already saved): the alert streams start or stop with it.</summary>
    public event Action? SettingsChanged;

    public void Attach(EventCenter center)
    {
        _center = center;
        BuildSettings();
        Invalidate();
    }

    /// <summary>The events changed: the list is rebuilt now if shown, otherwise when it is shown next.</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (IsVisible)
        {
            Refresh();
        }
    }

    /// <summary>Opening from the bell always starts on the list.</summary>
    public void ShowList()
    {
        SettingsToggle.IsChecked = false;
        UpdateView();
    }

    // ---- List ----

    void Refresh()
    {
        _dirty = false;
        FilterAll.IsChecked = _filter == EventFilter.All;
        FilterMotion.IsChecked = _filter == EventFilter.Motion;
        FilterConnection.IsChecked = _filter == EventFilter.Connection;
        FilterDisk.IsChecked = _filter == EventFilter.Disk;
        EventList.Children.Clear();
        if (_center is null)
        {
            return;
        }
        foreach (var e in _center.Filtered(_filter))
        {
            EventList.Children.Add(Row(e));
        }
        var empty = EventList.Children.Count == 0;
        EmptyText.Text = _center.Events.Count == 0 ? "Henüz olay yok" : "Bu türde olay yok";
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        MarkReadButton.IsEnabled = _center.Unread > 0;
    }

    /// <summary>
    /// One event: 28 px icon box, 10 px gap, then title + time on one line, the camera / recorder under it and, for a known
    /// camera, two equal 24 px buttons 6 px apart. 10 px above and below, 14 px from the sides, 1 px line under it.
    /// </summary>
    Border Row(DeviceEvent e)
    {
        var (toneBg, toneFg, icon) = Tone(e.Kind);
        var iconBox = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new GlyphIcon { Data = (Geometry)FindResource(icon), Size = 15 }.WithBrush(GlyphIcon.ForegroundProperty, toneFg),
        }.WithBrush(Border.BackgroundProperty, toneBg);

        var title = new TextBlock
        {
            Text = e.Title,
            FontSize = (double)FindResource("FontSizeList"),
            FontWeight = e.Read ? FontWeights.Normal : FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom,
        }.WithBrush(TextBlock.ForegroundProperty, "TextFg");
        var time = new TextBlock
        {
            Text = e.TimeText,
            FontSize = (double)FindResource("FontSizeSmall"),
            Margin = new Thickness(8, 0, 0, 1), // baseline of the 12.5 px title
            VerticalAlignment = VerticalAlignment.Bottom,
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
        System.Windows.Documents.Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        head.Children.Add(title);
        Grid.SetColumn(time, 1);
        head.Children.Add(time);

        var text = new StackPanel();
        text.Children.Add(head);
        text.Children.Add(new TextBlock
        {
            Text = e.Place,
            FontSize = (double)FindResource("FontSizeStatus"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        if (e.Camera is not null)
        {
            var buttons = new Grid
            {
                Margin = new Thickness(0, 7, 0, 0),
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(6) }, new ColumnDefinition() },
            };
            var live = RowButton("Canlı izle", $"{e.Place}: canlı izle", () => LiveRequested?.Invoke(e));
            var playback = RowButton("Kaydı oynat", $"{e.Place}: kaydı oynat ({e.TimeText} − 10 sn)", () => PlaybackRequested?.Invoke(e));
            Grid.SetColumn(playback, 2);
            buttons.Children.Add(live);
            buttons.Children.Add(playback);
            text.Children.Add(buttons);
        }

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(28) },
                new ColumnDefinition { Width = new GridLength(10) },
                new ColumnDefinition(),
            },
        };
        grid.Children.Add(iconBox);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var row = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
            ToolTip = e.Read ? null : "Okunmadı",
        }.WithBrush(Border.BorderBrushProperty, "Divider");
        if (e.Read)
        {
            row.Background = Brushes.Transparent;
        }
        else
        {
            row.SetResourceReference(Border.BackgroundProperty, "UnreadRowBg");
        }
        System.Windows.Automation.AutomationProperties.SetName(row,
            $"{e.Title}, {e.Place}, {e.TimeText}{(e.Read ? "" : ", okunmadı")}");
        return row;
    }

    Button RowButton(string text, string name, Action click)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("EventRowButton") };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Icon box colours and icon of a kind: danger for lost cameras and disks, warning for smart and tamper events.</summary>
    internal static (string Background, string Foreground, string Icon) Tone(AlertKind kind) => kind switch
    {
        AlertKind.CameraOffline => ("DangerSoftBg", "DangerText", "IconEventOffline"),
        AlertKind.Disk => ("DangerSoftBg", "DangerText", "IconEventDisk"),
        AlertKind.Smart => ("WarningSoftBg", "WarningText", "IconEventSmart"),
        AlertKind.Tamper => ("WarningSoftBg", "WarningText", "IconEventTamper"),
        _ => ("HoverBg", "IconFg", "IconEventMotion"),
    };

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = sender == FilterMotion ? EventFilter.Motion
            : sender == FilterConnection ? EventFilter.Connection
            : sender == FilterDisk ? EventFilter.Disk
            : EventFilter.All;
        ListScroller.ScrollToTop();
        Refresh();
    }

    void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        _center?.MarkAllRead();
        Refresh();
        ReadChanged?.Invoke();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void SettingsToggle_Click(object sender, RoutedEventArgs e) => UpdateView();

    void UpdateView()
    {
        var settings = SettingsToggle.IsChecked == true;
        ListPane.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPane.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = settings ? "BİLDİRİM AYARLARI" : "OLAYLAR";
        if (!settings && _dirty)
        {
            Refresh();
        }
    }

    // ---- Settings ----

    void BuildSettings()
    {
        if (_center is null)
        {
            return;
        }
        var settings = _center.Settings;
        KindRows.Children.Clear();
        foreach (var kind in Kinds)
        {
            KindRows.Children.Add(SwitchRow(kind.Name, kind.Help, settings.IsOn(kind.Kind), on => settings.Set(kind.Kind, on)));
        }
        HowRows.Children.Clear();
        HowRows.Children.Add(SwitchRow("Windows bildirimi", "Uygulama küçültülmüşken de sağ altta çıkar", settings.Popup, on => settings.Popup = on));
        HowRows.Children.Add(SwitchRow("Ses çal", "Bildirimle birlikte kısa bir uyarı sesi", settings.Sound, on => settings.Sound = on));
        UpdateRate();
    }

    /// <summary>Name + help on the left, a 36×20 switch on the right (12 px apart); at least 48 px, 6 / 14 px inside, line under it.</summary>
    Border SwitchRow(string name, string help, bool on, Action<bool> set)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontSize = (double)FindResource("FontSizeList"), TextWrapping = TextWrapping.Wrap }
            .WithBrush(TextBlock.ForegroundProperty, "TextFg"));
        text.Children.Add(new TextBlock { Text = help, FontSize = (double)FindResource("FontSizeSmall"), TextWrapping = TextWrapping.Wrap }
            .WithBrush(TextBlock.ForegroundProperty, "TextSecondary"));
        var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitch"), IsChecked = on, ToolTip = help };
        System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        // After the initial value: only the user's changes are saved (keyboard, mouse and UI Automation alike).
        toggle.Checked += (_, _) => Changed(() => set(true));
        toggle.Unchecked += (_, _) => Changed(() => set(false));
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = new GridLength(12) },
                new ColumnDefinition { Width = new GridLength(36) },
            },
        };
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);
        return new Border
        {
            MinHeight = 48,
            Padding = new Thickness(14, 6, 14, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        }.WithBrush(Border.BorderBrushProperty, "Divider");
    }

    void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (_center is not null && sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var seconds))
        {
            Changed(() => _center.Settings.IntervalSeconds = seconds);
        }
        UpdateRate();
    }

    void UpdateRate()
    {
        var seconds = (int)(_center?.Settings.Interval.TotalSeconds ?? 60);
        foreach (var button in new ToggleButton[] { Rate30, Rate60, Rate300 })
        {
            button.IsChecked = (string)button.Tag == seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    void Changed(Action apply)
    {
        apply();
        App.SaveSettings();
        SettingsChanged?.Invoke();
    }
}
