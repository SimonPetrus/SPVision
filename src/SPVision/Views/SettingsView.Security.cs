using System.Windows;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Uygulama ayarları › Güvenlik card ("Uygulama kilidi", <see cref="AppLock"/>): the switch opens "PIN belirle" (cancelled =
/// stays off) or asks for the current PIN before switching off; the idle segment and "PIN'i değiştir" are dimmed and
/// disabled while the lock is off. The settings are the truth: every action ends by reloading the card.
/// </summary>
public partial class SettingsView
{
    /// <summary>Called from <see cref="LoadAppSettings"/> while <c>_loadingSwitches</c> is set.</summary>
    void LoadSecurity()
    {
        var settings = App.Settings.AppLock;
        AppLockSwitch.IsChecked = settings.Enabled;
        var minutes = IdleLock.Normalize(settings.IdleMinutes);
        IdleOff.IsChecked = minutes == 0;
        Idle5.IsChecked = minutes == 5;
        Idle10.IsChecked = minutes == 10;
        Idle30.IsChecked = minutes == 30;
        LockOptions.IsEnabled = settings.Enabled;
        // The button dims itself when disabled (0.4); the labels and the segment group get the same.
        var opacity = settings.Enabled ? 1 : 0.4;
        IdleLabel.Opacity = ChangePinLabel.Opacity = IdleGroup.Opacity = opacity;
    }

    async void AppLockSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSwitches)
        {
            return;
        }
        var owner = Window.GetWindow(this);
        if (AppLockSwitch.IsChecked == true)
        {
            if (PinDialog.SetNew(owner) is { } pin)
            {
                await AppLock.EnableAsync(pin);
                Message?.Invoke("Uygulama kilidi açıldı");
            }
        }
        else if (PinDialog.Confirm(owner))
        {
            AppLock.Disable();
            Message?.Invoke("Uygulama kilidi kapatıldı");
        }
        LoadAppSettings(); // a cancelled dialog puts the switch back
    }

    void Idle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.AppLock.IdleMinutes = sender == IdleOff ? 0 : sender == Idle5 ? 5 : sender == Idle30 ? 30 : 10;
        App.SaveSettings();
        LoadAppSettings();
    }

    async void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        if (PinDialog.Change(Window.GetWindow(this)) is { } pin)
        {
            await AppLock.ChangePinAsync(pin);
            Message?.Invoke("PIN değiştirildi");
        }
    }
}
