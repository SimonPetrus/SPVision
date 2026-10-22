using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SPVision.Core;

namespace SPVision.Controls;

/// <summary>
/// Six-digit PIN entry of the lock screen and the PIN dialogs: six 40×48 boxes with 8 px gaps (280 px), a dot per typed
/// digit, a blinking caret in the next empty box and an accent border on it; red borders for an error. No on-screen
/// keypad: one transparent <see cref="PasswordBox"/> lies over the boxes, keeps the keyboard focus and drives them, so
/// typing, Backspace, paste and screen readers (a password field: digits are never read out) behave as usual. It is not
/// hit-testable — the caret always stays at the end — and the host refocuses it (<see cref="FocusInput"/>). Only digits
/// get in, at most six; Enter raises <see cref="Submitted"/>.
/// </summary>
sealed class PinBox : Grid
{
    const double BoxWidth = 40;
    const double BoxHeight = 48;
    const double Gap = 8;

    readonly PasswordBox _input;
    readonly Border[] _boxes = new Border[PinRules.Length];
    readonly Ellipse[] _dots = new Ellipse[PinRules.Length];
    readonly Rectangle[] _carets = new Rectangle[PinRules.Length];
    readonly DoubleAnimationUsingKeyFrames _blink;
    bool _error;
    bool _acceptsInput = true;

