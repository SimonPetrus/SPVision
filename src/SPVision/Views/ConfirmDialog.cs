using System.Windows;
using System.Windows.Controls;

namespace SPVision.Views;

/// <summary>
/// Small themed question with named buttons (MessageBox cannot rename its buttons). Buttons sit bottom right like the
/// device editor footer: the secondary one, then the accent action; Enter = accept, Esc / close = cancel.
/// </summary>
static class ConfirmDialog
{
    public static bool Show(Window? owner, string text, string accept, string cancel)
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

        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24) };

        var cancelButton = new Button { Content = cancel, IsCancel = true };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        var acceptButton = new Button { Content = accept, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        acceptButton.SetResourceReference(FrameworkElement.StyleProperty, "SettingsAccentButton");
        acceptButton.Click += (_, _) => dialog.DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(acceptButton);

        var content = new StackPanel { Width = 360, Margin = new Thickness(24) };
        content.Children.Add(message);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => acceptButton.Focus();
        return dialog.ShowDialog() == true;
    }
}
