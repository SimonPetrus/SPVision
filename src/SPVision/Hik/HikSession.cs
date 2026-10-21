using System.Collections.Concurrent;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// Per-device HCNetSDK login session (NET_DVR_Login_V40 / NET_DVR_Logout).
/// One session — one lUserID — is shared by every tile and view of the same device;
/// RealPlay_V40 and PlayBackByTime_V40 each accept the same user id for concurrent streams.
/// </summary>
public sealed class HikSession : IDisposable
{
    static readonly ConcurrentDictionary<Guid, Lazy<HikSession>> Sessions = new();

    readonly object _lock = new();

    HikSession(Device device)
    {
        Device = device;
        Login();
    }

    public Device Device { get; }

    /// <summary>SDK user id (lUserID); -1 when not logged in.</summary>
    public int UserId { get; private set; } = -1;

    public HikNative.DeviceInfoV40 DeviceInfo { get; private set; }

    public bool IsLoggedIn { get { lock (_lock) return UserId >= 0; } }

    /// <summary>
    /// ISAPI channel number → SDK channel number. On recorders the digital (IP) channels are
    /// numbered from <see cref="HikNative.DeviceInfoV30.StartDChan"/> (e.g. 33 on a 32-ch NVR),
    /// while ISAPI and the GUI use 1-based numbering; direct camera logins have StartDChan = 0.
    /// Passing the wrong number fails with NET_DVR_CHANNEL_ERROR (dwError 4).
    /// </summary>
    public int SdkChannel(int isapiChannel)
    {
        var start = DeviceInfo.DeviceV30.StartDChan;
        return start > 0 ? start + isapiChannel - 1 : isapiChannel;
    }

    public string SerialNumber
    {
        get
        {
            var bytes = DeviceInfo.DeviceV30.SerialNumber;
            return bytes is null ? "" : System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
    }

    /// <summary>
    /// Shared session for the device; performs the (synchronous) SDK login on first use.
    /// Throws <see cref="HikException"/> when login fails; a failed login is not cached,
    /// so the next call retries (e.g. after the network recovers or the password is fixed).
    /// </summary>
    public static HikSession GetOrCreate(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var lazy = Sessions.GetOrAdd(
            device.Id,
            _ => new Lazy<HikSession>(() => new HikSession(device), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Sessions.TryRemove(device.Id, out _);
            throw;
        }
    }

    /// <summary>Existing session for the device, if one is already logged in.</summary>
    public static HikSession? Find(Guid deviceId) =>
        Sessions.TryGetValue(deviceId, out var lazy) && lazy.IsValueCreated ? lazy.Value : null;

    /// <summary>Re-login after e.g. a password change or network recovery.</summary>
    public void EnsureLoggedIn()
    {
        lock (_lock)
        {
            if (UserId >= 0) return;
            Login();
        }
    }

    void Login()
    {
        var login = new HikNative.UserLoginInfo
        {
            DeviceAddress = Device.Host,
            Port = (ushort)Device.SdkPort,
            UserName = Device.UserName,
            Password = Device.Password,
            UseAsyncLogin = 0, // synchronous login; LoginResult callbacks stay null
            Res1 = new byte[3],
            Res3 = new byte[109],
        };
        var deviceInfo = new HikNative.DeviceInfoV40
        {
            DeviceV30 = new HikNative.DeviceInfoV30 { SerialNumber = new byte[HikNative.SerialNoLen] },
            Res2 = new byte[233],
        };

        int userId = HikNative.NET_DVR_Login_V40(ref login, ref deviceInfo);
        lock (_lock)
        {
            if (userId >= 0)
            {
                UserId = userId;
                DeviceInfo = deviceInfo;
            }
        }

        if (userId < 0)
            throw HikException.LastError($"{Device.Name} cihaz girişi başarısız");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            int userId = UserId;
            UserId = -1;
            if (userId >= 0) HikNative.NET_DVR_Logout(userId);
        }
        Sessions.TryRemove(Device.Id, out _);
    }
}
