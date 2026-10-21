namespace SPVision.Core;

/// <summary>
/// Digital zoom of a playback tile (display only: PlayCtrl shows a source rectangle of the picture in the window; the
/// recording and a clip / download are not affected). <see cref="Level"/> 1 = whole picture; the centre is in picture
/// coordinates 0…1 and is kept so the zoomed region never leaves the picture.
/// </summary>
public readonly record struct DigitalZoom(double Level, double CenterX, double CenterY)
{
    public const double MaxLevel = 8;

    /// <summary>One mouse-wheel notch.</summary>
    public const double WheelStep = 1.25;

    /// <summary>The level the tool bar's "Yakınlaştır" switches to.</summary>
    public const double ToggleLevel = 2;

    public static DigitalZoom None { get; } = new(1, 0.5, 0.5);

    public bool IsZoomed => Level > 1.0001;

    /// <summary>Half the visible width / height in picture coordinates.</summary>
    double Half => 0.5 / Level;

    /// <summary>"Yakınlaştır": off → 2× on the centre; on → whole picture.</summary>
    public DigitalZoom Toggle() => IsZoomed ? None : Clamp(new DigitalZoom(ToggleLevel, 0.5, 0.5));

    /// <summary>
    /// Zooms by <paramref name="factor"/> keeping the picture point under (<paramref name="viewX"/>, <paramref name="viewY"/>)
    /// — the cursor, 0…1 in the tile's video area — where it is. Back to 1× ends the zoom.
    /// </summary>
    public DigitalZoom ZoomAt(double factor, double viewX, double viewY)
    {
        var level = Math.Clamp(Level * factor, 1, MaxLevel);
        // The picture point under the cursor now…
        var px = CenterX - Half + Math.Clamp(viewX, 0, 1) * 2 * Half;
        var py = CenterY - Half + Math.Clamp(viewY, 0, 1) * 2 * Half;
        // …stays under it at the new level.
        var half = 0.5 / level;
        var cx = px - (Math.Clamp(viewX, 0, 1) * 2 - 1) * half;
        var cy = py - (Math.Clamp(viewY, 0, 1) * 2 - 1) * half;
        return level <= 1.0001 ? None : Clamp(new DigitalZoom(level, cx, cy));
    }

    /// <summary>Drag: the picture follows the mouse by (<paramref name="viewDx"/>, <paramref name="viewDy"/>) of the video area.</summary>
    public DigitalZoom PanBy(double viewDx, double viewDy) =>
        IsZoomed ? Clamp(this with { CenterX = CenterX - viewDx * 2 * Half, CenterY = CenterY - viewDy * 2 * Half }) : this;

    /// <summary>
    /// The source rectangle in picture pixels (left, top, right, bottom; even, inside the picture, at least 16 px);
    /// null when not zoomed.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom)? Region(int width, int height)
    {
        if (!IsZoomed || width < 32 || height < 32)
        {
            return null;
        }
        var w = Math.Max(16, (int)Math.Round(width / Level)) & ~1;
        var h = Math.Max(16, (int)Math.Round(height / Level)) & ~1;
        var left = Math.Clamp((int)Math.Round(CenterX * width - w / 2.0), 0, width - w) & ~1;
        var top = Math.Clamp((int)Math.Round(CenterY * height - h / 2.0), 0, height - h) & ~1;
        return (left, top, left + w, top + h);
    }

    static DigitalZoom Clamp(DigitalZoom zoom)
    {
        var half = 0.5 / zoom.Level;
        return zoom with
        {
            CenterX = Math.Clamp(zoom.CenterX, half, 1 - half),
            CenterY = Math.Clamp(zoom.CenterY, half, 1 - half),
        };
    }
}

/// <summary>
/// The hover tool bar of a playback tile. It is a Popup (its own top-level window: the only thing that can be drawn over
/// the video HWND), so where it goes is computed here: along the bottom edge inside the tile's video area, the full
/// width, <see cref="Height"/> high. It is offered only when the tile is big enough for its buttons, and it lingers
/// <see cref="HideDelay"/> after the mouse left (moving onto the bar must not flicker it).
/// </summary>
public static class TileToolBar
{
    /// <summary>Bar height (DIP).</summary>
    public const double Height = 32;

    /// <summary>Button size (DIP): 28 × 24, 4 apart, 8 from the edge.</summary>
    public const double ButtonWidth = 28;
    public const double ButtonHeight = 24;
    public const double ButtonGap = 4;
    public const double EdgePadding = 8;

    public static TimeSpan HideDelay { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Width the buttons need (DIP).</summary>
    public static double MinWidth(int buttons) => 2 * EdgePadding + buttons * ButtonWidth + Math.Max(0, buttons - 1) * ButtonGap;

    /// <summary>
    /// Bar rectangle (screen pixels) for a video area at (<paramref name="left"/>, <paramref name="top"/>) of the given size
    /// (screen pixels; <paramref name="scale"/> = pixels per DIP); null when the area is too small for it.
    /// </summary>
    public static (double X, double Y, double Width, double Height)? Place(double left, double top, double width, double height, double scale, int buttons)
    {
        var barHeight = Height * scale;
        if (width < MinWidth(buttons) * scale || height < barHeight * 2)
        {
            return null;
        }
        return (left, top + height - barHeight, width, barHeight);
    }

    public static bool Contains((double X, double Y, double Width, double Height) rect, double x, double y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>
    /// Which tile the bar belongs to now: the hovered one; when none is hovered, the last one until
    /// <see cref="HideDelay"/> has passed since the mouse left it.
    /// </summary>
    public static int? Owner(int? hovered, int? shown, DateTime lastHovered, DateTime now) =>
        hovered ?? (shown is { } current && now - lastHovered < HideDelay ? current : null);
}
