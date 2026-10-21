using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SPVision.Core.Updates;
using SPVision.Updates;

namespace SPVision.Views;

/// <summary>
/// The update dialogs (same chrome as the settings dialogs, <see cref="DialogChrome"/>):
/// <list type="bullet">
/// <item><see cref="ShowNotes"/> — "Neler yeni" of an optional update: notes + Kapat / Güncelle.</item>
/// <item><see cref="ShowMandatory"/> — "Bu güncelleme zorunlu": notes + Çıkış / Güncelle, blocking; closing it is Çıkış, so
/// the app cannot be used without updating. Güncelle downloads here with progress; a declined UAC prompt leaves the dialog
/// open with "Güncelleme iptal edildi".</item>
/// </list>
/// Both: 520 px, buttons bottom right (secondary, then accent), focus on Güncelle.
/// </summary>
static class UpdateDialog
{
    const double ContentWidth = 520;

    /// <summary>True when the user pressed Güncelle.</summary>
    public static bool ShowNotes(Window owner, UpdateManifest manifest)
    {
        var dialog = DialogChrome.Create(owner, "Neler yeni");
        var content = Body($"SPVision {manifest.Version} · Neler yeni", Subtitle(manifest), manifest.Notes, null);
        var (buttons, cancel, accept) = Buttons("Kapat", "UpdateNotesClose");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        accept.IsDefault = true;
        accept.Click += (_, _) => dialog.DialogResult = true;
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => accept.Focus();
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// Blocks until the installer started (true: the caller closes the app) or the user chose Çıkış / closed the window
    /// (false: the caller closes the app too). There is no way back to the app.
    /// </summary>
    public static bool ShowMandatory(Window owner)
    {
        var dialog = DialogChrome.Create(owner, "Zorunlu güncelleme");
        var offer = UpdateController.Offer!;
        var band = DialogChrome.WarningBand(
            "Bu sürüm artık kullanılamıyor. Devam etmek için SPVision'u güncelleyin. Güncelleme yönetici izni ister; " +
            "kurulum bitince SPVision kendiliğinden yeniden açılır.", new Thickness(0, 12, 0, 0));
        var content = Body("Bu güncelleme zorunlu", Subtitle(offer), offer.Notes, band);

        // Progress (hidden until Güncelle): 6 px bar + one status line, the width of the dialog.
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "Divider");
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        bar.Children.Add(fill);
        content.Children.Add(bar);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        status.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        AutomationProperties.SetAutomationId(status, "UpdateMandatoryStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        content.Children.Add(status);

        var (buttons, exit, accept) = Buttons("Çıkış", "UpdateMandatoryExit");
        content.Children.Add(buttons);

        var started = false;
        var exiting = false;
        void Refresh()
        {
            var phase = UpdateController.Phase;
            var downloading = phase == UpdatePhase.Downloading;
            accept.IsEnabled = !UpdateController.IsBusy;
            bar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            if (downloading)
            {
                var percent = UpdateText.Percent(UpdateController.Progress.Received, UpdateController.Progress.Total);
                bar.ColumnDefinitions[0].Width = new GridLength(percent, GridUnitType.Star);
                bar.ColumnDefinitions[1].Width = new GridLength(100 - percent, GridUnitType.Star);
            }
            var activity = UpdateController.Activity;
            status.Visibility = activity is null ? Visibility.Collapsed : Visibility.Visible;
            status.Text = activity ?? "";
            status.SetResourceReference(TextBlock.ForegroundProperty, UpdateController.ActivityFailed ? "DangerText" : "TextSecondary");
        }
        UpdateController.Changed += Refresh;
        dialog.Closed += (_, _) => UpdateController.Changed -= Refresh;

        accept.Click += async (_, _) =>
        {
            if (await UpdateController.InstallAsync())
            {
                started = true;
                dialog.DialogResult = true;
            }
        };
        exit.Click += (_, _) =>
        {
            exiting = true;
            UpdateController.CancelDownload();
            dialog.DialogResult = false;
        };
        dialog.Closing += (_, e) =>
        {
            if (!started && !exiting)
            {
                UpdateController.CancelDownload(); // window X / Alt+F4 = Çıkış
            }
        };
        dialog.Content = content;
        dialog.Loaded += (_, _) =>
        {
            Refresh();
            accept.Focus();
        };
        dialog.ShowDialog();
        return started;
    }

    static string Subtitle(UpdateManifest manifest) =>
        string.Format(CultureInfo.GetCultureInfo("tr-TR"), "Yeni sürüm {0} · yüklü sürüm {1} · {2:dd.MM.yyyy}",
            manifest.Version, UpdateController.CurrentVersion, manifest.Published.ToLocalTime());

    /// <summary>Title, subtitle, optional band, then the notes in a scrolling box (as the other dialogs' lists).</summary>
    static StackPanel Body(string title, string subtitle, string notes, UIElement? band)
    {
        var content = new StackPanel { Width = ContentWidth, Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "CardTitle");
        heading.TextTrimming = TextTrimming.None;
        content.Children.Add(heading);
        var sub = new TextBlock { Text = subtitle };
        sub.SetResourceReference(FrameworkElement.StyleProperty, "RowHelp");
        AutomationProperties.SetAutomationId(sub, "UpdateDialogVersion");
        content.Children.Add(sub);
        if (band is not null)
        {
            content.Children.Add(band);
        }
        var text = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 10), LineHeight = 19 };
        text.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeList");
        AutomationProperties.SetAutomationId(text, "UpdateDialogNotes");
        AutomationProperties.SetName(text, "Neler yeni: " + notes);
        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new ScrollViewer { MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = text },
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        box.SetResourceReference(Border.BackgroundProperty, "CardBg");
        content.Children.Add(box);
        return content;
    }

    static (StackPanel Buttons, Button Secondary, Button Accept) Buttons(string secondaryText, string secondaryId)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var secondary = new Button { Content = secondaryText };
        secondary.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        AutomationProperties.SetAutomationId(secondary, secondaryId);
        AutomationProperties.SetName(secondary, secondaryText);
        var accept = new Button { Content = "Güncelle", Margin = new Thickness(8, 0, 0, 0) };
        accept.SetResourceReference(FrameworkElement.StyleProperty, "SettingsAccentButton");
        AutomationProperties.SetAutomationId(accept, "UpdateDialogAccept");
        AutomationProperties.SetName(accept, "Güncelle");
        buttons.Children.Add(secondary);
        buttons.Children.Add(accept);
        return (buttons, secondary, accept);
    }
}
