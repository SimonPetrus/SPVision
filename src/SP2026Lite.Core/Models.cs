namespace SP2026Lite.Core;

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
    public List<Camera> Cameras { get; set; } = [];

    public string? StreamKeyFor(Camera camera) =>
        string.IsNullOrEmpty(camera.StreamKey) ? StreamKey : camera.StreamKey;
}

public sealed class Camera
{
    /// <summary>ISAPI channel id (1-based). RTSP uses {Channel}01 for main and {Channel}02 for sub stream.</summary>
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    public bool Online { get; set; } = true;

    /// <summary>Optional per-camera encryption key; encrypted with DPAPI on disk.</summary>
    public string? StreamKey { get; set; }
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

    /// <summary>Tile count of the <see cref="GridLayout"/> (6, 8, 25 or 32).</summary>
    public int? Layout { get; set; }

    /// <summary>Legacy (1/4/9/16 grids): column count of a square grid. Only read to migrate old settings.</summary>
    public int? Columns { get; set; }

    public List<CameraRef?> Tiles { get; set; } = [];
}

public sealed record CameraRef(Guid DeviceId, int Channel);

public sealed class AppSettings
{
    public List<Device> Devices { get; set; } = [];
    public List<SavedView> Views { get; set; } = [];

    /// <summary>Layout restored on startup.</summary>
    public SavedView? LastView { get; set; }

    public string SnapshotFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SP-2026 Lite");

    public string RecordingFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SP-2026 Lite");

    /// <summary>Live view quality for all tiles: null = automatic by tile size.</summary>
    public StreamQuality? LiveQuality { get; set; }

    /// <summary>Use the sub stream when a tile is smaller than this many pixels high.</summary>
    public int MainStreamMinTileHeight { get; set; } = 540;
}

public sealed record DeviceDetails(string Name, string? Model, string? SerialNumber, string? Firmware);

public sealed record StreamEncryptionInfo(bool Enabled, string? VerificationCode);

public sealed record RecordingSegment(DateTime Start, DateTime End, string PlaybackUri);
