using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SPVision.Core;

namespace SPVision.Views;

// Settings › Cihaz ayarları, phase 2: the risky operations (reboot, disk format; the network address and users use the
// same RunRiskyAsync). Each one: RiskyWrites builds the request → RiskyConfirmDialog (the separate confirmation) gives
// the ConfirmedRiskyWrite → IsapiClient.SendRiskyAsync sends exactly that, once. Nothing blocks the UI thread.
public partial class RecorderSettingsView
{
    enum RiskyOutcome
    {
        Cancelled,
        Done,
        Refused,

        /// <summary>No answer (timeout, connection reset): the device may have done it (reboot, new address).</summary>
        NoAnswer,
    }

    /// <summary>A recorder setting changed SPVision's saved device (new address or password): save it and reconnect.</summary>
    public event Action<Device, bool>? DeviceUpdated;

    static Border CardWarning(string text)
    {
        var band = DialogChrome.WarningBand(text, new Thickness(0), radius: 0);
        band.Padding = new Thickness(16, 10, 16, 10);
        band.BorderThickness = new Thickness(0, 0, 0, 1);
        band.SetResourceReference(Border.BorderBrushProperty, "Border");
        return band;
    }

    /// <summary>Confirmation → token → send. The tab is disabled meanwhile; the caller writes the result.</summary>
    async Task<(RiskyOutcome Outcome, string Text)> RunRiskyAsync(Tab tab, Device device, RiskyWriteRequest request)
    {
        var token = ConfirmedRiskyWrite.Confirm(request, new RiskyConfirmDialog(Window.GetWindow(this)));
        if (token is null)
        {
            return (RiskyOutcome.Cancelled, "");
        }
        _writing = true;
        _results[tab] = (null, "Uygulanıyor…");
        Render();
        UpdateEnabled();
        try
        {
            using var isapi = new IsapiClient(device, WriteTimeout);
            var status = await isapi.SendRiskyAsync(token);
            return status.IsOk ? (RiskyOutcome.Done, "")
                : status.StatusCode == 7 ? (RiskyOutcome.Done, "Cihaz yeniden başlatılınca geçerli olur.")
                : (RiskyOutcome.Refused, $"Cihaz kabul etmedi: {status.Describe()}");
        }
        catch (IsapiWriteDeniedException ex)
        {
            return (RiskyOutcome.Refused, ex.Message);
        }
        catch (IsapiException ex) when (ex.ConnectionFailed)
        {
            return (RiskyOutcome.NoAnswer, ex.Message);
        }
        catch (IsapiException ex)
        {
            return (RiskyOutcome.Refused, ex.Message);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Xml.XmlException)
        {
            return (RiskyOutcome.NoAnswer, ex.Message);
        }
        finally
        {
            _writing = false;
            UpdateEnabled();
        }
    }

    // ================================================================ Sistem: yeniden başlat, biçimlendir

    async void Reboot()
    {
        if (_device is not { } device || _writing)
        {
            return;
        }
        var (outcome, text) = await RunRiskyAsync(Tab.System, device, RiskyWrites.Reboot(device.Name));
        if (outcome == RiskyOutcome.Cancelled)
        {
            return;
        }
        _results[Tab.System] = outcome switch
        {
            RiskyOutcome.Done => (true, "Yeniden başlatma komutu cihaza ulaştı. Cihaz birkaç dakika kapalı kalır; SPVision açılınca kendiliğinden yeniden bağlanır."),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; yeniden başlıyor olabilir. Birkaç dakika sonra bu sayfayı yeniden açın. ({text})"),
            _ => (false, $"Yeniden başlatılamadı: {text}"),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? $"Yeniden başlatılıyor: {device.Name}" : $"Yeniden başlatma: {text}");
        if (device == _device)
        {
            Render();
        }
    }

    async void FormatDisk(HddInfo disk)
    {
        if (_device is not { } device || _writing)
        {
            return;
        }
        var name = disk.Name.Length > 0 ? disk.Name : string.Create(CultureInfo.InvariantCulture, $"Disk {disk.Id}");
        var (outcome, text) = await RunRiskyAsync(Tab.System, device, RiskyWrites.FormatDisk(disk));
        if (outcome == RiskyOutcome.Cancelled)
        {
            return;
        }
        _results[Tab.System] = outcome switch
        {
            RiskyOutcome.Done => (true, $"Biçimlendirme başladı: {name}. Sürerken disk durumu “Biçimlendiriliyor”, bitince “Normal” görünür."),
            RiskyOutcome.NoAnswer => (null, $"Cihaz yanıt vermedi; biçimlendirme başlamış olabilir. Disk durumunu birazdan yeniden okuyun. ({text})"),
            _ => (false, $"{name} biçimlendirilemedi: {text}"),
        };
        Message?.Invoke(outcome == RiskyOutcome.Done ? $"Biçimlendiriliyor: {name}" : $"Biçimlendirme: {text}");
        if (device == _device)
        {
            _system = null; // read the disk states again
            _overview = null;
            Render();
        }
    }
}
