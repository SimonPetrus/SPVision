using System.Globalization;

namespace SPVision.Core;

/// <summary>What the user typed into a device form (device editor or the add-device form). Secrets: an empty box = keep.</summary>
public sealed record DeviceFormInput(
    string Name,
    string Host,
    string HttpPort,
    string RtspPort,
    string SdkPort,
    bool UseHttps,
    string UserName,
    string Password,
    string StreamKey);

/// <summary>
/// Validation and Kaydet of the device forms, shared by the device editor (Settings › Cihaz yönetimi) and the
/// add-device form (Settings › Cihaz ekle, first-run screen). Storage is unchanged: the result is the same
/// <see cref="Device"/> object that <see cref="SettingsStore"/> saves (passwords and keys DPAPI-protected on disk).
/// </summary>
public static class DeviceForm
{
    public const string HostRequired = "IP adresi gerekli.";
    public const string PortsInvalid = "Port numaraları geçersiz (1-65535).";

    public static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    /// <summary>
    /// Form values on a scratch device (for test reads and for saving), or null with <paramref name="error"/>.
    /// An empty password / stream key box keeps the stored value (the stored value is never put into the box).
    /// </summary>
    public static Device? Read(Device saved, DeviceFormInput input, CertificatePin? pin, out string? error)
    {
        if (string.IsNullOrWhiteSpace(input.Host))
        {
            error = HostRequired;
            return null;
        }
        if (!TryPort(input.HttpPort, out var httpPort) || !TryPort(input.RtspPort, out var rtspPort) ||
            !TryPort(input.SdkPort, out var sdkPort))
        {
            error = PortsInvalid;
            return null;
        }
        error = null;
        return new Device
        {
            Id = saved.Id,
            Name = input.Name.Trim(),
            Host = input.Host.Trim(),
            HttpPort = httpPort,
            RtspPort = rtspPort,
            SdkPort = sdkPort,
            UseHttps = input.UseHttps,
            UserName = input.UserName.Trim(),
            Password = input.Password.Length > 0 ? input.Password : saved.Password,
            StreamKey = input.StreamKey.Length > 0 ? input.StreamKey : saved.StreamKey,
            Certificate = pin,
        };
    }

    /// <summary>
    /// Kaydet: copies the form onto the saved (or new) device and returns whether login data changed (then the SDK
    /// session is closed and the tiles reconnect). Saved camera objects are kept (open tiles point at them); name,
    /// state and choices come from the drafts.
    /// </summary>
    public static bool Apply(Device target, Device form, bool isNew, bool pinChanged, CertificatePin? pin,
        DeviceDetails? details, IReadOnlyList<Camera> drafts)
    {
        var loginChanged = isNew ||
            form.Host != target.Host || form.HttpPort != target.HttpPort || form.RtspPort != target.RtspPort ||
            form.SdkPort != target.SdkPort || form.UseHttps != target.UseHttps || form.UserName != target.UserName ||
            form.Password != target.Password || form.StreamKey != target.StreamKey;

        target.Name = string.IsNullOrWhiteSpace(form.Name) ? form.Host : form.Name;
        target.Host = form.Host;
        target.HttpPort = form.HttpPort;
        target.RtspPort = form.RtspPort;
        target.SdkPort = form.SdkPort;
        target.UseHttps = form.UseHttps;
        target.UserName = form.UserName;
        target.Password = form.Password;
        target.StreamKey = form.StreamKey;
        if (isNew || pinChanged)
        {
            target.Certificate = pin; // pinned (or reset) while editing; otherwise the saved pin stays as it is now
        }
        if (details is not null)
        {
            target.Model = details.Model;
            target.SerialNumber = details.SerialNumber;
            target.DeviceType = details.DeviceType ?? target.DeviceType;
        }

        var saved = target.Cameras.ToDictionary(c => c.Channel);
        target.Cameras = drafts.Select(draft =>
        {
            var camera = saved.TryGetValue(draft.Channel, out var old) ? old : new Camera { Channel = draft.Channel };
            camera.Name = draft.Name;
            camera.Online = draft.Online;
            camera.Hidden = draft.Hidden;
            camera.StreamKey = draft.StreamKey;
            return camera;
        }).ToList();
        return loginChanged;
    }

    /// <summary>Draft copy of a camera (name, online, hidden, key) for editing.</summary>
    public static Camera Copy(Camera c) => new()
    {
        Channel = c.Channel,
        Name = c.Name,
        Online = c.Online,
        Hidden = c.Hidden,
        StreamKey = c.StreamKey,
    };
}
