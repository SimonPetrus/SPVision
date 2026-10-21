using System.Text.Json.Serialization;

namespace SPVision.Core;

/// <summary>A recorder (NVR/DVR) or standalone camera reachable over ISAPI + RTSP.</summary>
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int HttpPort { get; set; } = 80;
    public int RtspPort { get; set; } = 554;

    /// <summary>HCNetSDK login port (NET_DVR_Login_V40). Hikvision default is 8000.</summary>
    public int SdkPort { get; set; } = 8000;
    public bool UseHttps { get; set; }
    public string UserName { get; set; } = "admin";

    /// <summary>Plain text in memory; encrypted with DPAPI on disk.</summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Video encryption key ("akış anahtarı" / verification code) shared by all cameras of this device.
    /// A camera-specific key in <see cref="Camera.StreamKey"/> takes precedence.
    /// </summary>
    public string? StreamKey { get; set; }

    public string? Model { get; set; }
    public string? SerialNumber { get; set; }

    /// <summary>ISAPI deviceInfo deviceType (e.g. "NVR", "IPCamera"); shown in the Settings device list.</summary>
    public string? DeviceType { get; set; }

    /// <summary>IP channel capacity reported by the SDK login (e.g. 32); null until known.</summary>
    public int? ChannelCount { get; set; }

    /// <summary>
    /// The HTTPS certificate trusted for ISAPI (trust on first use, <see cref="CertificateTrust"/>); null = not pinned yet
    /// (the next HTTPS connection stores it). Not secret. HCNetSDK (port 8000) and RTSP do not use it.
    /// </summary>
    public CertificatePin? Certificate { get; set; }

    public List<Camera> Cameras { get; set; } = [];

    public string? StreamKeyFor(Camera camera) =>
        string.IsNullOrEmpty(camera.StreamKey) ? StreamKey : camera.StreamKey;

    /// <summary>Cameras shown in the live sidebar list (not hidden in Settings), in channel order.</summary>
    public IEnumerable<Camera> ListedCameras() => Cameras.Where(c => !c.Hidden).OrderBy(c => c.Channel);
}

public sealed class Camera
{
    /// <summary>ISAPI channel id (1-based). RTSP uses {Channel}01 for main and {Channel}02 for sub stream.</summary>
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    public bool Online { get; set; } = true;

    /// <summary>Optional per-camera encryption key; encrypted with DPAPI on disk.</summary>
    public string? StreamKey { get; set; }

    /// <summary>Settings › "Listede göster" off: the camera is left out of the live sidebar list.</summary>
    public bool Hidden { get; set; }
}

public enum StreamQuality
{
    Main = 1,
    Sub = 2,
}

/// <summary>A layout the user saved: grid layout plus the camera shown in each tile.</summary>
public sealed class SavedView
{
    public string Name { get; set; } = "";

    /// <summary>Tile count of the <see cref="GridLayout"/> (1, 4, 6, 7, 8, 9, 10, 13, 16, 22, 25 or 36; a legacy 32 opens as 36).</summary>
    public int? Layout { get; set; }

    /// <summary>Legacy (1/4/9/16 grids): column count of a square grid. Only read to migrate old settings.</summary>
    public int? Columns { get; set; }

    /// <summary>
    /// Live view: the global camera order (<see cref="LivePages.Slots"/>), page p of a layout with N tiles shows
    /// Tiles[p*N .. p*N+N-1]; null = empty tile. Views saved before paging held only the tiles of one layout,
    /// which is the start of the same order.
    /// </summary>
    public List<CameraRef?> Tiles { get; set; } = [];

    /// <summary>Live view: zero-based page shown when the view was saved (clamped on restore).</summary>
    public int Page { get; set; }
}

public sealed record CameraRef(Guid DeviceId, int Channel);

public sealed class AppSettings
{
    public List<Device> Devices { get; set; } = [];
    public List<SavedView> Views { get; set; } = [];

