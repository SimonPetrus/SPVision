using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Lock screen of "Uygulama kilidi" (on startup before any device connection, and after the idle time). The main window
/// collapses the app behind it. The keyboard always types into the PIN boxes; the 6th digit checks the PIN at once (Enter
/// and "Kilidi aç" too). A wrong PIN clears the boxes; from the 3rd one in a row a persistent wait counts down
/// (<see cref="AppLock"/>, Core <see cref="PinLockout"/>) and input is ignored meanwhile. "PIN'i unuttum" asks whether to
/// reset the app (<see cref="ResetConfirmed"/>: the main window forgets the devices and shows the first-run screen).
/// </summary>
public partial class LockView : UserControl
{
    const string Hint = "PIN'inizi klavyeden yazın.";

    readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Window? _window;
    bool _checking;

    public LockView()
    {
        InitializeComponent();
        Pin.PinChanged += Pin_Changed;
        Pin.Submitted += Submit;
        _countdown.Tick += (_, _) => UpdateLockout();
        // A click anywhere (card, background, a button) gives the keyboard back to the PIN input. On mouse-up, not down: a
        // button that loses the keyboard focus while pressed releases the mouse and never raises Click.
        PreviewMouseUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusPin);
        IsVisibleChanged += (_, _) => FocusPin();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.Activated += Window_Activated;
            }
            UpdateLockout();
            FocusPin();
        };
        Unloaded += (_, _) =>
        {
            _countdown.Stop();
            if (_window is not null)
            {
                _window.Activated -= Window_Activated;
                _window = null;
            }
        };
    }

    /// <summary>The right PIN was typed: the main window removes this view and shows the app again.</summary>
    public event Action? Unlocked;

    /// <summary>"PIN'i unuttum" › Sıfırla.</summary>
    public event Action? ResetConfirmed;

    /// <summary>Puts the keyboard in the PIN input (window activated, any click, view shown).</summary>
    public void FocusPin()
    {
        if (IsVisible && EntryPanel.Visibility == Visibility.Visible)
        {
            Pin.FocusInput();
        }
    }

    // Activation restores the focus WPF remembered; ours wins once that is done.
    void Window_Activated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusPin);

    void Pin_Changed()
    {
        if (Pin.Pin.Length > 0 && Pin.IsError && AppLock.LockoutRemaining <= TimeSpan.Zero)
        {
            Pin.IsError = false; // typing again after a wrong PIN
            ErrorText.Text = "";
        }
        if (PinRules.IsComplete(Pin.Pin))
        {
            Submit(); // the 6th digit unlocks without Enter
        }
    }

    void Unlock_Click(object sender, RoutedEventArgs e) => Submit();

    async void Submit()
    {
        if (_checking || EntryPanel.Visibility != Visibility.Visible)
        {
            return;
        }
        if (AppLock.LockoutRemaining > TimeSpan.Zero)
        {
            UpdateLockout();
            return;
        }
        var pin = Pin.Pin;
        if (!PinRules.IsComplete(pin))
        {
            Pin.IsError = true;
            ErrorText.Text = AppLockText.TooShort;
            FocusPin();
            return;
        }
        _checking = true;
        Pin.AcceptsInput = false;
        PinCheck result;
        try
        {
            result = await AppLock.CheckAsync(pin);
        }
        finally
        {
            _checking = false;
            Pin.AcceptsInput = true;
        }
        if (result == PinCheck.Correct)
        {
            Unlocked?.Invoke();
            return;
        }
        Pin.Clear();
        Pin.IsError = true;
        ErrorText.Text = AppLockText.AfterFailure(AppLock.LockoutRemaining);
        UpdateLockout();
        FocusPin();
    }

    /// <summary>
    /// The wrong-PIN wait: "Yanlış PIN. N saniye bekleyin." counting down, red boxes, input ignored; when it is over the
    /// boxes take input again. Also right after startup: the wait survives a restart.
    /// </summary>
    void UpdateLockout()
    {
        var remaining = AppLock.LockoutRemaining;
        if (remaining > TimeSpan.Zero)
        {
            Pin.AcceptsInput = false;
            Pin.IsError = true;
            ErrorText.Text = AppLockText.Wait(remaining);
            if (!_countdown.IsEnabled)
            {
                _countdown.Start();
            }
        }
        else if (_countdown.IsEnabled)
        {
            _countdown.Stop();
            Pin.AcceptsInput = true;
            Pin.IsError = false;
            ErrorText.Text = "";
        }
    }

    void Forgot_Click(object sender, RoutedEventArgs e)
    {
        Pin.Clear();
        EntryPanel.Visibility = Visibility.Collapsed;
        ResetPanel.Visibility = Visibility.Visible;
        HintText.Text = "PIN'i unuttum";
        ResetCancelButton.Focus();
    }

    void ResetCancel_Click(object sender, RoutedEventArgs e)
    {
        if (ResetPanel.Visibility != Visibility.Visible)
        {
            return; // Esc while the PIN boxes show
        }
        ResetPanel.Visibility = Visibility.Collapsed;
        EntryPanel.Visibility = Visibility.Visible;
        HintText.Text = Hint;
        if (AppLock.LockoutRemaining <= TimeSpan.Zero)
        {
            Pin.IsError = false;
            ErrorText.Text = "";
        }
        UpdateLockout();
        FocusPin();
    }

    void Reset_Click(object sender, RoutedEventArgs e) => ResetConfirmed?.Invoke();
}