    public PinBox()
    {
        Width = PinRules.Length * BoxWidth + (PinRules.Length - 1) * Gap;
        Height = BoxHeight;
        HorizontalAlignment = HorizontalAlignment.Center;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var row = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
        for (var i = 0; i < PinRules.Length; i++)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Visibility = Visibility.Hidden }.WithBrush(Shape.FillProperty, "TextStrong");
            var caret = new Rectangle { Width = 2, Height = 22, Visibility = Visibility.Hidden }.WithBrush(Shape.FillProperty, "Accent");
            var cell = new Grid();
            cell.Children.Add(dot);
            cell.Children.Add(caret);
            var box = new Border
            {
                Width = BoxWidth,
                Height = BoxHeight,
                Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 2),
                Child = cell,
            }.WithBrush(Border.BackgroundProperty, "CardBg");
            _boxes[i] = box;
            _dots[i] = dot;
            _carets[i] = caret;
            row.Children.Add(box);
        }
        Children.Add(row);

        // The real input: same bounds as the boxes (UI Automation), invisible, never under the mouse.
        _input = new PasswordBox
        {
            MaxLength = PinRules.Length,
            Opacity = 0,
            IsHitTestVisible = false,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            CaretBrush = Brushes.Transparent,
            SelectionBrush = Brushes.Transparent,
        };
        InputMethod.SetIsInputMethodEnabled(_input, false);
        AutomationProperties.SetAutomationId(_input, "PinInput");
        AutomationProperties.SetName(_input, "PIN");
        AutomationProperties.SetHelpText(_input, "6 haneli PIN; yalnızca rakam. Enter onaylar.");
        _input.PasswordChanged += (_, _) =>
        {
            Render();
            RestartBlink();
            PinChanged?.Invoke();
        };
        _input.PreviewTextInput += Input_PreviewTextInput;
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.IsKeyboardFocusedChanged += (_, _) => Render();
        DataObject.AddPastingHandler(_input, Input_Pasting);
        Children.Add(_input);

        // Caret blink: 0.5 s on, 0.5 s off (restarted on every key so it is solid while typing).
        _blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), RepeatBehavior = RepeatBehavior.Forever };
        _blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        _blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5))));
        _blink.Freeze();

        Loaded += (_, _) => RestartBlink();
        Unloaded += (_, _) => StopBlink();
        Render();
    }

    /// <summary>The digits typed so far (0–6).</summary>
    public string Pin => _input.Password;

    /// <summary>The digits changed (typed, deleted, pasted or cleared).</summary>
    public event Action? PinChanged;

    /// <summary>Enter in the input.</summary>
    public event Action? Submitted;

    /// <summary>Red borders (wrong PIN, PINs differ…); cleared by the host when the user types again.</summary>
    public bool IsError
    {
        get => _error;
        set
        {
            _error = value;
            Render();
        }
    }

    /// <summary>False during a wrong-PIN wait or a running check: keys and paste are ignored (focus stays).</summary>
    public bool AcceptsInput
    {
        get => _acceptsInput;
        set
        {
            _acceptsInput = value;
            Render();
        }
    }

    /// <summary>Accessible name of the input ("PIN", "Yeni PIN"…).</summary>
    public string Label
    {
        set => AutomationProperties.SetName(_input, value);
    }

    public void Clear() => _input.Clear();

    /// <summary>Puts the keyboard focus in the input (window activated, a click anywhere on the card, a step changed).</summary>
    public void FocusInput()
    {
        if (IsVisible && !_input.IsKeyboardFocused)
        {
            _input.Focus();
            Keyboard.Focus(_input);
        }
    }

    /// <summary>
    /// Takes digits, Backspace and Enter typed anywhere in <paramref name="window"/> while the boxes are shown, whatever has
    /// the keyboard focus (a button clicked last, the window itself…): the PIN never depends on where the mouse went.
    /// Enter on a focused button stays that button's.
    /// </summary>
    public void ListenTo(Window window)
    {
        window.PreviewTextInput += Window_PreviewTextInput;
        window.PreviewKeyDown += Window_PreviewKeyDown;
    }

    public void StopListening(Window window)
    {
        window.PreviewTextInput -= Window_PreviewTextInput;
        window.PreviewKeyDown -= Window_PreviewKeyDown;
    }

    void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsVisible || _input.IsKeyboardFocusWithin || e.Text.Length == 0 || !e.Text.All(char.IsAsciiDigit))
        {
            return; // the input handles its own keys
        }
        e.Handled = true;
        var digits = _acceptsInput ? PinRules.Digits(e.Text, PinRules.Length - _input.Password.Length) : "";
        if (digits.Length > 0)
        {
            _input.Password += digits;
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || _input.IsKeyboardFocusWithin)
        {
            return;
        }
        if (e.Key == Key.Back)
        {
            e.Handled = true;
            if (_acceptsInput && _input.Password.Length > 0)
            {
                _input.Password = _input.Password[..^1];
            }
        }
        else if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = true;
            Submitted?.Invoke();
        }
    }

    void Input_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Only digits; typed full-width digits are not converted here (the IME is off), pasted ones are.
        if (!_acceptsInput || e.Text.Length == 0 || !e.Text.All(char.IsAsciiDigit))
        {
            e.Handled = true;
        }
    }

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Submitted?.Invoke();
                return;
            case Key.Back or Key.Delete when !_acceptsInput:
            // The caret stays at the end: no moving, selecting or overwrite mode (Space is no text input in WPF boxes).
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Insert or Key.Space:
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                e.Handled = true;
                return;
        }
    }

    void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? e.DataObject.GetData(DataFormats.UnicodeText) as string : null;
        var digits = _acceptsInput ? PinRules.Digits(text, PinRules.Length - _input.Password.Length) : "";
        if (digits.Length == 0)
        {
            e.CancelCommand();
            return;
        }
        var data = new DataObject();
        data.SetText(digits);
        e.DataObject = data;
    }

    void Render()
    {
        var length = _input.Password.Length;
        var active = Math.Min(length, PinRules.Length - 1);
        var focused = _acceptsInput; // keys reach the boxes wherever the focus is (ListenTo), so the caret always shows
        for (var i = 0; i < PinRules.Length; i++)
        {
            _dots[i].Visibility = i < length ? Visibility.Visible : Visibility.Hidden;
            _carets[i].Visibility = focused && i == length ? Visibility.Visible : Visibility.Hidden;
            _boxes[i].SetResourceReference(Border.BorderBrushProperty,
                _error ? "DangerText" : i == active && _acceptsInput ? "Accent" : "InputBorder");
        }
    }

    void RestartBlink()
    {
        foreach (var caret in _carets)
        {
            caret.BeginAnimation(OpacityProperty, _blink); // replaces the running one: starts "on"
        }
    }

    void StopBlink()
    {
        foreach (var caret in _carets)
        {
            caret.BeginAnimation(OpacityProperty, null);
        }
    }
}
