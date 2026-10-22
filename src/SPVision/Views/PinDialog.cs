using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using SPVision.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// PIN dialogs of Ayarlar › Güvenlik, 400 px wide: "PIN belirle" (switching the lock on), the current PIN (switching it
/// off) and "PIN'i değiştir" (current, then the new one twice). Centred title, hint, the six <see cref="PinBox"/> boxes
/// that always have the keyboard, a 16 px error line and two equal buttons: İptal · Devam / Kaydet. A new PIN is checked
/// with Core <see cref="PinRules"/>; the current one with <see cref="AppLock.CheckAsync"/> (wrong PINs count toward the
/// same lockout as the lock screen).
/// </summary>
sealed class PinDialog
{
    enum Step
    {
        Current,
        New,
        Repeat,
    }

    enum Mode
    {
        Set,
        Confirm,
        Change,
    }

    readonly Mode _mode;
    readonly Window _dialog;
    readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, FontSize = 15, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _hint = new() { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    readonly PinBox _pin = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock _error = new() { Height = 16, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    readonly Button _accept = new();
    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly string _currentHint;
    Step _step;
    string? _first;
    bool _busy;

    /// <summary>The new PIN when the dialog ended with Kaydet (Set, Change); kept only until the caller hashes it.</summary>
    string? _result;

    PinDialog(Window? owner, Mode mode, string currentHint)
    {
        _mode = mode;
        _currentHint = currentHint;
        _dialog = DialogChrome.Create(owner, mode == Mode.Set ? "PIN belirle" : mode == Mode.Change ? "PIN'i değiştir" : "Uygulama kilidi");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _error.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeTile");
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        AutomationProperties.SetAutomationId(_error, "PinError");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);

        // 400 px window content: 24 px margin around 352 px; the two buttons share the full width with an 8 px gap.
        var content = new StackPanel { Width = 352, Margin = new Thickness(24) };
        content.Children.Add(_title);
        content.Children.Add(_hint);
        content.Children.Add(_pin);
        content.Children.Add(_error);
        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = new Button { Content = "İptal", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "SettingsButton");
        cancel.Width = double.NaN;
        AutomationProperties.SetAutomationId(cancel, "PinCancel");
        _accept.SetResourceReference(FrameworkElement.StyleProperty, "SettingsAccentButton");
        _accept.Width = double.NaN;
        AutomationProperties.SetAutomationId(_accept, "PinAccept");
        _accept.Click += (_, _) => Next();
        buttons.Children.Add(cancel);
        Grid.SetColumn(_accept, 2);
        buttons.Children.Add(_accept);
        content.Children.Add(buttons);
        _dialog.Content = content;

        _pin.PinChanged += Pin_Changed;
        _pin.Submitted += Next;
        _countdown.Tick += (_, _) => UpdateLockout();
        // The keyboard always types into the boxes: on open, on activation and after any click on the dialog.
        _dialog.Loaded += (_, _) => _pin.FocusInput();
        _dialog.Activated += (_, _) => _dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input, _pin.FocusInput);
        _dialog.PreviewMouseDown += (_, _) => _dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input, _pin.FocusInput);
        _dialog.Closed += (_, _) => _countdown.Stop();

