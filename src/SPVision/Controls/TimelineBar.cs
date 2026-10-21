using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SPVision.Core;

namespace SPVision.Controls;

/// <summary>
/// Day timeline of the focused camera, 50 px high: a rounded box with the recorded segments on an inner
/// track, the shared play-head and hour labels below. Click to seek, mouse wheel (or the −/+ buttons beside
/// it) to zoom, drag to pan. Labels stay inside the box: the day's first label is anchored left, the last one
/// right, the others centred on their tick (and pushed inward when the view edge cuts them).
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
    const double BoxHeight = 50;
    const double TrackInset = 8;    // left/right padding of the track inside the box
    const double TrackTop = 9;
    const double TrackHeight = 18;
    const double LabelTop = 31;
    const double LabelSize = 10.5;
    const double MinLabelSpacing = 60;

    static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(10);
    static readonly TimeSpan[] TickSteps =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
         TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];

    // Colours come from the active theme (Themes/Light.xaml or Dark.xaml) and are looked up again after a switch.
    Brush _boxBrush = Brushes.Transparent;
    Pen _boxPen = new(Brushes.Transparent, 1);
    Brush _trackBrush = Brushes.Transparent;
    Brush _segmentBrush = Brushes.Transparent;
    Brush _headBrush = Brushes.Transparent;
    Brush _textBrush = Brushes.Transparent;
    Brush _hoverBrush = Brushes.Transparent;
    Pen _hoverPen = new(Brushes.Transparent, 1);

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
        Height = BoxHeight;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        ToolTip = "Fare tekerleği ile yakınlaştırın, tıklayarak atlayın";
        System.Windows.Automation.AutomationProperties.SetName(this, "Zaman çizelgesi");
        LoadThemeBrushes();
        Loaded += (_, _) => ThemeManager.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>A time was clicked.</summary>
    public event Action<DateTime>? TimeSelected;

    /// <summary>Zoom or pan changed (the −/+ buttons follow <see cref="CanZoomIn"/> / <see cref="CanZoomOut"/>).</summary>
    public event Action? ViewChanged;

    public TimeSpan ViewSpan => _viewSpan;

    public bool CanZoomIn => _viewSpan > MinSpan;

    public bool CanZoomOut => _viewSpan < TimeSpan.FromDays(1);

    void OnThemeChanged()
    {
        LoadThemeBrushes();
        InvalidateVisual();
    }

    void LoadThemeBrushes()
    {
        _boxBrush = Theme("InputBg");
        _boxPen = new Pen(Theme("Border"), 1);
        _trackBrush = Theme("Divider");
        _segmentBrush = Theme("Recorded");
        _headBrush = Theme("TextStrong");
        _textBrush = Theme("TextSecondary");
        _hoverBrush = Theme("TextFg");
        _hoverPen = new Pen(Theme("TextSecondary"), 1);
    }

    /// <summary>New day or new focused camera: whole day in view.</summary>
    public void SetDay(DateTime day, IReadOnlyList<RecordingSegment> segments)
    {
        if (day.Date != _day)
        {
            _day = day.Date;
            _viewStart = _day;
            _viewSpan = TimeSpan.FromDays(1);
            ViewChanged?.Invoke();
        }
        _segments = segments;
        InvalidateVisual();
    }

    /// <summary>The shared play-head; null hides it.</summary>
    public DateTime? Position
    {
        get => _position;
        set
        {
            if (_position == value)
            {
                return;
            }
            _position = value;
            // Keep the play-head visible when zoomed in.
            if (value is { } p && (p < _viewStart || p > _viewStart + _viewSpan))
            {
                _viewStart = Clamp(p - _viewSpan / 2);
                ViewChanged?.Invoke();
            }
            InvalidateVisual();
        }
    }

    public void ZoomIn() => Zoom(0.5, AnchorTime(), null);

    public void ZoomOut() => Zoom(2, AnchorTime(), null);

    /// <summary>Buttons zoom around the play-head when it is in view, otherwise around the centre.</summary>
    DateTime AnchorTime() =>
        _position is { } p && p >= _viewStart && p <= _viewStart + _viewSpan ? p : _viewStart + _viewSpan / 2;

    void Zoom(double factor, DateTime anchor, double? anchorRatio)
    {
        var span = TimeSpan.FromTicks((long)(_viewSpan.Ticks * factor));
        span = span < MinSpan ? MinSpan : span > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : span;
        var ratio = anchorRatio ?? (anchor - _viewStart) / _viewSpan;
        _viewSpan = span;
        _viewStart = Clamp(anchor - _viewSpan * ratio);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    double TrackWidth => Math.Max(1, ActualWidth - 2 * TrackInset);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 2 * TrackInset)
        {
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRoundedRectangle(_boxBrush, _boxPen, new Rect(0.5, 0.5, width - 1, BoxHeight - 1), 6, 6);

        // Track and recorded segments (clipped to the track).
        var track = new Rect(TrackInset, TrackTop, TrackWidth, TrackHeight);
        dc.DrawRoundedRectangle(_trackBrush, null, track, 2, 2);
        dc.PushClip(new RectangleGeometry(track, 2, 2));
        foreach (var segment in _segments)
        {
            var x1 = Math.Max(track.Left, ToX(segment.Start));
            var x2 = Math.Min(track.Right, ToX(segment.End));
            if (x2 > x1)
            {
                dc.DrawRectangle(_segmentBrush, null, new Rect(x1, track.Top, Math.Max(1, x2 - x1), track.Height));
            }
        }
        dc.Pop();

        // Hour labels: day start anchored left, day end anchored right, the rest centred; never outside the track span.
        var step = TickSteps.FirstOrDefault(s => TrackWidth / (_viewSpan / s) >= MinLabelSpacing, TickSteps[^1]);
        var first = new DateTime((_viewStart.Ticks + step.Ticks - 1) / step.Ticks * step.Ticks);
        var hovering = _hoverX is not null;
        for (var t = first; t <= _viewStart + _viewSpan; t += step)
        {
            var label = Text(t == _day.AddDays(1) ? "24:00" : t.ToString("HH:mm", CultureInfo.InvariantCulture), _textBrush, dpi, FontWeights.Normal);
            var x = ToX(t);
            var left = t == _day ? x : t == _day.AddDays(1) ? x - label.Width : x - label.Width / 2;
            dc.DrawText(label, new Point(Math.Clamp(left, track.Left, track.Right - label.Width), LabelTop));
        }

        if (_hoverX is { } hx && hovering)
        {
            var x = Math.Clamp(hx, track.Left, track.Right);
            dc.DrawLine(_hoverPen, new Point(x, track.Top), new Point(x, track.Bottom));
            // Hover time in the label row, over the hour labels (box-coloured backing so they do not show through).
            var label = Text(ToTime(x).ToString("HH:mm:ss", CultureInfo.InvariantCulture), _hoverBrush, dpi, FontWeights.SemiBold);
            var lx = Math.Clamp(x - label.Width / 2, track.Left, track.Right - label.Width);
            dc.DrawRectangle(_boxBrush, null, new Rect(lx - 3, LabelTop - 1, label.Width + 6, label.Height + 2));
            dc.DrawText(label, new Point(lx, LabelTop));
        }

        if (_position is { } p && p >= _viewStart && p <= _viewStart + _viewSpan)
        {
            // 2 px line reaching 5 px beyond the track, with a 10×6 cap on top, both centred on the time.
            var x = ToX(p);
            dc.DrawRectangle(_headBrush, null, new Rect(x - 1, track.Top - 5, 2, track.Height + 10));
            dc.DrawRoundedRectangle(_headBrush, null, new Rect(x - 5, track.Top - 9, 10, 6), 1, 1);
        }
    }

    FormattedText Text(string text, Brush brush, double dpi, FontWeight weight) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), LabelSize, brush, dpi);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var x = e.GetPosition(this).X;
        Zoom(e.Delta > 0 ? 0.7 : 1 / 0.7, ToTime(x), Math.Clamp((x - TrackInset) / TrackWidth, 0, 1));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragViewStart = _viewStart;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
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
                _viewStart = Clamp(_dragViewStart - _viewSpan * (dx / TrackWidth));
                ViewChanged?.Invoke();
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
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    double ToX(DateTime t) => TrackInset + (t - _viewStart) / _viewSpan * TrackWidth;

    DateTime ToTime(double x) => _viewStart + _viewSpan * (Math.Clamp(x - TrackInset, 0, TrackWidth) / TrackWidth);

    DateTime Clamp(DateTime start)
    {
        var max = _day.AddDays(1) - _viewSpan;
        return start < _day ? _day : start > max ? max : start;
    }

    static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
}
