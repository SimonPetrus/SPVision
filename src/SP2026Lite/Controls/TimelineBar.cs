using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SP2026Lite.Core;

namespace SP2026Lite.Controls;

/// <summary>
/// Day timeline: recorded segments as bars, a play-head, click to seek, mouse wheel to zoom, drag to pan.
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
    static readonly Brush Background = Frozen(Color.FromRgb(0x1E, 0x21, 0x28));
    static readonly Brush SegmentBrush = Frozen(Color.FromRgb(0x46, 0xA7, 0x58));
    static readonly Brush HeadBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x4D));
    static readonly Brush HoverBrush = Frozen(Color.FromArgb(0x90, 0xE6, 0xE8, 0xEC));
    static readonly Brush TextBrush = Frozen(Color.FromRgb(0x8B, 0x92, 0xA0));
    static readonly Pen TickPen = new(Frozen(Color.FromRgb(0x3A, 0x3F, 0x4A)), 1);
    static readonly Pen HeadPen = new(HeadBrush, 2);
    static readonly Pen HoverPen = new(HoverBrush, 1);
    static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(10);
    static readonly TimeSpan[] TickSteps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
         TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3)];

    DateTime _day = DateTime.Today;
    DateTime _viewStart = DateTime.Today;
    TimeSpan _viewSpan = TimeSpan.FromDays(1);
    IReadOnlyList<RecordingSegment> _segments = [];
    DateTime? _position;
    double? _hoverX;
    Point? _dragStart;
    DateTime _dragViewStart;
    bool _dragged;

    public TimelineBar()
    {
        Height = 56;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
    }

    public event Action<DateTime>? TimeSelected;

    public void SetDay(DateTime day, IReadOnlyList<RecordingSegment> segments)
    {
        _day = day.Date;
        _viewStart = _day;
        _viewSpan = TimeSpan.FromDays(1);
        _segments = segments;
        _position = null;
        InvalidateVisual();
    }

    public DateTime? Position
    {
        get => _position;
        set
        {
            _position = value;
            // Keep the play-head visible when zoomed in.
            if (value is { } p && (p < _viewStart || p > _viewStart + _viewSpan))
            {
                _viewStart = Clamp(p - _viewSpan / 2);
            }
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (width <= 0)
        {
            return;
        }

        var barTop = 22.0;
        var barHeight = height - barTop - 6;
        foreach (var segment in _segments)
        {
            var x1 = Math.Max(0, ToX(segment.Start));
            var x2 = Math.Min(width, ToX(segment.End));
            if (x2 > x1)
            {
                dc.DrawRectangle(SegmentBrush, null, new Rect(x1, barTop, Math.Max(1, x2 - x1), barHeight));
            }
        }

        var step = TickSteps.FirstOrDefault(s => width / (_viewSpan / s) >= 60, TickSteps[^1]);
        var first = new DateTime((_viewStart.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var t = first; t <= _viewStart + _viewSpan; t += step)
        {
            var x = ToX(t);
            dc.DrawLine(TickPen, new Point(x, 14), new Point(x, height));
            var label = new FormattedText(t.ToString(t == t.Date && t != _day ? "24:00" : "HH:mm", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, TextBrush, dpi);
            dc.DrawText(label, new Point(x + 3, 2));
        }

        if (_hoverX is { } hx)
        {
            dc.DrawLine(HoverPen, new Point(hx, barTop - 4), new Point(hx, height));
            var label = new FormattedText(ToTime(hx).ToString("HH:mm:ss"), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, HoverBrush, dpi);
            var lx = Math.Min(Math.Max(0, hx - label.Width / 2), width - label.Width);
            dc.DrawRectangle(Background, null, new Rect(lx - 2, 1, label.Width + 4, label.Height));
            dc.DrawText(label, new Point(lx, 1));
        }

        if (_position is { } p)
        {
            var x = ToX(p);
            dc.DrawLine(HeadPen, new Point(x, 12), new Point(x, height));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var anchor = ToTime(e.GetPosition(this).X);
        var factor = e.Delta > 0 ? 0.7 : 1 / 0.7;
        var span = TimeSpan.FromTicks((long)(_viewSpan.Ticks * factor));
        _viewSpan = span < MinSpan ? MinSpan : span > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : span;
        var ratio = e.GetPosition(this).X / ActualWidth;
        _viewStart = Clamp(anchor - _viewSpan * ratio);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragViewStart = _viewStart;
        _dragged = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = p.X - start.X;
            if (Math.Abs(dx) > 4)
            {
                _dragged = true;
            }
            if (_dragged)
            {
                _viewStart = Clamp(_dragViewStart - _viewSpan * (dx / ActualWidth));
            }
        }
        _hoverX = p.X;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragStart is not null && !_dragged)
        {
            TimeSelected?.Invoke(ToTime(e.GetPosition(this).X));
        }
        _dragStart = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    double ToX(DateTime t) => (t - _viewStart) / _viewSpan * ActualWidth;

    DateTime ToTime(double x) => _viewStart + _viewSpan * (Math.Clamp(x, 0, ActualWidth) / ActualWidth);

    DateTime Clamp(DateTime start)
    {
        var max = _day.AddDays(1) - _viewSpan;
        return start < _day ? _day : start > max ? max : start;
    }

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