        Show(mode == Mode.Set ? Step.New : Step.Current);
    }

    /// <summary>Switching the lock on: a new PIN typed twice; null when cancelled.</summary>
    public static string? SetNew(Window? owner) => new PinDialog(owner, Mode.Set, "").Run();

    /// <summary>Switching the lock off: true when the current PIN was typed.</summary>
    public static bool Confirm(Window? owner) =>
        new PinDialog(owner, Mode.Confirm, "Kilidi kapatmak için mevcut PIN'i yazın.").Run() is not null;

    /// <summary>"PIN'i değiştir": the current PIN, then a new one twice; null when cancelled.</summary>
    public static string? Change(Window? owner) =>
        new PinDialog(owner, Mode.Change, "PIN'i değiştirmek için önce mevcut PIN'i yazın.").Run();

    string? Run() => _dialog.ShowDialog() == true ? _result : null;

    void Show(Step step, string error = "")
    {
        _step = step;
        (_title.Text, _hint.Text) = step switch
        {
            Step.Current => ("Mevcut PIN'i girin", _currentHint),
            Step.New => (_mode == Mode.Change ? "Yeni PIN belirle" : "PIN belirle", "Klavyeden 6 haneli, yalnızca rakamlardan oluşan bir PIN yazın."),
            _ => ("PIN'i tekrar girin", "Onaylamak için aynı PIN'i bir kez daha girin."),
        };
        var label = step == Step.Repeat ? "Kaydet" : step == Step.Current && _mode == Mode.Confirm ? "Onayla" : "Devam";
        _accept.Content = label;
        AutomationProperties.SetName(_accept, label);
        _pin.Label = step == Step.Current ? "Mevcut PIN" : step == Step.New ? "Yeni PIN" : "Yeni PIN (tekrar)";
        _pin.Clear();
        _pin.IsError = error.Length > 0;
        _error.Text = error;
        UpdateAccept();
        UpdateLockout();
        _pin.FocusInput();
    }

    void Pin_Changed()
    {
        if (_pin.Pin.Length > 0 && _pin.IsError && !(_step == Step.Current && AppLock.LockoutRemaining > TimeSpan.Zero))
        {
            _pin.IsError = false;
            _error.Text = "";
        }
        UpdateAccept();
    }

    void UpdateAccept() => _accept.IsEnabled = PinRules.IsComplete(_pin.Pin) && !_busy;

    async void Next()
    {
        var pin = _pin.Pin;
        if (_busy || !PinRules.IsComplete(pin))
        {
            return;
        }
        switch (_step)
        {
            case Step.Current:
                if (AppLock.LockoutRemaining > TimeSpan.Zero)
                {
                    UpdateLockout();
                    return;
                }
                _busy = true;
                _pin.AcceptsInput = false;
                UpdateAccept();
                PinCheck result;
                try
                {
                    result = await AppLock.CheckAsync(pin);
                }
                finally
                {
                    _busy = false;
                    _pin.AcceptsInput = true;
                }
                if (!_dialog.IsVisible)
                {
                    return; // closed meanwhile
                }
                if (result == PinCheck.Correct)
                {
                    if (_mode == Mode.Confirm)
                    {
                        _result = "";
                        _dialog.DialogResult = true;
                    }
                    else
                    {
                        Show(Step.New);
                    }
                    return;
                }
                _pin.Clear();
                _pin.IsError = true;
                _error.Text = AppLockText.AfterFailure(AppLock.LockoutRemaining);
                UpdateAccept();
                UpdateLockout();
                _pin.FocusInput();
                return;

            case Step.New:
                if (PinRules.CheckNew(pin) is { } problem)
                {
                    Show(Step.New, problem);
                    return;
                }
                _first = pin;
                Show(Step.Repeat);
                return;

            default:
                if (pin != _first)
                {
                    _first = null;
                    Show(Step.New, AppLockText.Mismatch); // both again: the first one may have been the typo
                    return;
                }
                _result = pin;
                _dialog.DialogResult = true;
                return;
        }
    }

    /// <summary>Current-PIN step during a wrong-PIN wait: countdown text, red boxes, input ignored (as on the lock screen).</summary>
    void UpdateLockout()
    {
        var remaining = _step == Step.Current ? AppLock.LockoutRemaining : TimeSpan.Zero;
        if (remaining > TimeSpan.Zero)
        {
            _pin.AcceptsInput = false;
            _pin.IsError = true;
            _error.Text = AppLockText.Wait(remaining);
            if (!_countdown.IsEnabled)
            {
                _countdown.Start();
            }
        }
        else if (_countdown.IsEnabled)
        {
            _countdown.Stop();
            _pin.AcceptsInput = true;
            _pin.IsError = false;
            _error.Text = "";
        }
    }
}
