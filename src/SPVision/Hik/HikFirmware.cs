using System.Runtime.InteropServices;
using SPVision.Core;

namespace SPVision.Hik;

/// <summary>
/// HCNetSDK firmware upgrade calls (Settings › Cihaz ayarları › Bakım, phase 3). Both entry points take a
/// <see cref="FirmwareJob"/>, which exists only after the typed confirmation (<see cref="ConfirmedRiskyWrite.TakeFirmwareJobAsync"/>).
/// Recorder: NET_DVR_Upgrade_V50 + ENUM_UPGRADE_DVR (SDK General Developer Guide §4.2 / §12.7.5–8); cameras:
/// NET_DVR_StartRemoteConfig + NET_DVR_UPGRADE_IPC (official ClientDemo DlgUpgradeIPC.cpp). The SDK reads the file
/// from disk itself (nothing is loaded into memory here). Layouts follow incEn\HCNetSDK.h of V6.1.11.40, x64.
/// Kept apart from <see cref="HikNative"/> on purpose: nothing else may reach these functions.
/// </summary>
static class HikFirmwareNative
{
    const string NetSdk = "HCNetSDK.dll";

    public const uint UpgradeTypeDvr = 0;           // ENUM_UPGRADE_DVR
    public const uint UpgradeIpcCommand = 6174;     // NET_DVR_UPGRADE_IPC

    /// <summary>NET_DVR_UPGRADE_PARAM is 656 bytes on x64; the buffer is larger and zeroed, so any layout slack reads zeros.</summary>
    public const int UpgradeParamBuffer = 1024;

    public const int StreamInfoSize = 72;           // NET_DVR_STREAM_INFO: dwSize, byID[32], dwChannel, byRes[32]
    public const int StreamInfoChannelOffset = 36;
    public const int MaxChannels = 64;              // MAX_CHANNUM_V30
    public const int FileNameLength = 260;          // NET_DVR_UPGRADE_IPC_PARAM.szFileName
    public const int UpgradeIpcParamSize = StreamInfoSize * MaxChannels + FileNameLength; // 4868

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RemoteConfigCallback(uint type, IntPtr buffer, uint length, IntPtr user);

    [DllImport(NetSdk)] public static extern int NET_DVR_Upgrade_V50(int userId, IntPtr upgradeParam);
    [DllImport(NetSdk)] public static extern int NET_DVR_GetUpgradeState(int upgradeHandle);
    [DllImport(NetSdk)] public static extern int NET_DVR_GetUpgradeProgress(int upgradeHandle);
    [DllImport(NetSdk)] public static extern int NET_DVR_GetUpgradeStep(int upgradeHandle, out int subProgress);
    [DllImport(NetSdk)] public static extern bool NET_DVR_CloseUpgradeHandle(int upgradeHandle);
    [DllImport(NetSdk)] public static extern int NET_DVR_StartRemoteConfig(int userId, uint command, IntPtr inBuffer, uint inLength, RemoteConfigCallback callback, IntPtr user);
    [DllImport(NetSdk)] public static extern bool NET_DVR_StopRemoteConfig(int handle);

    /// <summary>
    /// The path as the SDK's char* (system ANSI code page), or null when it does not survive that encoding (a name
    /// with characters outside the code page would make the SDK open another or no file) or is too long.
    /// </summary>
    public static IntPtr? AnsiPath(string path, int maxBytes)
    {
        var pointer = Marshal.StringToHGlobalAnsi(path);
        if (Marshal.PtrToStringAnsi(pointer) is { } back && string.Equals(back, path, StringComparison.Ordinal) &&
            back.Length > 0 && AnsiLength(pointer) < maxBytes)
        {
            return pointer;
        }
        Marshal.FreeHGlobal(pointer);
        return null;
    }

    static int AnsiLength(IntPtr pointer)
    {
        var length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }
        return length;
    }

    public const string PathProblem =
        "Dosya yolu SDK'nın okuyamayacağı karakterler içeriyor ya da çok uzun. Dosyayı C:\\Yazilim gibi kısa, Türkçe karakter içermeyen bir klasöre taşıyıp yeniden seçin.";
}

/// <summary>Recorder upgrade over the device's shared SDK session (<see cref="HikSession"/>).</summary>
sealed class HikRecorderFirmware(Device device) : IRecorderFirmwareTransport
{
    readonly Dictionary<int, (IntPtr Param, IntPtr Path)> _buffers = [];

