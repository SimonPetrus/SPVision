using System.Windows;
using SPVision.Core;

namespace SPVision.Views;

/// <summary>
/// Draft of one device while a form is open (the device editor, or the add-device form of Settings and the first-run
/// screen): cameras, details and the HTTPS certificate pin read so far. Nothing reaches the saved device before
/// <see cref="Save"/> (Kaydet), which goes through <see cref="DeviceForm.Apply"/>.
/// </summary>
sealed class DeviceDraft
{
    public DeviceDraft(Device device, bool isNew)
    {
        Device = device;
        IsNew = isNew;
        Cameras = device.Cameras.Select(DeviceForm.Copy).ToList();
        Pin = device.Certificate;
    }

    /// <summary>The saved device (edited in place on Kaydet), or a fresh one that is added on Kaydet.</summary>
    public Device Device { get; }

    public bool IsNew { get; }

    /// <summary>Draft copies (name, online, hidden, key).</summary>
    public List<Camera> Cameras { get; private set; }

    public DeviceDetails? Details { get; set; }

    /// <summary>
    /// HTTPS certificate pin of the draft: starts as the saved one; a test read may store (first use) or replace it
    /// (trusted in the "sertifika değişti" dialog). Kaydet keeps it when it changed (<see cref="PinChanged"/>).
    /// </summary>
    public CertificatePin? Pin { get; private set; }

    public bool PinChanged { get; private set; }

    /// <summary>Fetched list replaces the draft; per-camera keys and "listede göster" choices are kept.</summary>
    public void TakeCameras(List<Camera> fetched) => Cameras = CameraList.Merge(Cameras, fetched);

    /// <summary>After a read: the draft's pin as the client left it (stored on first use or replaced after the question).</summary>
    public void TakePin(Device form)
    {
        if (!ReferenceEquals(form.Certificate, Pin))
        {
            Pin = form.Certificate;
            PinChanged = true;
        }
    }

    /// <summary>"Sertifikayı sıfırla": forgets the pin (the saved device's too, at once).</summary>
    public void ResetPin()
    {
        Pin = null;
        PinChanged = true;
        if (!IsNew)
        {
            CertificateTrust.Shared.Reset(Device); // saved through CertificateTrustUi
        }
        CertificateTrust.Shared.ForgetDeclined(Device.Id);
    }

    /// <summary>Form values on a scratch device; null with the error when invalid.</summary>
    public Device? Read(DeviceFormInput input, out string? error) => DeviceForm.Read(Device, input, Pin, out error);

    /// <summary>Kaydet: writes the form onto <see cref="Device"/>; true when login data changed.</summary>
    public bool Save(Device form) => DeviceForm.Apply(Device, form, IsNew, PinChanged, Pin, Details, Cameras);
}

/// <summary>The form side of a device read: values, result line, busy state and switching the form to HTTP.</summary>
interface IDeviceFormHost
{
    /// <summary>Form values on a scratch device, or null after showing why they are invalid.</summary>
    Device? ReadForm();

    void ShowResult(string text);

    void SetBusy(bool busy);

    /// <summary>Unticks "HTTPS kullan" and moves a standard port to 80.</summary>
    void SwitchToHttp();

    /// <summary>After the read: keep the pin the client left on the form device.</summary>
    void TakePin(Device form);
}

/// <summary>Device reads of the forms (ISAPI GET only), shared by the device editor and the add-device form.</summary>
static class DeviceReads
{
    /// <summary>
    /// Runs one device read with the form values; true when it completed. When HTTPS was on and the device could not be
    /// reached at all (TCP/TLS, not a 401 or other answer), asks before switching the form to HTTP and retries once;
    /// never silently. A changed certificate is not such a failure (<see cref="CertificateChangedException"/>): it is
    /// asked about in the certificate dialog, even if it was declined before (the user asks again here).
    /// </summary>
    public static async Task<bool> RunAsync(IDeviceFormHost host, Window? owner, string progress, Func<IsapiClient, Task> read)
    {
        if (host.ReadForm() is not { } form)
        {
            return false;
        }
        CertificateTrust.Shared.ForgetDeclined(form.Id);
        host.SetBusy(true);
        host.ShowResult(progress);
        IsapiClient? isapi = null;
        try
        {
            try
            {
                isapi = new IsapiClient(form);
                await read(isapi);
                return true;
            }
            catch (IsapiException ex) when (HttpsPort.OfferHttpFallback(form.UseHttps, isapi?.Answered == true, ex))
            {
                host.ShowResult($"✗ {ex.Message}");
                if (!ConfirmDialog.Show(owner, "Bu cihazda HTTPS kapalı olabilir. HTTP (port 80) ile denensin mi?",
                        "HTTP ile dene", "İptal"))
                {
                    return false;
                }
                host.SwitchToHttp();
                if (host.ReadForm() is not { } http)
                {
                    return false;
                }
                host.ShowResult(progress);
                isapi?.Dispose();
                isapi = new IsapiClient(http);
                await read(isapi);
                return true;
            }
        }
        catch (Exception ex)
        {
            host.ShowResult($"✗ {ex.Message}");
            return false;
        }
        finally
        {
            isapi?.Dispose();
            host.TakePin(form);
            host.SetBusy(false);
        }
    }
}
