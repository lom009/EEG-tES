using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace EGGtCSPlatform.Controls;

public class StimulationChannelAllocationEditor : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationEditor, IEnumerable?>(
            nameof(ItemsSource)
        );

    public static readonly StyledProperty<string?> StatusTextProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationEditor,
        string?
    >(nameof(StatusText));

    public static readonly StyledProperty<bool> HasErrorProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationEditor,
        bool
    >(nameof(HasError));

    public static readonly StyledProperty<bool> IsPeakAboveSelectableTotalProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationEditor, bool>(
            nameof(IsPeakAboveSelectableTotal)
        );

    public static readonly StyledProperty<bool> IsPeakBelowSelectableTotalProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationEditor, bool>(
            nameof(IsPeakBelowSelectableTotal)
        );

    public static readonly StyledProperty<ICommand?> SelectFixedCommandProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationEditor, ICommand?>(
            nameof(SelectFixedCommand)
        );

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public bool HasError
    {
        get => GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
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

    public ICommand? SelectFixedCommand
    {
        get => GetValue(SelectFixedCommandProperty);
        set => SetValue(SelectFixedCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HasErrorProperty)
            PseudoClasses.Set(":error", change.GetNewValue<bool>());
        else if (change.Property == IsPeakAboveSelectableTotalProperty)
            PseudoClasses.Set(":peak-above-selectable-total", change.GetNewValue<bool>());
        else if (change.Property == IsPeakBelowSelectableTotalProperty)
            PseudoClasses.Set(":peak-below-selectable-total", change.GetNewValue<bool>());
    }
}
