using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace SPVision.Controls;

/// <summary>
/// ToggleButton whose behaviour lives in <c>Click</c> (tabs, segments, list rows, calendar days, layout items…):
/// IsChecked only shows what the code selected, and the code sets it back itself. A plain ToggleButton toggled through
/// UI Automation (screen reader, <c>TogglePattern.Toggle()</c>) flips IsChecked without raising Click, so it would look
/// selected while nothing happened. Here the Toggle pattern performs a real click instead (OnClick: toggle, Click,
/// Command), exactly what the mouse and the keyboard (Space) do. Looks and behaves like a ToggleButton otherwise; the
/// ToggleButton styles apply as they are.
/// </summary>
public class ClickToggleButton : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ClickTogglePeer(this);

    sealed class ClickTogglePeer(ClickToggleButton owner) : ToggleButtonAutomationPeer(owner), IToggleProvider
    {
        void IToggleProvider.Toggle()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }
            ((ClickToggleButton)Owner).OnClick();
        }
    }
}
