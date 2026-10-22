using System.Windows;
using System.Windows.Controls;

namespace SPVision.Views;

/// <summary>
/// Small themed question with named buttons (MessageBox cannot rename its buttons). Buttons sit bottom right like the
/// device editor footer: the secondary one, then the accent action; Enter = accept, Esc / close = cancel.
/// The list overload shows one line per item (e.g. "&lt;kamera&gt; · &lt;alan&gt;: &lt;eski&gt; → &lt;yeni&gt;") in a scrolling box.
/// </summary>
static class ConfirmDialog
{
    /// <summary><paramref name="danger"/>: the accept button is red-filled (deleting something, like Bakım › Tam sıfırla…).</summary>
    public static bool Show(Window? owner, string text, string accept, string cancel, bool danger = false) =>
        Show(owner, text, null, accept, cancel, danger);

    /// <summary>
    /// <paramref name="sections"/>: optional captioned lists ("UYGULANDI", "UYGULANMADI"…); a null caption shows the
    /// lines without one. <paramref name="cancel"/> null = a single (accept) button, for results.
    /// </summary>
    public static bool Show(Window? owner, string text, IReadOnlyList<(string? Caption, IReadOnlyList<string> Lines)>? sections,
        string accept, string? cancel, bool danger = false)
    {
        var dialog = new Window
        {
            Title = "SPVision",
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "WindowBg");
        dialog.SetResourceReference(Control.ForegroundProperty, "TextFg");
        dialog.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        dialog.SetResourceReference(Control.FontSizeProperty, "FontSizeBase");

        var hasList = sections is { Count: > 0 };
        var content = new StackPanel { Width = hasList ? 520 : 360, Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, hasList ? 12 : 24) });

        if (hasList)
        {
            var list = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            foreach (var (caption, lines) in sections!)
            {
                if (lines.Count == 0)
                {
                    continue;
                }
                if (caption is not null)
                {
                    var head = new TextBlock { Text = caption, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 10, 0, 4) };
                    head.SetResourceReference(FrameworkElement.StyleProperty, "CaptionText");
                    list.Children.Add(head);
                }
                foreach (var line in lines)
                {
                    var row = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                    row.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeList");
                    list.Children.Add(row);
                }
            }
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 24),
                Child = new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            };
            box.SetResourceReference(Border.BorderBrushProperty, "Border");
            box.SetResourceReference(Border.BackgroundProperty, "CardBg");
            content.Children.Add(box);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var acceptButton = new Button { Content = accept, IsDefault = true };
        if (danger)
        {
            dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/SPVision;component/Themes/RecorderControls.xaml", UriKind.Relative),
            });
        }
        acceptButton.SetResourceReference(FrameworkElement.StyleProperty, danger ? "SettingsDangerFillButton" : "SettingsAccentButton");
        acceptButton.Click += (_, _) => dialog.DialogResult = true;
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, IsCancel = true };
            cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
            buttons.Children.Add(cancelButton);
            acceptButton.Margin = new Thickness(8, 0, 0, 0);
        }
        buttons.Children.Add(acceptButton);
        content.Children.Add(buttons);

        dialog.Content = content;
        dialog.Loaded += (_, _) => acceptButton.Focus();
        return dialog.ShowDialog() == true;
    }
}
