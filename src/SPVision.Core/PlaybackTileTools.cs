namespace SPVision.Core;

/// <summary>
/// Digital zoom of a playback tile (display only: PlayCtrl shows a source rectangle of the picture in the window; the
/// recording and a clip / download are not affected). <see cref="Level"/> 1 = whole picture; the centre is in picture
/// coordinates 0…1 and is kept so the zoomed region never leaves the picture. The on/off mode is <see cref="TileZoom"/>.
/// </summary>
public readonly record struct DigitalZoom(double Level, double CenterX, double CenterY)
{
    public const double MaxLevel = 8;

    /// <summary>One mouse-wheel notch (WHEEL_DELTA = 120).</summary>
    public const double WheelStep = 1.25;

    public static DigitalZoom None { get; } = new(1, 0.5, 0.5);

    public bool IsZoomed => Level > 1.0001;

    /// <summary>Half the visible width / height in picture coordinates.</summary>
    double Half => 0.5 / Level;

    /// <summary>Zoom factor of a wheel turn: <see cref="WheelStep"/> per notch, any fraction of one (precision touchpads).</summary>
    public static double WheelFactor(int delta) => Math.Pow(WheelStep, delta / 120.0);

    /// <summary>
    /// The picture point (0…1) shown at (<paramref name="viewX"/>, <paramref name="viewY"/>) — 0…1 across the picture on
    /// screen (<see cref="ZoomGeometry.ViewPoint"/>).
    /// </summary>
    public (double X, double Y) PictureAt(double viewX, double viewY) =>
        (CenterX - Half + Math.Clamp(viewX, 0, 1) * 2 * Half, CenterY - Half + Math.Clamp(viewY, 0, 1) * 2 * Half);

    /// <summary>
    /// Zooms by <paramref name="factor"/> keeping the picture point under (<paramref name="viewX"/>, <paramref name="viewY"/>)
    /// — the cursor, 0…1 across the picture on screen — where it is (as far as the picture's edges allow). 1× is the whole
    /// picture (<see cref="None"/>); whether the zoom mode stays on is <see cref="TileZoom"/>'s business.
    /// </summary>
    public DigitalZoom ZoomAt(double factor, double viewX, double viewY)
    {
        var level = Math.Clamp(Level * factor, 1, MaxLevel);
        // The picture point under the cursor now…
        var (px, py) = PictureAt(viewX, viewY);
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
/// The zoom tool of a playback tile: the tool bar's "Yakınlaştır" only switches the zoom <see cref="Mode"/> on or off (the
/// picture stays as it is); while the mode is on the wheel zooms 1×–8× around the cursor and a drag moves a zoomed
/// picture. Reaching 1× with the wheel keeps the mode on; only the button (or a new camera / Durdur) ends it.
/// </summary>
public readonly record struct TileZoom(bool Mode, DigitalZoom View)
{
    public static TileZoom Off { get; } = new(false, DigitalZoom.None);

    /// <summary>More than the whole picture is magnified (drag pans, the source rectangle is set).</summary>
    public bool IsZoomed => Mode && View.IsZoomed;

    /// <summary>"Yakınlaştır": off → mode on at 1× (nothing moves yet); on → off, the whole picture again.</summary>
    public TileZoom Toggle() => Mode ? Off : new TileZoom(true, DigitalZoom.None);

    /// <summary>A wheel turn (<paramref name="delta"/> as in WM_MOUSEWHEEL) at the cursor (0…1 across the picture); ignored when the mode is off.</summary>
    public TileZoom Wheel(int delta, double viewX, double viewY) =>
        Mode && delta != 0 ? this with { View = View.ZoomAt(DigitalZoom.WheelFactor(delta), viewX, viewY) } : this;

    /// <summary>Drag by a fraction of the picture on screen; only a zoomed picture moves.</summary>
    public TileZoom Pan(double viewDx, double viewDy) => IsZoomed ? this with { View = View.PanBy(viewDx, viewDy) } : this;
}

/// <summary>
/// Where the cursor is on a tile's picture. WPF works in DIPs, the cursor (GetCursorPos, WM_MOUSEWHEEL) and
/// <c>PointToScreen</c> in physical screen pixels: the video area's size is converted with the DPI scale before anything is
/// compared. The picture may be letterboxed in the area (<c>pictureAspect</c> &gt; 0: fitted, centred) or fill it
/// (0: PlayCtrl stretches the picture over the whole window, which is how the SDK path draws).
/// </summary>
public static class ZoomGeometry
{
    /// <summary>The picture's rectangle inside an area of the given size (same unit); the whole area when stretched.</summary>
    public static (double X, double Y, double Width, double Height) PictureRect(double width, double height, double pictureAspect)
    {
        if (pictureAspect <= 0 || width <= 0 || height <= 0)
        {
            return (0, 0, Math.Max(0, width), Math.Max(0, height));
        }
        if (width / height > pictureAspect)
        {
            var w = height * pictureAspect; // pillarbox: bars left and right
            return ((width - w) / 2, 0, w, height);
        }
        var h = width / pictureAspect;      // letterbox: bars above and below
        return (0, (height - h) / 2, width, h);
    }

    /// <summary>
    /// The cursor at screen pixel (<paramref name="screenX"/>, <paramref name="screenY"/>) as 0…1 across the picture, for a
    /// video area whose top-left is at screen pixel (<paramref name="areaLeft"/>, <paramref name="areaTop"/>) and whose size
    /// is in DIPs (<paramref name="scale"/> = pixels per DIP). Null when the cursor is outside the area; over a letterbox bar
    /// it is clamped to the picture's edge.
    /// </summary>
    public static (double X, double Y)? ViewPoint(double screenX, double screenY, double areaLeft, double areaTop,
        double widthDip, double heightDip, double scale, double pictureAspect = 0)
    {
        var width = widthDip * scale;
        var height = heightDip * scale;
        var x = screenX - areaLeft;
        var y = screenY - areaTop;
        if (width < 1 || height < 1 || x < 0 || y < 0 || x >= width || y >= height)
        {
            return null;
        }
        var picture = PictureRect(width, height, pictureAspect);
        return (Math.Clamp((x - picture.X) / picture.Width, 0, 1), Math.Clamp((y - picture.Y) / picture.Height, 0, 1));
    }

    /// <summary>A drag of (<paramref name="dx"/>, <paramref name="dy"/>) screen pixels as a fraction of the picture on screen.</summary>
    public static (double X, double Y) ViewDelta(double dx, double dy, double widthDip, double heightDip, double scale, double pictureAspect = 0)
    {
        var picture = PictureRect(widthDip * scale, heightDip * scale, pictureAspect);
        return picture.Width < 1 || picture.Height < 1 ? (0, 0) : (dx / picture.Width, dy / picture.Height);
    }

    /// <summary>
    /// The source-picture pixel shown at (<paramref name="viewX"/>, <paramref name="viewY"/>) (0…1 across the picture on
    /// screen) when PlayCtrl displays <paramref name="region"/> (null: the whole picture).
    /// </summary>
    public static (double X, double Y) SourcePixel((int Left, int Top, int Right, int Bottom)? region, int width, int height, double viewX, double viewY)
    {
        var (left, top, right, bottom) = region ?? (0, 0, width, height);
        return (left + viewX * (right - left), top + viewY * (bottom - top));
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
