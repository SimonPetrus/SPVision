using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Name dialog of the camera groups (approved mockup): 400 px (360 + 20 px on each side), 14 px between the blocks.
/// "Grup kaydet": title · "Grup adı" box (prefilled, selected) · preview (mini diagram of the layout + what is stored) ·
/// İptal / Kaydet (120×30, right). "Yeniden adlandır": the same without the preview. The name is checked with
/// <see cref="CameraGroups.CheckName"/> (not empty, unique ignoring case); the problem shows under the box.
/// </summary>
static class GroupDialog
{
    const double ContentWidth = 360;
    const double Gap = 14;

    /// <summary>Returns the trimmed name, or null when cancelled.</summary>
    public static string? Save(Window? owner, string name, GridLayout layout, int cameras, IReadOnlyList<CameraGroup> groups) =>
        Show(owner, "Grup kaydet", name, (layout, cameras), groups, null);

    /// <summary>Returns the trimmed new name, or null when cancelled.</summary>
    public static string? Rename(Window? owner, CameraGroup group, IReadOnlyList<CameraGroup> groups) =>
        Show(owner, "Yeniden adlandır", group.Name, null, groups, group.Id);

    static string? Show(Window? owner, string title, string name, (GridLayout Layout, int Cameras)? preview,
        IReadOnlyList<CameraGroup> groups, Guid? except)
    {
        var dialog = DialogChrome.Create(owner, title);
        var content = new StackPanel { Width = ContentWidth, Margin = new Thickness(20) };
        var head = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeBrand");
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        content.Children.Add(head);

        var label = new TextBlock { Text = "Grup adı", Margin = new Thickness(0, Gap, 0, 6) };
        label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
        content.Children.Add(label);
        var box = new TextBox { Text = name, Height = 30, MinHeight = 0, MaxLength = CameraGroups.MaxNameLength };
        box.SetResourceReference(Control.FontSizeProperty, "FontSizeList");
        AutomationProperties.SetName(box, "Grup adı");
        AutomationProperties.SetAutomationId(box, "GroupName");
        content.Children.Add(box);

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        error.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTile");
        AutomationProperties.SetAutomationId(error, "GroupNameError");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        box.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;

        if (preview is { } stored)
        {
            content.Children.Add(Preview(stored.Layout, stored.Cameras));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, Gap, 0, 0) };
        var cancel = new Button { Content = "İptal", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        AutomationProperties.SetAutomationId(cancel, "GroupDialogCancel");
        var save = new Button { Content = "Kaydet", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        save.SetResourceReference(FrameworkElement.StyleProperty, "SettingsAccentButton");
        AutomationProperties.SetAutomationId(save, "GroupDialogSave");
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        content.Children.Add(buttons);

        string? result = null;
        save.Click += (_, _) =>
        {
            if (CameraGroups.CheckName(box.Text, groups, except) is { } problem)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                box.Focus();
                box.SelectAll();
                return;
            }
            result = box.Text.Trim();
            dialog.DialogResult = true;
        };

        dialog.Content = content;
        dialog.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    /// <summary>Mini diagram (40×28, like the layout picker's items) and "Ekrandaki N kareli ızgara ve M kameranın yeri kaydedilir…".</summary>
    static Border Preview(GridLayout layout, int cameras)
    {
        var diagram = new LayoutDiagram { Layout = layout, Width = 40, Height = 28, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
        diagram.SetResourceReference(LayoutDiagram.ForegroundProperty, "TextFg");
        var text = new TextBlock
        {
            Text = CameraGroups.SaveSummary(layout, cameras),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTile");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() } };
        grid.Children.Add(diagram);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var box = new Border
        {
            Child = grid,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, Gap, 0, 0),
        };
        box.SetResourceReference(Border.BackgroundProperty, "CardBg"); // the dialog itself is WindowBg (like ConfirmDialog's list box)
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        AutomationProperties.SetAutomationId(box, "GroupPreview");
        AutomationProperties.SetName(box, text.Text);
        return box;
    }
}