    public int Start(FirmwareJob job)
    {
        if (job.Operation != RiskyOperation.RecorderFirmware)
        {
            throw new IsapiWriteDeniedException("Bu iş bir kayıt cihazı güncellemesi değil.");
        }
        EnsureSdk();
        var session = HikSession.GetOrCreate(device);
        session.EnsureLoggedIn();
        var path = HikFirmwareNative.AnsiPath(job.File.Path, 256) ?? throw new FirmwareFileException(HikFirmwareNative.PathProblem);
        var param = Marshal.AllocHGlobal(HikFirmwareNative.UpgradeParamBuffer);
        Zero(param, HikFirmwareNative.UpgradeParamBuffer);
        Marshal.WriteInt32(param, 0, (int)HikFirmwareNative.UpgradeTypeDvr); // dwUpgradeType
        Marshal.WriteIntPtr(param, 8, path);                                  // sFileName (pInbuffer, dwBufferLen, the rest: 0)
        var handle = HikFirmwareNative.NET_DVR_Upgrade_V50(session.UserId, param);
        if (handle < 0)
        {
            var error = HikException.LastError("Yükseltme başlatılamadı");
            Marshal.FreeHGlobal(param);
            Marshal.FreeHGlobal(path);
            throw error;
        }
        _buffers[handle] = (param, path); // the SDK may still read the path while it uploads
        return handle;
    }

    public SdkUpgradeSnapshot Poll(int handle)
    {
        var state = HikFirmwareNative.NET_DVR_GetUpgradeState(handle);
        var progress = HikFirmwareNative.NET_DVR_GetUpgradeProgress(handle);
        var step = HikFirmwareNative.NET_DVR_GetUpgradeStep(handle, out var sub);
        return new SdkUpgradeSnapshot(state, progress, step, sub);
    }

    public void Close(int handle)
    {
        HikFirmwareNative.NET_DVR_CloseUpgradeHandle(handle);
        if (_buffers.Remove(handle, out var buffers))
        {
            Marshal.FreeHGlobal(buffers.Param);
            Marshal.FreeHGlobal(buffers.Path);
        }
    }

    internal static void EnsureSdk()
    {
        if (!HikSdk.Available)
        {
            throw new HikException(3, "Hikvision SDK yüklenemedi; yazılım güncellemesi SDK gerektirir");
        }
        if (IntPtr.Size != 8)
        {
            throw new HikException(6, "Yazılım güncellemesi yalnızca 64 bit SPVision ile yapılabilir");
        }
    }

    internal static void Zero(IntPtr buffer, int length)
    {
        for (var i = 0; i < length; i++)
        {
            Marshal.WriteByte(buffer, i, 0);
        }
    }
}

/// <summary>
/// One camera at a time through the recorder (NET_DVR_UPGRADE_IPC); the return check reads the recorder's channel
/// list and status with ISAPI GETs.
/// </summary>
sealed class HikCameraFirmware(Device device) : ICameraFirmwareTransport
{
    /// <summary>No final status from the SDK for this long → give up on this camera.</summary>
    static readonly TimeSpan UpgradeLimit = TimeSpan.FromMinutes(20);

