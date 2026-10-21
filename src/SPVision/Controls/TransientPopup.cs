using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SPVision.Controls;

/// <summary>
/// Rules for the small popups of the app (layout picker, "Saate git"). A WPF Popup is a top-level
/// window — the only thing that can be drawn over the SDK/Flyleaf video HWNDs — so it must never be
/// left hanging: it closes on an outside click (StaysOpen=false), on Esc, and when the main window is
/// deactivated, moved, resized or minimized. Its toggle button ignores the click that closes it, so
/// that click does not open it again. The toggle's checked state is the popup's open state.
/// </summary>
static class TransientPopup
{
    /// <summary>Wires <paramref name="popup"/> to <paramref name="toggle"/> (checked while open).</summary>
    public static void Attach(Popup popup, ToggleButton toggle)
    {
        popup.StaysOpen = false;
        popup.AllowsTransparency = true;
        popup.PopupAnimation = PopupAnimation.None;
        popup.PlacementTarget = toggle;
        popup.Opened += (_, _) =>
        {
            toggle.IsChecked = true;
            toggle.IsHitTestVisible = false;
        };
        popup.Closed += (_, _) =>
        {
            toggle.IsChecked = false;
            toggle.IsHitTestVisible = true;
        };
        popup.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                toggle.Focus();
                e.Handled = true;
            }
        };
        // Checked/Unchecked rather than Click: UI Automation (TogglePattern.Toggle) changes IsChecked without a click.
        // Opened/Closed above set IsChecked to the popup's own state, so these then change nothing.
        toggle.Checked += (_, _) => popup.IsOpen = true;
        toggle.Unchecked += (_, _) => popup.IsOpen = false;

        Window? window = null;
        void Close(object? sender, EventArgs e) => popup.IsOpen = false;
        toggle.Loaded += (_, _) =>
        {
            if (Window.GetWindow(toggle) is not { } owner || ReferenceEquals(owner, window))
            {
                return;
            }
            window = owner;
            owner.Deactivated += Close;
            owner.LocationChanged += Close;
            owner.SizeChanged += Close;
            owner.StateChanged += Close;
        };
    }

    /// <summary>
    /// Custom placement relative to the target: <paramref name="place"/> gets (popup size, target size, DIP→device
    /// scale) and returns the popup's top-left corner relative to the target's top-left.
    /// </summary>
    public static void Place(Popup popup, Func<Size, Size, double, Point> place)
    {
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // The callback works in device pixels; the offsets are designed in DIPs.
            var scale = popup.PlacementTarget is FrameworkElement target && target.ActualWidth > 0
                ? targetSize.Width / target.ActualWidth
                : 1;
            return [new CustomPopupPlacement(place(popupSize, targetSize, scale), PopupPrimaryAxis.None)];
        };
    }
}
