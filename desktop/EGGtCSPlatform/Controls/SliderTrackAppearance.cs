using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace EGGtCSPlatform.Controls;

/// <summary>Optional track geometry for the shared slider theme; leaves the hit area unchanged.</summary>
public sealed class SliderTrackAppearance : AvaloniaObject
{
    public static readonly FuncValueConverter<double, bool> HasTrackLength = new(width => width > 0);

    public static readonly AttachedProperty<Thickness> FillMarginProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackAppearance, Control, Thickness>("FillMargin");

    public static Thickness GetFillMargin(Control control) => control.GetValue(FillMarginProperty);
    public static void SetFillMargin(Control control, Thickness value) => control.SetValue(FillMarginProperty, value);

    public static readonly AttachedProperty<double> HeightProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackAppearance, Control, double>("Height", 10);

    public static readonly AttachedProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<SliderTrackAppearance, Control, CornerRadius>(
            "CornerRadius", new CornerRadius(2));

    public static double GetHeight(Control control) => control.GetValue(HeightProperty);
    public static void SetHeight(Control control, double value) => control.SetValue(HeightProperty, value);
    public static CornerRadius GetCornerRadius(Control control) => control.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(Control control, CornerRadius value) => control.SetValue(CornerRadiusProperty, value);
}
