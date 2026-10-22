using System.Windows.Controls;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Welcome card shown instead of an empty live view when no device is saved. Hosts the same
/// <see cref="AddDevicePanel"/> as Settings › Cihaz ekle (first-run mode): "Sonra ekle" leaves the normal, empty app;
/// "Bağlan ve devam et" tests, then hands the new device over (the main window saves it and opens live view).
/// </summary>
public partial class FirstRunView : UserControl
{
    public FirstRunView()
    {
        InitializeComponent();
        var panel = new AddDevicePanel(AddDeviceMode.FirstRun);
        panel.Saved += device => Connected?.Invoke(device);
        panel.Cancelled += () => Later?.Invoke();
        PanelHost.Content = panel;
    }

    /// <summary>Bağlan ve devam et: the tested new device (not yet in the settings).</summary>
    public event Action<Device>? Connected;

    /// <summary>Sonra ekle.</summary>
    public event Action? Later;
}
