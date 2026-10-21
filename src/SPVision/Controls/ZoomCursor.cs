using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SPVision.Controls;

/// <summary>
/// The magnifier cursor of the playback zoom mode (Windows has no stock one). Drawn once at the screen's DPI: a lens with
/// a "+" and a handle, black strokes on a white outline so it reads on light and dark pictures; the hot spot is the
/// lens centre. Built as a PNG-in-.cur stream (supported since Windows Vista).
/// </summary>
static class ZoomCursor
{
    static Cursor? _magnifier;

    public static Cursor Magnifier => _magnifier ??= Create();

    static Cursor Create()
    {
        var scale = Application.Current?.MainWindow is { } window ? VisualTreeHelper.GetDpi(window).DpiScaleX : 1.0;
        var size = (int)Math.Round(32 * scale);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            var centre = new Point(12, 12);
            var outline = new Pen(Brushes.White, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var ink = new Pen(Brushes.Black, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var handleOutline = new Pen(Brushes.White, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var handleInk = new Pen(Brushes.Black, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(handleOutline, new Point(18, 18), new Point(27, 27));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), outline, centre, 8, 8);
            dc.DrawEllipse(null, ink, centre, 8, 8);
            dc.DrawLine(handleInk, new Point(18, 18), new Point(27, 27));
            dc.DrawLine(outline, new Point(8, 12), new Point(16, 12));
            dc.DrawLine(outline, new Point(12, 8), new Point(12, 16));
            dc.DrawLine(ink, new Point(8, 12), new Point(16, 12));
            dc.DrawLine(ink, new Point(12, 8), new Point(12, 16));
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        // ICONDIR (type 2 = cursor) + one ICONDIRENTRY whose "planes/bit count" fields hold the hot spot.
        var hot = (ushort)Math.Round(12 * scale);
        var cur = new MemoryStream();
        using (var w = new BinaryWriter(cur, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);
            w.Write((ushort)2);
            w.Write((ushort)1);
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write(hot);
            w.Write(hot);
            w.Write((uint)png.Length);
            w.Write((uint)22);
            w.Write(png.ToArray());
        }
        cur.Position = 0;
        return new Cursor(cur);
    }
}
