using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// Camera names through HCNetSDK (Settings › Cihaz ayarları › Kameralar): NET_DVR_GET_PICCFG_V40 /
/// NET_DVR_SET_PICCFG_V40 on the device's shared <see cref="HikSession"/>. This is the app's ONLY HCNetSDK
/// configuration write; NET_DVR_SetDVRConfig is declared privately here and called in one place, right after
/// <see cref="SdkWritePolicy.EnsureSetAllowed"/> (command 6180 only, buffer = the one just read with only sChanName
/// changed). All calls block on the network: never call them on the UI thread. No lock is held across an SDK call.
/// </summary>
public sealed class HikChannelConfig : IPicConfigTransport
{
    // NET_DVR_API BOOL __stdcall NET_DVR_SetDVRConfig(LONG lUserID, DWORD dwCommand, LONG lChannel, LPVOID lpInBuffer,
    //     DWORD dwInBufferSize);
    [DllImport("HCNetSDK.dll")]
    static extern bool NET_DVR_SetDVRConfig(int userId, uint command, int channel, IntPtr inBuffer, uint inBufferSize);

    readonly HikSession _session;

    HikChannelConfig(HikSession session)
    {
        _session = session;
        EncodeType = session.DeviceInfo.CharEncodeType;
    }

    public byte EncodeType { get; }

    /// <summary>Logs in if needed (blocking). Throws <see cref="SdkConfigException"/> with a short Turkish reason.</summary>
    public static HikChannelConfig Open(Device device)
    {
        if (!HikSdk.Available)
        {
            throw new SdkConfigException("SDK yüklenemedi");
        }
        try
        {
            var session = HikSession.GetOrCreate(device);
            session.EnsureLoggedIn();
            return new HikChannelConfig(session);
        }
        catch (HikException e)
        {
            throw new SdkConfigException($"SDK girişi başarısız: {HikException.Describe(e.Code)}");
        }
    }

    public int SdkChannel(int channel) => _session.SdkChannel(channel);

    /// <summary>The camera name as the device stores it, or null when it cannot be decoded (unknown encoding).</summary>
    public string? ReadName(int channel) =>
        ChannelNameCodec.Decode(PicCfgV40.NameField(GetPicConfig(SdkChannel(channel))), EncodeType);

    public byte[] GetPicConfig(int sdkChannel)
    {
        var userId = UserId();
        var buffer = Marshal.AllocHGlobal(PicCfgV40.Size);
        try
        {
            var data = new byte[PicCfgV40.Size];
            BitConverter.TryWriteBytes(data.AsSpan(0, 4), PicCfgV40.Size); // dwSize
            Marshal.Copy(data, 0, buffer, data.Length);
            if (!HikNative.NET_DVR_GetDVRConfig(userId, PicCfgV40.GetCommand, sdkChannel, buffer, PicCfgV40.Size, out var returned))
            {
                throw new SdkConfigException($"Kamera adı okunamadı ({HikException.Describe(HikNative.NET_DVR_GetLastError())})");
            }
            Marshal.Copy(buffer, data, 0, data.Length);
            if (returned != PicCfgV40.Size || PicCfgV40.ReadSize(data) != PicCfgV40.Size)
            {
                throw new SdkConfigException("Cihazın resim ayarı beklenen boyutta değil");
            }
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SetPicConfig(int sdkChannel, byte[] current, byte[] body)
    {
        // The one SDK write of the app: refused here before any I/O unless it is NET_DVR_SET_PICCFG_V40 with the buffer
        // just read and only the name bytes changed.
        SdkWritePolicy.EnsureSetAllowed(PicCfgV40.SetCommand, sdkChannel, current, body, EncodeType);
        var userId = UserId();
        var buffer = Marshal.AllocHGlobal(PicCfgV40.Size);
        try
        {
            Marshal.Copy(body, 0, buffer, body.Length);
            if (!NET_DVR_SetDVRConfig(userId, PicCfgV40.SetCommand, sdkChannel, buffer, PicCfgV40.Size))
            {
                throw new SdkConfigException($"Cihaz değeri kabul etmedi: {HikException.Describe(HikNative.NET_DVR_GetLastError())}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    int UserId()
    {
        var userId = _session.UserId;
        if (userId < 0)
        {
            throw new SdkConfigException("SDK oturumu kapalı; sayfayı yeniden açın");
        }
        return userId;
    }
}
