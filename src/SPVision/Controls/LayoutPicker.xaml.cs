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
/// the popup. Live view offers every layout; playback only those up to 16 tiles.
/// </summary>
public partial class LayoutPicker : UserControl
{
    /// <summary>Unselected cells are dimmed; the selected item shows them at full strength.</summary>
    const double IdleDiagramOpacity = 0.55;

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
            var item = new ToggleButton
            {
                Style = (Style)FindResource("LayoutItem"),
                Content = new StackPanel { Children = { diagram, number } },
            };
            System.Windows.Automation.AutomationProperties.SetName(item, $"{layout.TileCount} {description}");
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
