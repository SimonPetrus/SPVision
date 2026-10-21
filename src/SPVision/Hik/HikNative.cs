using System.Runtime.InteropServices;

namespace SPVision.Hik;

/// <summary>
/// The subset of Hikvision Device Network SDK (HCNetSDK.dll) and PlayCtrl.dll used by the app.
/// Layouts follow incEn\HCNetSDK.h / plaympeg4.h of SDK V6.1.11.40 (x64, default packing).
/// </summary>
public static class HikNative
{
    const string NetSdk = "HCNetSDK.dll";
    const string PlayCtrl = "PlayCtrl.dll";

    public const int SerialNoLen = 48;
    public const int StreamIdLen = 32;

    // Real-time / playback data types
    public const uint SysHead = 1;
    public const uint StreamData = 2;

    // Playback control codes (HCNetSDK.h NET_DVR_PLAY* defines)
    public const uint PlayStart = 1;
    public const uint PlayStopControl = 2;
    public const uint PlayPause = 3;
    public const uint PlayRestart = 4;
    public const uint PlayFast = 5;
    public const uint PlaySlow = 6;
    public const uint PlayNormal = 7;
    public const uint PlayGetTime = 14;   // out: NET_DVR_TIME of the frame on screen
    public const uint PlaySetTime = 26;   // in: NET_DVR_TIME absolute seek

    // Exception callback types
    public const uint ExceptionPreview = 0x8003;
    public const uint ExceptionReconnect = 0x8005;
    public const uint ExceptionPlayback = 0x8010;

    // Error codes
    public const uint ErrorPassword = 1;
    public const uint ErrorConnect = 7;
    public const uint ErrorUserLocked = 153;

    // PlayCtrl
    public const uint StreamRealtime = 0;
    public const uint StreamFile = 1;
    public const uint SoftDecodeEngine = 0;
    public const uint HardDecodeEngine = 1;
    public const int AesKeyType = 1;
    public const int AesKeyBits = 128;

    // Private-data rendering (plaympeg4.h enum _PLAYM4_PRIDATA_RENDER; HCNetSDK.h _PRIDATA_RENDER has the same values).
    // The green boxes the recorder sends with the stream are intelligent-analysis rules/targets and
    // motion-detection info; PlayCtrl draws them on the video unless switched off per port.
    public const int RenderAnaIntelData = 0x00000001; // PLAYM4_RENDER_ANA_INTEL_DATA
    public const int RenderMotionDetection = 0x00000002; // PLAYM4_RENDER_MD

    /// <summary>The private-data types toggled by the "Hareket kutularını göster" setting.</summary>
    public static readonly int[] MotionBoxRenderTypes = [RenderAnaIntelData, RenderMotionDetection];

    // Recordings are NOT made with NET_DVR_SaveRealData_V30 / NET_DVR_PlayBackSaveData: they store the stream as the
    // recorder sends it (packing type 1 = PS, the "IMKH" Hikvision file; per the developer guide 1-PS, 2-TS, 3-RTP,
    // 5-MP4/3GPP) and the payload stays stream-encrypted — no standard player can show it. See HikFrameRecorder.

    // PlayCtrl decode callback (plaympeg4.h): picture type of FRAME_INFO.nType, stream selector of PlayM4_SetDecCBStream.
    public const int FrameTypeYv12 = 3;    // T_YV12
    public const uint DecodeVideoOnly = 1; // PlayM4_SetDecCBStream: 1 = video, 2 = audio, 3 = both

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DataCallback(int handle, uint dataType, IntPtr buffer, uint size, IntPtr user);

    /// <summary>
    /// plaympeg4.h FRAME_INFO (all <c>long</c> = 32-bit on Windows): picture size, stream timestamp in ms, type
    /// (<see cref="FrameTypeYv12"/> for video), frame rate and frame number.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FrameInfo
    {
        public int Width;
        public int Height;
        public int Stamp;
        public int Type;
        public int FrameRate;
        public uint FrameNum;
    }

