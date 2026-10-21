using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SPVision.Controls;

/// <summary>
/// The hover tool bar of a playback tile (Hikvision-style): Resim al · Klip kaydet · Yakınlaştır · İndir. It lives in a
/// Popup the playback view places over the bottom edge of the hovered tile's video area (a Popup is its own top-level
/// window, the only thing that can be drawn over the video HWND); the view polls the cursor and moves / hides it. The
/// buttons act on <see cref="Tile"/> and never take the keyboard focus.
/// </summary>
public partial class TileToolBar : UserControl
{
    public TileToolBar()
    {
        InitializeComponent();
    }

    /// <summary>The tile the bar is shown for.</summary>
    public PlaybackTile? Tile { get; private set; }

    public event Action<PlaybackTile>? SnapshotRequested;
    public event Action<PlaybackTile>? ClipRequested;
    public event Action<PlaybackTile>? ZoomRequested;
    public event Action<PlaybackTile>? DownloadRequested;

    /// <summary>Buttons follow the tile's state: a picture for snapshot / clip, the clip's active state, zoom on the SDK path.</summary>
    public void Update(PlaybackTile tile, bool downloadOpen)
    {
        Tile = tile;
        var picture = tile.HasPicture;
        SnapshotButton.IsEnabled = picture;
        SnapshotButton.ToolTip = picture ? "Resim al" : "Resim al (görüntü yok)";

        var clipping = tile.IsClipping;
        ClipButton.IsEnabled = clipping || picture;
        ClipIcon.Data = (Geometry)FindResource(clipping ? "IconStopSquare" : "IconRecord");
        ClipIcon.FillData = (Geometry)FindResource(clipping ? "IconStopSquare" : "IconRecordDot");
        ClipIcon.SetResourceReference(GlyphIcon.ForegroundProperty, clipping ? "TileBarRecord" : "TileBarFg");
        SetActive(ClipButton, clipping);
        var clipText = clipping ? "Klibi bitir" : "Klip kaydet";
        ClipButton.ToolTip = clipText;
        AutomationProperties.SetName(ClipButton, clipText);

        var zoomed = tile.IsZoomed;
        ZoomButton.IsEnabled = zoomed || tile.CanZoom;
        SetActive(ZoomButton, zoomed);
        var zoomText = zoomed ? "Yakınlaştırmayı kapat (tekerlek: yakınlaştır, sürükle: kaydır)"
            : tile.CanZoom ? "Yakınlaştır" : "Yakınlaştır (yalnız şifreli akışta, görüntü gelince)";
        ZoomButton.ToolTip = zoomText;
        AutomationProperties.SetName(ZoomButton, zoomed ? "Yakınlaştırmayı kapat" : "Yakınlaştır");

        DownloadButton.IsEnabled = tile.Camera is not null;
        SetActive(DownloadButton, downloadOpen);
    }

    static void SetActive(Button button, bool active)
    {
        if (active)
        {
            button.SetResourceReference(BackgroundProperty, "TileBarActiveBg");
        }
        else
        {
            button.Background = Brushes.Transparent;
        }
    }

    void Snapshot_Click(object sender, RoutedEventArgs e) => Raise(SnapshotRequested);

    void Clip_Click(object sender, RoutedEventArgs e) => Raise(ClipRequested);

    void Zoom_Click(object sender, RoutedEventArgs e) => Raise(ZoomRequested);

    void Download_Click(object sender, RoutedEventArgs e) => Raise(DownloadRequested);

    void Raise(Action<PlaybackTile>? handler)
    {
        if (Tile is { } tile)
        {
            handler?.Invoke(tile);
        }
    }
}
