using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SPVision.Core;

namespace SPVision.Controls;

/// <summary>
/// Mini diagram of a <see cref="GridLayout"/>: every tile as a small rounded cell, big tiles spanning
/// their cells, with even gaps. Cells use the inherited foreground, so the diagram follows the
/// selected/hover state of the button it sits in, like text would.
/// </summary>
public sealed class LayoutDiagram : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(GridLayout), typeof(LayoutDiagram),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Gap between cells and around them, in DIPs.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(LayoutDiagram),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LayoutDiagram),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public LayoutDiagram()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
    }

    public GridLayout? Layout
    {
        get => (GridLayout?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Layout is not { } layout)
        {
            return;
        }
        var gap = Gap;
        var cellWidth = (ActualWidth - gap * (layout.Columns + 1)) / layout.Columns;
        var cellHeight = (ActualHeight - gap * (layout.Rows + 1)) / layout.Rows;
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            return;
        }
        foreach (var cell in layout.Cells)
        {
            var rect = new Rect(
                gap + cell.Column * (cellWidth + gap),
                gap + cell.Row * (cellHeight + gap),
                cell.ColumnSpan * cellWidth + (cell.ColumnSpan - 1) * gap,
                cell.RowSpan * cellHeight + (cell.RowSpan - 1) * gap);
            dc.DrawRoundedRectangle(Foreground, null, rect, 1, 1);
        }
    }
}