    /// <summary>plaympeg4.h PLAYM4_SYSTEM_TIME: absolute (recorder wall-clock) time of the frame on screen.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Pm4SystemTime
    {
        public uint Year;
        public uint Month;
        public uint Day;
        public uint Hour;
        public uint Minute;
        public uint Second;
        public uint Millisecond;

        public DateTime? ToDateTime() =>
            Year is >= 2000 and < 2100 && Month is >= 1 and <= 12 && Day is >= 1 and <= 31 && Hour < 24 && Minute < 60 && Second < 60
                ? new DateTime((int)Year, (int)Month, Math.Min((int)Day, DateTime.DaysInMonth((int)Year, (int)Month)),
                    (int)Hour, (int)Minute, (int)Second, (int)Math.Min(Millisecond, 999u))
                : null;
    }

    // void (CALLBACK* DecCBFun)(long nPort, char* pBuf, long nSize, FRAME_INFO* pFrameInfo, long nUser, long nReserved2)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DecodeCallback(int port, IntPtr buffer, int size, ref FrameInfo info, int user, int reserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void ExceptionCallback(uint type, int userId, int handle, IntPtr user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct UserLoginInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string DeviceAddress;
        public byte UseTransport;
        public ushort Port;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Password;
        public IntPtr LoginResultCallback;
        public IntPtr User;
        public int UseAsyncLogin;
        public byte ProxyType;
        public byte UseUtcTime;
        public byte LoginMode;
        public byte Https;
        public int ProxyId;
        public byte VerifyMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] Res1;
        public IntPtr LoginResultCallbackV40;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 109)] public byte[] Res3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoV30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SerialNoLen)] public byte[] SerialNumber;
        public byte AlarmInPortNum;
        public byte AlarmOutPortNum;
        public byte DiskNum;
        public byte DvrType;
        public byte ChanNum;
        public byte StartChan;
        public byte AudioChanNum;
        public byte IpChanNum;
        public byte ZeroChanNum;
        public byte MainProto;
        public byte SubProto;
        public byte Support;
        public byte Support1;
        public byte Support2;
        public ushort DevType;
        public byte Support3;
        public byte MultiStreamProto;
        public byte StartDChan;
        public byte StartDTalkChan;
        public byte HighDChanNum;
        public byte Support4;
        public byte LanguageType;
        public byte VoiceInChanNum;
        public byte StartVoiceInChanNo;
        public byte Support5;
        public byte Support6;
        public byte MirrorChanNum;
        public ushort StartMirrorChanNo;
        public byte Support7;
        public byte Res2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfoV40
    {
        public DeviceInfoV30 DeviceV30;
        public byte SupportLock;
        public byte RetryLoginTime;
        public byte PasswordLevel;
        public byte ProxyType;
        public uint SurplusLockTime;
        public byte CharEncodeType;
        public byte SupportDev5;
        public byte Support;
        public byte LoginMode;
        public uint OemCode;
        public int ResidualValidity;
        public byte ResidualValidityFlag;
        public byte SingleStartDTalkChan;
        public byte SingleDTalkChanNums;
        public byte PasswordResetLevel;
        public byte SupportStreamEncrypt;
        public byte MarketType;
        public byte TlsCap;
        public byte ChildManage;
        public byte PlaybackNewPosCap;
        public byte SecondaryAuth;
        public byte HttpsResult;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 233)] public byte[] Res2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PreviewInfo
    {
        public int Channel;
        public uint StreamType;
        public uint LinkMode;
        public IntPtr PlayWnd;
        public uint Blocked;
        public uint PassbackRecord;
        public byte PreviewMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = StreamIdLen)] public byte[] StreamId;
        public byte ProtoType;
        public byte Res1;
        public byte VideoCodingType;
        public uint DisplayBufNum;
        public byte NpqMode;
        public byte RecvMetaData;
        public byte DataType;
        public byte Reconnect;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 212)] public byte[] Res;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DvrTime
    {
        public uint Year;
        public uint Month;
        public uint Day;
        public uint Hour;
        public uint Minute;
        public uint Second;

        public static DvrTime From(DateTime t) => new()
        {
            Year = (uint)t.Year, Month = (uint)t.Month, Day = (uint)t.Day,
            Hour = (uint)t.Hour, Minute = (uint)t.Minute, Second = (uint)t.Second,
        };

        public DateTime? ToDateTime() =>
            Year is >= 2000 and < 2100 && Month is >= 1 and <= 12 && Day is >= 1 and <= 31
                ? new DateTime((int)Year, (int)Month, (int)Day, (int)Hour % 24, (int)Minute % 60, (int)Second % 60)
                : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = StreamIdLen)] public byte[] Id;
        public uint Channel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Res;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VodPara
    {
        public uint Size;
        public StreamInfo IdInfo;
        public DvrTime BeginTime;
        public DvrTime EndTime;
        public IntPtr Wnd;
        public byte DrawFrame;
        public byte VolumeType;
        public byte VolumeNum;
        public byte StreamType;
        public uint FileIndex;
        public byte AudioFile;
        public byte CourseFile;
        public byte Download;
        public byte OptimalStreamType;
        public byte UseAsync;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 19)] public byte[] Res2;
    }

    // ---- HCNetSDK ----

    [DllImport(NetSdk)] public static extern bool NET_DVR_Init();
    [DllImport(NetSdk)] public static extern bool NET_DVR_Cleanup();
    [DllImport(NetSdk)] public static extern uint NET_DVR_GetLastError();
    [DllImport(NetSdk)] public static extern bool NET_DVR_SetConnectTime(uint waitTime, uint tryTimes);
    [DllImport(NetSdk)] public static extern bool NET_DVR_SetReconnect(uint interval, bool enable);
    [DllImport(NetSdk)] public static extern bool NET_DVR_SetLogToFile(uint level, string dir, bool autoDel);
    [DllImport(NetSdk)] public static extern bool NET_DVR_SetExceptionCallBack_V30(uint reserved1, IntPtr reserved2, ExceptionCallback callback, IntPtr user);
    [DllImport(NetSdk)] public static extern int NET_DVR_Login_V40(ref UserLoginInfo loginInfo, ref DeviceInfoV40 deviceInfo);
    [DllImport(NetSdk)] public static extern bool NET_DVR_Logout(int userId);
    [DllImport(NetSdk)] public static extern int NET_DVR_RealPlay_V40(int userId, ref PreviewInfo previewInfo, DataCallback? callback, IntPtr user);
    [DllImport(NetSdk)] public static extern bool NET_DVR_StopRealPlay(int realHandle);
    [DllImport(NetSdk)] public static extern int NET_DVR_PlayBackByTime_V40(int userId, ref VodPara vodPara);
    [DllImport(NetSdk)] public static extern bool NET_DVR_SetPlayDataCallBack_V40(int playHandle, DataCallback callback, IntPtr user);
    [DllImport(NetSdk)] public static extern bool NET_DVR_PlayBackControl_V40(int playHandle, uint controlCode, IntPtr inBuffer, uint inLen, IntPtr outBuffer, ref uint outLen);
    [DllImport(NetSdk)] public static extern bool NET_DVR_StopPlayBack(int playHandle);
    [DllImport(NetSdk, CharSet = CharSet.Ansi)] public static extern bool NET_DVR_PlayBackSaveData(int playHandle, string fileName);
    [DllImport(NetSdk)] public static extern bool NET_DVR_StopPlayBackSave(int playHandle);
    [DllImport(NetSdk, CharSet = CharSet.Ansi)] public static extern bool NET_DVR_SaveRealData_V30(int realHandle, uint transType, string fileName);
    [DllImport(NetSdk)] public static extern bool NET_DVR_StopSaveRealData(int realHandle);

    // NET_DVR_API BOOL __stdcall NET_DVR_RenderPrivateData(LONG lRealHandle, int iIntelType, BOOL bTrue);
    // Only affects video the SDK decodes itself (RealPlay with a play window); display-only, nothing is sent to the device.
    [DllImport(NetSdk)] public static extern bool NET_DVR_RenderPrivateData(int realHandle, int intelType, bool enable);

    // NET_DVR_API BOOL __stdcall NET_DVR_GetDVRConfig(LONG lUserID, DWORD dwCommand, LONG lChannel, LPVOID lpOutBuffer,
    //     DWORD dwOutBufferSize, LPDWORD lpBytesReturned);
    // Read only. Used for NET_DVR_GET_PICCFG_V40 (camera name, Hik/HikChannelConfig.cs). The matching
    // NET_DVR_SetDVRConfig is deliberately NOT declared here: its only declaration is private to HikChannelConfig,
    // behind SdkWritePolicy (one allowed command: NET_DVR_SET_PICCFG_V40, name bytes only).
    [DllImport(NetSdk)] public static extern bool NET_DVR_GetDVRConfig(int userId, uint command, int channel, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned);

    // NET_DVR_API LONG __stdcall NET_DVR_GetPlayBackPlayerIndex(LONG lPlayHandle);
    // The PlayCtrl port the SDK itself decodes/draws a playback with (PlayBackByTime_V40 got a window); -1 if none.
    [DllImport(NetSdk)] public static extern int NET_DVR_GetPlayBackPlayerIndex(int playHandle);

    // ---- PlayCtrl ----

    [DllImport(PlayCtrl)] public static extern bool PlayM4_GetPort(ref int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_FreePort(int port);
    [DllImport(PlayCtrl)] public static extern uint PlayM4_GetLastError(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetStreamOpenMode(int port, uint mode);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_OpenStream(int port, IntPtr header, uint size, uint bufPoolSize);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_CloseStream(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_InputData(int port, IntPtr buffer, uint size);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetDisplayBuf(int port, uint frames);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetSecretKey(int port, int keyType, string key, int keyLen);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetDecodeEngineEx(int port, uint engine);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_Play(int port, IntPtr wnd);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_Stop(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_Pause(int port, uint pause);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_Fast(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_Slow(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_GetPictureSize(int port, ref int width, ref int height);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_GetJPEG(int port, IntPtr jpeg, uint bufSize, ref uint jpegSize);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetOverlayMode(int port, bool overlay, uint colorKey);
    // PLAYM4_API BOOL __stdcall PlayM4_RefreshPlay(LONG nPort): draws the current picture again (e.g. paused, window re-shown).
    [DllImport(PlayCtrl)] public static extern bool PlayM4_RefreshPlay(int port);

    // PLAYM4_API BOOL __stdcall PlayM4_RenderPrivateData(LONG nPort, int nIntelType, BOOL bTrue);
    // LONG and BOOL are 32-bit on Windows x64 (int / 4-byte bool); display-only, per port.
    [DllImport(PlayCtrl)] public static extern bool PlayM4_RenderPrivateData(int port, int intelType, bool enable);
    // PLAYM4_API BOOL __stdcall PlayM4_SetDecCallBackMend(LONG nPort, DecCBFun, long nUser): decoded (decrypted) pictures.
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetDecCallBackMend(int port, DecodeCallback? callback, int user);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_SetDecCBStream(int port, uint stream);

    // PLAYM4_API BOOL __stdcall PlayM4_GetSystemTime(LONG nPort, PLAYM4_SYSTEM_TIME* pstSystemTime): the absolute time
    // carried by the stream for the current frame (the time the camera's OSD shows). Local call, no network.
    [DllImport(PlayCtrl)] public static extern bool PlayM4_GetSystemTime(int port, ref Pm4SystemTime time);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_PlaySound(int port);
    [DllImport(PlayCtrl)] public static extern bool PlayM4_StopSound();
}
