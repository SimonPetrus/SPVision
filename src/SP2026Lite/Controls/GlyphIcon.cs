using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SP2026Lite.Controls;

/// <summary>
/// Draws a 24×24 stroke icon (the geometries in Themes/Dark.xaml) at <see cref="Size"/> pixels.
/// The colour follows the inherited foreground, so an icon inside a button changes with the
/// button's hover/checked state like its text would.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    const double DesignSize = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional geometry filled with the icon colour (e.g. the dot of the record icon).</summary>
    public static readonly DependencyProperty FillDataProperty = DependencyProperty.Register(
        nameof(FillData), typeof(Geometry), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public GlyphIcon()
    {
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Geometry? FillData
    {
        get => (Geometry?)GetValue(FillDataProperty);
        set => SetValue(FillDataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Size / DesignSize;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        if (Data is { } data)
        {
            dc.DrawGeometry(null, pen, data);
        }
        if (FillData is { } fill)
        {
            dc.DrawGeometry(Foreground, null, fill);
        }
        dc.Pop();
    }
}
