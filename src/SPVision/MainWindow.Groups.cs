using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using SPVision.Controls;
using SPVision.Core;
using SPVision.Views;

namespace SPVision;

/// <summary>
/// Live sidebar › GRUPLAR (camera groups, Core <see cref="CameraGroups"/>): "Grup kaydet" stores the layout and the camera of
/// every tile on screen; clicking a row brings it back (<see cref="LiveView.ShowGroup"/>); the ⋯ menu updates it from the
/// screen, renames or deletes it. The built-in first row "Tüm kameralar" is the normal arrangement. Nothing here talks to
/// a device: groups live in the settings only.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width of the ⋯ menu; its right edge sits under the row's right edge.</summary>
    const double GroupMenuWidth = 200;

    /// <summary>The group on screen changed (row click, device row double-click, startup): rows and status bar follow.</summary>
    void GroupsChanged()
    {
        BuildGroupList();
        UpdateStatusBar();
    }

    /// <summary>"Tüm kameralar" first, then the saved groups in the order they were made.</summary>
    void BuildGroupList()
    {
        // A row picked with the keyboard is replaced by the rebuild: the focus goes to the new active row.
        var hadFocus = GroupList.IsKeyboardFocusWithin;
        GroupList.Children.Clear();
        var listed = LiveView.ListedCameras();
        var active = Live.ActiveGroup;
        GroupList.Children.Add(GroupRow(null, GridLayout.Default, CameraGroups.AllCamerasName, listed.Count, active is null, 0));
        var index = 1;
        foreach (var group in App.Settings.Groups)
        {
            GroupList.Children.Add(GroupRow(group, CameraGroups.LayoutOf(group), group.Name, CameraGroups.CameraCount(group, listed),
                active?.Id == group.Id, index++));
        }
        if (hadFocus && GroupList.Children.OfType<Border>()
                .Select(r => ((Grid)r.Child).Children.OfType<ToggleButton>().First())
                .FirstOrDefault(p => p.IsChecked == true) is { } current)
        {
            current.Focus();
        }
    }

    /// <summary>
    /// One 32 px row: 22×16 mini diagram of the layout (the layout picker's drawing; accent cells when active) · name
    /// (semibold when active) · camera count · 28 px ⋯ button (kept as empty space on "Tüm kameralar", so the counts line up).
    /// </summary>
    Border GroupRow(CameraGroup? group, GridLayout layout, string name, int count, bool active, int index)
    {
        var diagram = new LayoutDiagram { Layout = layout, Width = 22, Height = 16, Gap = 1, Opacity = active ? 1 : 0.85 };
        if (active)
        {
            diagram.SetResourceReference(LayoutDiagram.ForegroundProperty, "Accent");
        }
        var title = new TextBlock
        {
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var number = new TextBlock
        {
            Text = count.ToString(CultureInfo.InvariantCulture),
            FontSize = Res<double>("FontSizeSmall"),
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        }.WithBrush(TextBlock.ForegroundProperty, "TextSecondary");
        Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
        var content = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        content.Children.Add(diagram);
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        Grid.SetColumn(number, 2);
        content.Children.Add(number);

        var pick = new ClickToggleButton
        {
            Style = Res<Style>("GroupRowButton"),
            Content = content,
            IsChecked = active,
            ToolTip = group is null
                ? "Tüm kameralar, liste sırasıyla, varsayılan düzende"
                : $"{name} · {layout.TileCount} kareli ızgara, {count} kamera",
        };
        AutomationProperties.SetName(pick, $"{name}, {count} kamera");
        AutomationProperties.SetAutomationId(pick, group is null ? "GroupAll" : $"Group{index}");
        pick.Click += (_, _) =>
        {
            pick.IsChecked = active; // the row shows what is on screen; the list is rebuilt when that changes
            Live.ShowGroup(group);
        };

        var more = new Button
        {
            Style = Res<Style>("GroupMoreButton"),
            Content = new GlyphIcon { Data = Res<Geometry>("IconMore"), Size = 14, StrokeThickness = 3.6 },
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = group is null ? Visibility.Hidden : Visibility.Visible,
            ToolTip = "Seçenekler",
        };
        AutomationProperties.SetName(more, $"{name} grubu seçenekleri");
        AutomationProperties.SetAutomationId(more, $"GroupMenu{index}");

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(28) } },
        };
        grid.Children.Add(pick);
        Grid.SetColumn(more, 1);
        grid.Children.Add(more);
        var row = new Border
        {
            Style = Res<Style>("GroupRow"),
            Child = grid,
            Margin = new Thickness(0, index == 0 ? 0 : 2, 0, 0),
        };
        if (active)
        {
            row.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        }
        if (group is not null)
        {
            more.Click += (_, _) => OpenGroupMenu(row, group);
        }
        return row;
    }

    /// <summary>⋯ menu, 200 px, right edge under the row's right edge, right below the row.</summary>
    void OpenGroupMenu(Border row, CameraGroup group)
    {
        var menu = new ContextMenu
        {
            Style = Res<Style>("GroupMenu"),
            PlacementTarget = row,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = row.ActualWidth - GroupMenuWidth,
        };
        menu.Items.Add(GroupMenuItem("Ekrandaki düzenle güncelle", "GroupMenuUpdate", "TextFg", () => UpdateGroup(group)));
        menu.Items.Add(GroupMenuItem("Yeniden adlandır…", "GroupMenuRename", "TextFg", () => RenameGroup(group)));
        menu.Items.Add(GroupMenuItem("Sil", "GroupMenuDelete", "DangerText", () => DeleteGroup(group)));
        menu.IsOpen = true;
    }

    MenuItem GroupMenuItem(string header, string automationId, string foreground, Action action)
    {
        var item = new MenuItem { Header = header, Style = Res<Style>("GroupMenuItem") };
        item.SetResourceReference(Control.ForegroundProperty, foreground);
        AutomationProperties.SetAutomationId(item, automationId);
        // After the menu has closed, so a dialog opened by the action gets the focus.
        item.Click += (_, _) => Dispatcher.BeginInvoke(action);
        return item;
    }

    /// <summary>"Grup kaydet": name dialog, then the new group is stored and is the one on screen.</summary>
    void SaveGroup_Click(object sender, RoutedEventArgs e)
    {
        var layout = Live.Layout;
        var screen = Live.ScreenCameras.ToList();
        var name = GroupDialog.Save(this, CameraGroups.UniqueName(App.Settings.Groups), layout,
            screen.Count(c => c is not null), App.Settings.Groups);
        if (name is null)
        {
            return;
        }
        var group = CameraGroups.Capture(name, layout, screen);
        App.Settings.Groups.Add(group);
        App.SaveSettings();
        Live.ShowGroup(group); // same tiles: nothing reconnects; the list is rebuilt (GroupChanged)
        ShowMessage($"Grup kaydedildi: {group.Name}");
    }

    /// <summary>"Ekrandaki düzenle güncelle": the group takes the layout and cameras on screen and is shown.</summary>
    void UpdateGroup(CameraGroup group)
    {
        CameraGroups.Overwrite(group, Live.Layout, Live.ScreenCameras.ToList());
        App.SaveSettings();
        Live.ShowGroup(group);
        GroupsChanged(); // diagram and count changed even when the group was on screen already
        ShowMessage($"Grup güncellendi: {group.Name}");
    }

    void RenameGroup(CameraGroup group)
    {
        if (GroupDialog.Rename(this, group, App.Settings.Groups) is not { } name)
        {
            return;
        }
        group.Name = name;
        App.SaveSettings();
        GroupsChanged();
    }

    /// <summary>"Sil" after a short question; deleting the group on screen shows "Tüm kameralar".</summary>
    void DeleteGroup(CameraGroup group)
    {
        if (!ConfirmDialog.Show(this, $"“{group.Name}” grubu silinsin mi? Kameralar ve kayıtlar etkilenmez.", "Sil", "İptal", danger: true))
        {
            return;
        }
        var wasActive = App.Settings.ActiveGroup == group.Id;
        App.Settings.Groups.Remove(group);
        App.SaveSettings();
        if (wasActive)
        {
            Live.ShowGroup(null); // GroupChanged rebuilds the list
        }
        else
        {
            GroupsChanged();
        }
        ShowMessage($"Grup silindi: {group.Name}");
    }

    /// <summary>Status bar, live view only: "Grup: &lt;ad&gt;" next to "n / m kamera ekranda".</summary>
    void UpdateGroupStatus()
    {
        GroupText.Visibility = _section == MainSection.Live ? Visibility.Visible : Visibility.Collapsed;
        GroupText.Text = CameraGroups.StatusText(Live.ActiveGroup);
    }
}
