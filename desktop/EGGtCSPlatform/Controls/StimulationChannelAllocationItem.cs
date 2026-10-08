using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace EGGtCSPlatform.Controls;

public class StimulationChannelAllocationItem : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        string?
    >(nameof(Label));

    public static readonly StyledProperty<string?> RoleTextProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        string?
    >(nameof(RoleText));

    public static readonly StyledProperty<double> CurrentProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        double
    >(nameof(Current), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumCurrentProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, double>(nameof(MinimumCurrent));

    public static readonly StyledProperty<double> MaximumCurrentProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, double>(nameof(MaximumCurrent));

    public static readonly StyledProperty<double> CurrentIncrementProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, double>(
            nameof(CurrentIncrement)
        );

    public static readonly StyledProperty<bool> IsChannelEnabledProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, bool>(
            nameof(IsChannelEnabled),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<bool> CanToggleProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        bool
    >(nameof(CanToggle));

    public static readonly StyledProperty<bool> CanEditCurrentProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        bool
    >(nameof(CanEditCurrent));

    public static readonly StyledProperty<bool> IsFixedCandidateProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, bool>(nameof(IsFixedCandidate));

    public static readonly StyledProperty<bool> IsSelectedFixedProperty = AvaloniaProperty.Register<
        StimulationChannelAllocationItem,
        bool
    >(nameof(IsSelectedFixed));

    public static readonly StyledProperty<ICommand?> SelectFixedCommandProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, ICommand?>(
            nameof(SelectFixedCommand)
        );

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<StimulationChannelAllocationItem, object?>(
            nameof(CommandParameter)
        );

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? RoleText
    {
        get => GetValue(RoleTextProperty);
        set => SetValue(RoleTextProperty, value);
    }

    public double Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    public double MinimumCurrent
    {
        get => GetValue(MinimumCurrentProperty);
        set => SetValue(MinimumCurrentProperty, value);
    }

    public double MaximumCurrent
    {
        get => GetValue(MaximumCurrentProperty);
        set => SetValue(MaximumCurrentProperty, value);
    }

    public double CurrentIncrement
    {
        get => GetValue(CurrentIncrementProperty);
        set => SetValue(CurrentIncrementProperty, value);
    }

    public bool IsChannelEnabled
    {
        get => GetValue(IsChannelEnabledProperty);
        set => SetValue(IsChannelEnabledProperty, value);
    }

    public bool CanToggle
    {
        get => GetValue(CanToggleProperty);
        set => SetValue(CanToggleProperty, value);
    }

    public bool CanEditCurrent
    {
        get => GetValue(CanEditCurrentProperty);
        set => SetValue(CanEditCurrentProperty, value);
    }

    public bool IsFixedCandidate
    {
        get => GetValue(IsFixedCandidateProperty);
        set => SetValue(IsFixedCandidateProperty, value);
    }

    public bool IsSelectedFixed
    {
        get => GetValue(IsSelectedFixedProperty);
        set => SetValue(IsSelectedFixedProperty, value);
    }

    public ICommand? SelectFixedCommand
    {
        get => GetValue(SelectFixedCommandProperty);
        set => SetValue(SelectFixedCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
