using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;

namespace EGGtCSPlatform.Controls;

public class StimulusParameterEditor : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        string?
    >(nameof(Title));

    public static readonly StyledProperty<string?> InfoTextProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        string?
    >(nameof(InfoText));

    public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        string?
    >(nameof(Unit));

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        double
    >(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        double
    >(nameof(Maximum), 100d);

    public static readonly StyledProperty<double> IncrementProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        double
    >(nameof(Increment), 1d);

    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        double
    >(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> StatusTextProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        string?
    >(nameof(StatusText));

    public static readonly StyledProperty<bool> IsErrorProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        bool
    >(nameof(IsError));

    public static readonly StyledProperty<bool> IsAllocatedProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        bool
    >(nameof(IsAllocated));

    public static readonly StyledProperty<bool> IsPeakAboveSelectableTotalProperty =
        AvaloniaProperty.Register<StimulusParameterEditor, bool>(
            nameof(IsPeakAboveSelectableTotal)
        );

    public static readonly StyledProperty<bool> IsPeakBelowSelectableTotalProperty =
        AvaloniaProperty.Register<StimulusParameterEditor, bool>(
            nameof(IsPeakBelowSelectableTotal)
        );

    public static readonly StyledProperty<IBrush?> ActiveTrackBrushProperty =
        AvaloniaProperty.Register<StimulusParameterEditor, IBrush?>(
            nameof(ActiveTrackBrush),
            new SolidColorBrush(Color.Parse("#3941B6"))
        );

    public static readonly StyledProperty<IBrush?> InactiveTrackBrushProperty =
        AvaloniaProperty.Register<StimulusParameterEditor, IBrush?>(
            nameof(InactiveTrackBrush),
            new SolidColorBrush(Color.Parse("#E2E8F2"))
        );

    public static readonly StyledProperty<int> DecimalPlacesProperty = AvaloniaProperty.Register<
        StimulusParameterEditor,
        int
    >(nameof(DecimalPlaces), 1);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? InfoText
    {
        get => GetValue(InfoTextProperty);
        set => SetValue(InfoTextProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool IsError
    {
        get => GetValue(IsErrorProperty);
        set => SetValue(IsErrorProperty, value);
    }

    public bool IsAllocated
    {
        get => GetValue(IsAllocatedProperty);
        set => SetValue(IsAllocatedProperty, value);
    }

    public bool IsPeakAboveSelectableTotal
    {
        get => GetValue(IsPeakAboveSelectableTotalProperty);
        set => SetValue(IsPeakAboveSelectableTotalProperty, value);
    }

    public bool IsPeakBelowSelectableTotal
    {
        get => GetValue(IsPeakBelowSelectableTotalProperty);
        set => SetValue(IsPeakBelowSelectableTotalProperty, value);
    }

    public IBrush? ActiveTrackBrush
    {
        get => GetValue(ActiveTrackBrushProperty);
        set => SetValue(ActiveTrackBrushProperty, value);
    }

    public IBrush? InactiveTrackBrush
    {
        get => GetValue(InactiveTrackBrushProperty);
        set => SetValue(InactiveTrackBrushProperty, value);
    }

    public int DecimalPlaces
    {
        get => GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsErrorProperty)
            PseudoClasses.Set(":error", change.GetNewValue<bool>());
        else if (change.Property == IsAllocatedProperty)
            PseudoClasses.Set(":allocated", change.GetNewValue<bool>());
        else if (change.Property == IsPeakAboveSelectableTotalProperty)
            PseudoClasses.Set(":peak-above-selectable-total", change.GetNewValue<bool>());
        else if (change.Property == IsPeakBelowSelectableTotalProperty)
            PseudoClasses.Set(":peak-below-selectable-total", change.GetNewValue<bool>());
    }
}
