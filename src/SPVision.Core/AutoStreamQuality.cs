namespace SPVision.Core;

/// <summary>
/// "Oto" live quality of one tile: small tiles use the sub stream (SD), large ones the main stream (HD).
/// <list type="bullet">
/// <item>A maximized tile, a big (spanning) tile of a "büyük kareli" layout and every tile of the 1 and 4 grids: HD,
/// whatever the window size.</item>
/// <item>Any other tile: HD when its video area is at least <c>minMainHeight</c> device pixels high. A tile that is HD
/// already stays HD down to <see cref="Hysteresis"/> below that, so resizing the window around the threshold does
/// not reopen streams back and forth.</item>
/// </list>
/// A tile that is recording is never switched (the caller's job: switching reopens the stream).
/// </summary>
public static class AutoStreamQuality
{
    /// <summary>Uniform grids with at most this many tiles (1, 4) are HD.</summary>
    public const int UniformMainMaxTiles = 4;

    /// <summary>An HD tile drops to SD only below <c>minMainHeight × (1 − Hysteresis)</c>.</summary>
    public const double Hysteresis = 0.1;

    /// <param name="layout">Layout on screen.</param>
    /// <param name="tileIndex">Index of the tile in <see cref="GridLayout.Cells"/>.</param>
    /// <param name="maximized">The tile fills the whole grid (double-click).</param>
    /// <param name="videoHeightPixels">Height of the tile's video area in device pixels.</param>
    /// <param name="minMainHeight"><see cref="AppSettings.MainStreamMinTileHeight"/>.</param>
    /// <param name="current">Stream the tile shows now (for the hysteresis).</param>
    public static StreamQuality Decide(GridLayout layout, int tileIndex, bool maximized, double videoHeightPixels,
        int minMainHeight, StreamQuality current)
    {
        if (maximized || IsLargeByLayout(layout, tileIndex))
        {
            return StreamQuality.Main;
        }
        var threshold = current == StreamQuality.Main ? minMainHeight * (1 - Hysteresis) : minMainHeight;
        return videoHeightPixels >= threshold ? StreamQuality.Main : StreamQuality.Sub;
    }

    /// <summary>HD by the layout alone: a big tile of a "büyük kareli" layout, or a tile of the 1 or 4 grid.</summary>
    public static bool IsLargeByLayout(GridLayout layout, int tileIndex)
    {
        if (layout.IsUniform)
        {
            return layout.TileCount <= UniformMainMaxTiles;
        }
        return tileIndex >= 0 && tileIndex < layout.TileCount && layout.Cells[tileIndex].RowSpan > 1;
    }
}