    public async Task<CameraUpgradeResult> UpgradeAsync(FirmwareJob job, int channel, IProgress<int>? percent, CancellationToken ct)
    {
        if (job.Operation != RiskyOperation.CameraFirmware || !job.Channels.Contains(channel))
        {
            throw new IsapiWriteDeniedException("Bu kamera onaylanan işte yok.");
        }
        HikRecorderFirmware.EnsureSdk();
        var final = new TaskCompletionSource<(int Status, int Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        HikFirmwareNative.RemoteConfigCallback callback = (type, buffer, length, _) => OnCallback(type, buffer, length, final, percent);
        var param = IntPtr.Zero;
        var handle = -1;
        try
        {
            handle = await Task.Run(() =>
            {
                var session = HikSession.GetOrCreate(device);
                session.EnsureLoggedIn();
                param = BuildParam(session.SdkChannel(channel), job.File.Path);
                var started = HikFirmwareNative.NET_DVR_StartRemoteConfig(session.UserId, HikFirmwareNative.UpgradeIpcCommand, param,
                    HikFirmwareNative.UpgradeIpcParamSize, callback, IntPtr.Zero);
                return started >= 0 ? started : throw HikException.LastError("Kamera yükseltmesi başlatılamadı");
            }, CancellationToken.None);

            var finished = await Task.WhenAny(final.Task, Task.Delay(UpgradeLimit, ct));
            if (finished != final.Task)
            {
                ct.ThrowIfCancellationRequested();
                return new CameraUpgradeResult(false, "Kayıt cihazından uzun süredir sonuç gelmiyor; kameranın durumu bilinmiyor.");
            }
            var (status, error) = final.Task.Result;
            var text = FirmwareText.CameraStatus(status);
            if (status != 1000 && error > 1)
            {
                text += " (" + FirmwareText.CameraError(error) + ")";
            }
            return new CameraUpgradeResult(status == 1000, text);
        }
        catch (HikException e)
        {
            return new CameraUpgradeResult(false, e.Message);
        }
        finally
        {
            if (handle >= 0)
            {
                var stopHandle = handle;
                await Task.Run(() => HikFirmwareNative.NET_DVR_StopRemoteConfig(stopHandle), CancellationToken.None);
            }
            if (param != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(param);
            }
            GC.KeepAlive(callback); // the SDK calls it until StopRemoteConfig
        }
    }

    /// <summary>
    /// The demo treats the callback's type itself as the status (1000…); the generic remote-config contract puts a
    /// status DWORD in the buffer of type 0 and a percentage in the buffer of type 1. Both are accepted.
    /// </summary>
    static void OnCallback(uint type, IntPtr buffer, uint length, TaskCompletionSource<(int, int)> final, IProgress<int>? percent)
    {
        try
        {
            int status;
            if (type is >= 1000 and <= 1100)
            {
                status = (int)type;
            }
            else if (type == 1)
            {
                if (buffer != IntPtr.Zero && length >= 4)
                {
                    percent?.Report(Marshal.ReadInt32(buffer));
                }
                return;
            }
            else if (type == 0 && buffer != IntPtr.Zero && length >= 4)
            {
                status = Marshal.ReadInt32(buffer);
            }
            else
            {
                return;
            }
            if (status == 1001)
            {
                return; // processing
            }
            var error = 0;
            if (buffer != IntPtr.Zero && length >= HikFirmwareNative.StreamInfoSize + 2)
            {
                error = (ushort)Marshal.ReadInt16(buffer, HikFirmwareNative.StreamInfoSize); // NET_DVR_UPGRADE_IPC_ERR_INFO_SINGLE[0].wErrCode
            }
            final.TrySetResult((status, error));
        }
        catch (Exception)
        {
            // Never throw into the SDK thread.
        }
    }

    static IntPtr BuildParam(int sdkChannel, string path)
    {
        var ansi = HikFirmwareNative.AnsiPath(path, HikFirmwareNative.FileNameLength) ?? throw new FirmwareFileException(HikFirmwareNative.PathProblem);
        try
        {
            var param = Marshal.AllocHGlobal(HikFirmwareNative.UpgradeIpcParamSize);
            HikRecorderFirmware.Zero(param, HikFirmwareNative.UpgradeIpcParamSize);
            Marshal.WriteInt32(param, 0, HikFirmwareNative.StreamInfoSize);                           // struStreamInfo[0].dwSize
            Marshal.WriteInt32(param, HikFirmwareNative.StreamInfoChannelOffset, sdkChannel);          // struStreamInfo[0].dwChannel
            var nameOffset = HikFirmwareNative.StreamInfoSize * HikFirmwareNative.MaxChannels;
            for (var i = 0; ; i++)
            {
                var b = Marshal.ReadByte(ansi, i);
                Marshal.WriteByte(param, nameOffset + i, b);                                           // szFileName
                if (b == 0)
                {
                    break;
                }
            }
            return param;
        }
        finally
        {
            Marshal.FreeHGlobal(ansi);
        }
    }

    public async Task<string?> ReadVersionAsync(int channel, CancellationToken ct)
    {
        try
        {
            using var isapi = new IsapiClient(device, TimeSpan.FromSeconds(8));
            var channels = await isapi.GetConfigAsync(MaintenancePaths.Channels, ct);
            var status = await isapi.GetConfigAsync(MaintenancePaths.ChannelStatus, ct);
            var camera = CameraFirmware.ParseList(channels, status).FirstOrDefault(c => c.Channel == channel);
            return camera is { Online: true, Firmware: { } firmware } ? firmware.Key : null;
        }
        catch (Exception e) when (e is IsapiException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
