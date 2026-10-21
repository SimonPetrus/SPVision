using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// The separate confirmation of a risky recorder operation (reboot, disk format, network address, users): title,
/// warning band with the consequences, the "eski → yeni" lines and, for a disk format, a box where the word
/// <see cref="RiskyWrites.FormatWord"/> must be typed. The only <see cref="IRiskyConfirmation"/> of the app, so a
/// <see cref="ConfirmedRiskyWrite"/> exists only after this dialog was accepted. Vazgeç has the focus; Enter does not
/// accept (no default button); Esc / close = cancel.
/// </summary>
sealed class RiskyConfirmDialog(Window? owner) : IRiskyConfirmation
{
    internal static readonly Geometry WarningIcon = Geometry.Parse("M12,3 L22,20 L2,20 Z M12,10 V14 M12,17 H12.01");

    public string? Ask(RiskyWriteRequest request)
    {
        var dialog = DialogChrome.Create(owner, "Onay gerekiyor");
        var content = new StackPanel { Width = 520, Margin = new Thickness(24) };

        var title = new TextBlock { Text = request.Title, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(FrameworkElement.StyleProperty, "CardTitle");
        title.TextTrimming = TextTrimming.None;
        content.Children.Add(title);

        content.Children.Add(DialogChrome.WarningBand(request.Warning, new Thickness(0, 12, 0, 0)));

        if (request.Changes.Count > 0)
        {
            var list = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            foreach (var line in request.Changes)
            {
                var row = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                row.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeList");
                list.Children.Add(row);
            }
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new ScrollViewer { MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            };
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            box.SetResourceReference(Border.BackgroundProperty, "CardBg");
            content.Children.Add(box);
        }

        var accept = new Button { Content = request.Accept };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "SettingsDangerButton");
        AutomationProperties.SetAutomationId(accept, "RiskyAccept");
        AutomationProperties.SetName(accept, request.Accept);
        TextBox? word = null;
        if (request.RequiredWord is { } required)
        {
            var label = new TextBlock { Text = $"Onaylamak için {required} yazın", Margin = new Thickness(0, 16, 0, 4) };
            label.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
            content.Children.Add(label);
            word = new TextBox { MaxLength = 32 };
            word.SetResourceReference(FrameworkElement.StyleProperty, "RowTextBox");
            AutomationProperties.SetAutomationId(word, "RiskyWord");
            AutomationProperties.SetName(word, $"Onaylamak için {required} yazın");
            content.Children.Add(word);
            accept.IsEnabled = false;
            word.TextChanged += (_, _) => accept.IsEnabled = RiskyWrites.WordMatches(required, word.Text);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Vazgeç", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        AutomationProperties.SetAutomationId(cancel, "RiskyCancel");
        AutomationProperties.SetName(cancel, "Vazgeç");
        accept.Margin = new Thickness(8, 0, 0, 0);
        accept.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        content.Children.Add(buttons);

        dialog.Content = content;
        dialog.Loaded += (_, _) => (word is null ? cancel : (UIElement)word).Focus();
        return dialog.ShowDialog() == true ? word?.Text ?? "" : null;
    }
}

/// <summary>Shared look of the settings dialogs (themed window, page styles, warning band).</summary>
static class DialogChrome
{
    public static Window Create(Window? owner, string title)
    {
        var dialog = new Window
        {
            Title = $"SPVision · {title}",
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        // Page styles (30 px text boxes, drop-downs) of Settings › Cihaz ayarları.
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/SPVision;component/Themes/RecorderControls.xaml", UriKind.Relative),
        });
        dialog.SetResourceReference(Control.BackgroundProperty, "WindowBg");
        dialog.SetResourceReference(Control.ForegroundProperty, "TextFg");
        dialog.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        dialog.SetResourceReference(Control.FontSizeProperty, "FontSizeBase");
        return dialog;
    }

    /// <summary>Amber band: triangle icon + text (same as the band under a risky card header).</summary>
    public static Border WarningBand(string text, Thickness margin, double radius = 6)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new GlyphIcon { Data = RiskyConfirmDialog.WarningIcon, Size = 15, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(GlyphIcon.ForegroundProperty, "WarningText");
        grid.Children.Add(icon);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        message.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTile");
        message.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
        Grid.SetColumn(message, 1);
        grid.Children.Add(message);
        var band = new Border { Child = grid, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(radius), Margin = margin };
        band.SetResourceReference(Border.BackgroundProperty, "WarningSoftBg");
        AutomationProperties.SetName(band, text);
        return band;
    }
}
