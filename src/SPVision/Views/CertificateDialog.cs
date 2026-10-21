using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Connects the Core trust-on-first-use gate (<see cref="CertificateTrust.Shared"/>) to the app: a changed HTTPS
/// certificate is asked about with <see cref="CertificateDialog"/> (one dialog at a time; Core coalesces the requests
/// that wait for it), and every stored, replaced or reset pin is saved to settings.json.
/// </summary>
static class CertificateTrustUi
{
    public static void Register()
    {
        CertificateTrust.Shared.Prompt = AskAsync;
        CertificateTrust.Shared.PinChanged += device => Application.Current?.Dispatcher.BeginInvoke(() => Store(device));
    }

    static Task<bool> AskAsync(Device device, CertificatePin? old, CertificatePin presented)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return Task.FromResult(false);
        }
        return dispatcher.InvokeAsync(() => CertificateDialog.Ask(Owner(), device, old, presented)).Task;
    }

    /// <summary>The active window (e.g. Ayarlar inside the main window), else the main window when shown; never a hidden one.</summary>
    static Window? Owner()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? (app.MainWindow is { IsVisible: true } main ? main : null);
    }

    /// <summary>
    /// UI thread. A saved device: save the settings. A draft of the device editor (same id, not in the list): its pin is
    /// taken over by the saved device only when both point at the same HTTPS address; otherwise Kaydet stores it.
    /// </summary>
    static void Store(Device device)
    {
        if (!App.Settings.Devices.Contains(device))
        {
            var saved = App.Settings.Devices.FirstOrDefault(d => d.Id == device.Id);
            if (saved is null || !saved.UseHttps || !device.UseHttps ||
                !string.Equals(saved.Host, device.Host, StringComparison.OrdinalIgnoreCase) || saved.HttpPort != device.HttpPort)
            {
                return;
            }
            saved.Certificate = device.Certificate;
        }
        try
        {
            App.SaveSettings();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Not fatal: the pin stays in memory and is saved with the next settings change.
        }
    }
}

/// <summary>
/// "Kayıt cihazının güvenlik sertifikası değişti": why this can be normal and why it can be an attack, the stored and the
/// presented certificate side by side (short fingerprint, validity; full details in the tool tip), and two equal
/// buttons. "Bağlanma" has the focus and Esc / close mean the same; Enter does not trust (no default button).
/// </summary>
static class CertificateDialog
{
    const double ButtonWidth = 168;

    public static bool Ask(Window? owner, Device device, CertificatePin? old, CertificatePin presented)
    {
        var dialog = DialogChrome.Create(owner, "Sertifika değişti");
        var content = new StackPanel { Width = 520, Margin = new Thickness(24) };

        var title = new TextBlock { Text = "Kayıt cihazının güvenlik sertifikası değişti", TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(FrameworkElement.StyleProperty, "CardTitle");
        title.TextTrimming = TextTrimming.None;
        content.Children.Add(title);

        var name = string.IsNullOrWhiteSpace(device.Name) ? device.Host : device.Name;
        var where = new TextBlock { Text = $"{name} · {device.Host}:{device.HttpPort} (HTTPS)", Margin = new Thickness(0, 4, 0, 0) };
        where.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        content.Children.Add(where);

        content.Children.Add(DialogChrome.WarningBand(
            "Kayıt cihazında yazılım güncellemesi, fabrika ayarlarına dönüş ya da sertifika yenilemesi yapıldıysa bu normaldir. " +
            "Ancak ağdaki biri kendini kayıt cihazı gibi gösteriyor da olabilir. Değişikliği siz ya da yöneticiniz yapmadıysanız bağlanmayın.",
            new Thickness(0, 12, 0, 0)));

        content.Children.Add(Comparison(old, presented));

        var note = new TextBlock
        {
            Text = "Sertifika doğrulanmadan cihaza hiçbir bilgi (şifre dahil) gönderilmedi. “Bağlanma” seçilirse SPVision bu cihazın " +
                   "HTTPS bağlantısını (ayarlar, kayıt arama, anlık görüntü) kurmaz; Ayarlar › Cihaz yönetimi'nden yeniden deneyebilirsiniz.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        note.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        content.Children.Add(note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var decline = new Button { Content = "Bağlanma", IsCancel = true };
        decline.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        decline.Width = ButtonWidth;
        AutomationProperties.SetAutomationId(decline, "CertificateDecline");
        AutomationProperties.SetName(decline, "Bağlanma");
        var trust = new Button { Content = "Yeni sertifikaya güven", Margin = new Thickness(8, 0, 0, 0) };
        trust.SetResourceReference(FrameworkElement.StyleProperty, "SettingsDangerButton");
        trust.Width = ButtonWidth;
        AutomationProperties.SetAutomationId(trust, "CertificateTrust");
        AutomationProperties.SetName(trust, "Yeni sertifikaya güven");
        trust.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(decline);
        buttons.Children.Add(trust);
        content.Children.Add(buttons);

        dialog.Content = content;
        dialog.Loaded += (_, _) => decline.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>Label column 96 | stored | 16 | presented (two equal columns), in a bordered box like the change lists.</summary>
    static Border Comparison(CertificatePin? old, CertificatePin presented)
    {
        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        foreach (var width in new[] { new GridLength(96), new GridLength(1, GridUnitType.Star), new GridLength(16), new GridLength(1, GridUnitType.Star) })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        for (var i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        }

        void Put(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        TextBlock Cell(string text, string style, string? tip = null, bool mono = false)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = tip ?? text };
            block.SetResourceReference(FrameworkElement.StyleProperty, style);
            if (mono)
            {
                block.FontFamily = new FontFamily("Consolas");
            }
            return block;
        }

        Put(Cell("KAYITLI", "CaptionText"), 0, 1);
        Put(Cell("CİHAZIN SUNDUĞU", "CaptionText"), 0, 3);
        Put(Cell("Parmak izi", "CellText"), 1, 0);
        Put(Cell("Geçerlilik", "CellText"), 2, 0);

        var oldPrint = Cell(old is null ? "—" : CertificateFingerprint.Short(old.SpkiSha256), "CellText", old is null ? null : CertificateText.Details(old), mono: true);
        AutomationProperties.SetAutomationId(oldPrint, "CertificateOldFingerprint");
        Put(oldPrint, 1, 1);
        Put(Cell(old is null ? "—" : CertificateText.Validity(old), "CellText"), 2, 1);

        var newPrint = Cell(CertificateFingerprint.Short(presented.SpkiSha256), "CellText", CertificateText.Details(presented), mono: true);
        AutomationProperties.SetAutomationId(newPrint, "CertificateNewFingerprint");
        Put(newPrint, 1, 3);
        Put(Cell(CertificateText.Validity(presented), "CellText"), 2, 3);

        foreach (var label in grid.Children.OfType<TextBlock>().Where(t => Grid.GetColumn(t) == 0))
        {
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }

        var box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 12, 0, 0), Child = grid };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        box.SetResourceReference(Border.BackgroundProperty, "CardBg");
        return box;
    }
}
