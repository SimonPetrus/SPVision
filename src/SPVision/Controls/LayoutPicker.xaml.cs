using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using SPVision.Core;

namespace SPVision.Controls;

/// <summary>
/// Top bar layout picker: one button showing the current layout (mini diagram + tile count) that opens
/// a transient popup with two sections, STANDART and BÜYÜK KARELİ. Picking a layout applies it and closes
/// the popup. Live view offers every layout (6 + 6); playback 1, 4, 9, 16 and 6, 8, 10, 13 (4 + 4).
/// </summary>
public partial class LayoutPicker : UserControl
{
    /// <summary>Unselected cells are dimmed; the selected item shows them at full strength.</summary>
    const double IdleDiagramOpacity = 0.55;

    /// <summary>One column of the popup: a 56 px item plus its 4 px margin on both sides.</summary>
    const double ColumnWidth = 64;

    /// <summary>Horizontal margin of the item grids (+ 4 px item margin = 14 px from the panel edge) and the panel border.</summary>
    const double GridMargin = 10;
    const double PanelBorderWidth = 1;

    readonly List<(ToggleButton Item, LayoutDiagram Diagram, GridLayout Layout)> _items = [];
    GridLayout _current = GridLayout.Default;

    public LayoutPicker()
    {
        InitializeComponent();
        TransientPopup.Attach(Panel, Toggle);
        TransientPopup.Place(Panel, (popup, target, scale) => new Point(target.Width - popup.Width, target.Height + 4 * scale));
        Panel.Opened += (_, _) => Dispatcher.BeginInvoke(() => _items.FirstOrDefault(i => i.Item.IsChecked == true).Item?.Focus());
        SetLayouts(GridLayout.Standard, GridLayout.BigTile);
        Current = GridLayout.Default;
    }

    /// <summary>The user picked a layout (the popup is already closed).</summary>
    public event Action<GridLayout>? LayoutSelected;

    /// <summary>Layout shown on the button and highlighted in the popup.</summary>
    public GridLayout Current
    {
        get => _current;
        set
        {
            _current = value;
            CurrentDiagram.Layout = value;
            CurrentCount.Text = value.TileCount.ToString(CultureInfo.InvariantCulture);
            System.Windows.Automation.AutomationProperties.SetName(Toggle, $"Izgara düzeni seç, şu an {value.TileCount} kare");
            foreach (var (item, diagram, layout) in _items)
            {
                var on = ReferenceEquals(layout, value);
                item.IsChecked = on;
                diagram.Opacity = on ? 1 : IdleDiagramOpacity;
            }
        }
    }

    /// <summary>Which layouts the two sections offer.</summary>
    public void SetLayouts(IReadOnlyList<GridLayout> standard, IReadOnlyList<GridLayout> bigTile)
    {
        _items.Clear();
        // Live 6 + 6, playback 4 + 4: equal item widths and gaps in both, the panel as wide as its columns.
        var columns = Math.Max(1, Math.Max(standard.Count, bigTile.Count));
        StandardItems.Columns = columns;
        BigItems.Columns = columns;
        PanelBorder.Width = columns * ColumnWidth + 2 * GridMargin + 2 * PanelBorderWidth;
        Fill(StandardItems, standard, "kareli eşit ızgara");
        Fill(BigItems, bigTile, "kareli, büyük kareli ızgara");
        BigSection.Visibility = bigTile.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Current = _current;
    }

    void Fill(UniformGrid grid, IReadOnlyList<GridLayout> layouts, string description)
    {
        grid.Children.Clear();
        foreach (var layout in layouts)
        {
            var diagram = new LayoutDiagram { Layout = layout, Width = 40, Height = 28 };
            var number = new TextBlock
            {
                Text = layout.TileCount.ToString(CultureInfo.InvariantCulture),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
            };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            var item = new ClickToggleButton
            {
                Style = (Style)FindResource("LayoutItem"),
                Content = new StackPanel { Children = { diagram, number } },
            };
            System.Windows.Automation.AutomationProperties.SetName(item, $"{layout.TileCount} {description}");
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, $"Layout{layout.TileCount}");
            item.Click += (_, _) =>
            {
                Panel.IsOpen = false;
                Current = layout;
                LayoutSelected?.Invoke(layout);
            };
            _items.Add((item, diagram, layout));
            grid.Children.Add(item);
        }
    }
}
