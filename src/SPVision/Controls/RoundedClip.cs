using System.Windows;
using System.Windows.Media;

namespace SPVision.Controls;

/// <summary>
/// Clips an element to a rounded rectangle of its own size, so row backgrounds (header, hover, expanded row)
/// inside a card with rounded corners do not paint over the corners. Use the card's corner radius minus its border.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }
        element.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue > 0)
        {
            element.SizeChanged += OnSizeChanged;
            Update(element);
        }
        else
        {
            element.Clip = null;
        }
    }

    static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    static void Update(FrameworkElement element)
    {
        var radius = GetRadius(element);
        element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
    }
}