    /// <summary>Layout restored on startup.</summary>
    public SavedView? LastView { get; set; }

    /// <summary>
    /// Kayıt Oynatma: layout and the checked cameras in tile order (<see cref="SavedView.Tiles"/>, no gaps), restored on
    /// startup like <see cref="LastView"/> (<see cref="PlaybackRestore"/>). The day and time are not kept.
    /// </summary>
    public SavedView? LastPlaybackView { get; set; }

    public string SnapshotFolder { get; set; } = AppPaths.DefaultSnapshotFolder;

    public string RecordingFolder { get; set; } = AppPaths.DefaultRecordingFolder;

    /// <summary>Live view quality for all tiles: null = automatic by tile size.</summary>
    public StreamQuality? LiveQuality { get; set; }

    /// <summary>
    /// Oto: a tile whose video area is smaller than this many pixels high uses the sub stream (<see cref="AutoStreamQuality"/>;
    /// big tiles of the "büyük kareli" layouts and the 1 and 4 grids are HD whatever their size).
    /// </summary>
    public int MainStreamMinTileHeight { get; set; } = 540;

    /// <summary>Light, dark or follow Windows (default). Stored as text in settings.json.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Draw the recorder's motion / intelligent-analysis boxes on SDK video (PlayCtrl private data).</summary>
    public bool ShowMotionBoxes { get; set; } = true;

    /// <summary>Restore the grid layout, camera order and page on startup (and the playback layout and cameras).</summary>
    public bool RestoreLastView { get; set; } = true;

    /// <summary>Open the main window in full screen (F11).</summary>
    public bool StartFullScreen { get; set; }

    /// <summary>In-app updater state (no secrets): last successful check and a remembered mandatory update.</summary>
    public UpdateState? Update { get; set; }

    /// <summary>
    /// HCNetSDK log written to <c>logs\sdk</c> (<see cref="SdkLog"/>): 0 off, 1 errors only (default), 2 + debug, 3 everything.
    /// Level 3 (the old default) writes several lines per stream event; raise it in settings.json only to diagnose.
    /// </summary>
    public int SdkLogLevel { get; set; } = SdkLog.DefaultLevel;

    /// <summary>
    /// SDK video decodes on the GPU (PlayCtrl hardware decoder) when the driver allows it; false = always the CPU
    /// (fallback if a graphics driver misbehaves; no UI yet, settings.json only).
    /// </summary>
    public bool HardwareDecoding { get; set; } = true;
}

/// <summary>HCNetSDK log level rule (NET_DVR_SetLogToFile: 0 off, 1 errors, 2 errors + debug, 3 all).</summary>
public static class SdkLog
{
    public const int DefaultLevel = 1;

    /// <summary>The level passed to the SDK: the setting, kept within 0–3.</summary>
    public static uint Level(int setting) => (uint)Math.Clamp(setting, 0, 3);
}

/// <summary>
/// What the updater remembers between runs. The manifests are the exact bytes (base64) of signed manifest.json files with
/// their .sig; they are verified again on every use, so editing settings.json cannot force or fake an update. Nothing here
/// is secret. <see cref="LatestManifest"/>: the last successful check's result (an optional offer survives a restart within
/// the 6 h check interval). <see cref="MandatoryManifest"/>: a manifest that made the update mandatory — still enforced
/// when later checks fail or a newer release is optional, until that version is installed.
/// </summary>
public sealed class UpdateState
{
    /// <summary>Last successful automatic or manual check (UTC); automatic checks run at most every 6 hours.</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    public string? LatestManifest { get; set; }

    public string? LatestSignature { get; set; }

    public string? MandatoryManifest { get; set; }

    public string? MandatorySignature { get; set; }
}

public sealed record DeviceDetails(string Name, string? Model, string? SerialNumber, string? Firmware, string? DeviceType = null);

public sealed record StreamEncryptionInfo(bool Enabled, string? VerificationCode);

public sealed record RecordingSegment(DateTime Start, DateTime End, string PlaybackUri);
