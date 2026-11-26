using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public class ElectrodePoint : TemplatedControl
{
    public ElectrodePoint()
    {
        AddHandler(
            PointerPressedEvent,
            OnPreviewPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    public static readonly StyledProperty<ElectrodeSiteViewModel?> SiteProperty =
        AvaloniaProperty.Register<ElectrodePoint, ElectrodeSiteViewModel?>(nameof(Site));

    public ElectrodeSiteViewModel? Site
    {
        get => GetValue(SiteProperty);
        set => SetValue(SiteProperty, value);
    }

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        string?
    >(nameof(Label));

    public static readonly StyledProperty<string?> ToolTipTextProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        string?
    >(nameof(ToolTipText));

    public static readonly StyledProperty<ElectrodeRole> RoleProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        ElectrodeRole
    >(nameof(Role));

    public static readonly StyledProperty<StimulationChannelRole?> StimulationChannelRoleProperty =
        AvaloniaProperty.Register<ElectrodePoint, StimulationChannelRole?>(
            nameof(StimulationChannelRole)
        );

    public static readonly StyledProperty<ResolvedElectrodeRole?> ResolvedStimulationRoleProperty =
        AvaloniaProperty.Register<ElectrodePoint, ResolvedElectrodeRole?>(
            nameof(ResolvedStimulationRole)
        );

    public static readonly StyledProperty<bool> IsStimulusProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        bool
    >(nameof(IsStimulus));

    public static readonly StyledProperty<ImpedanceQuality> QualityProperty =
        AvaloniaProperty.Register<ElectrodePoint, ImpedanceQuality>(nameof(Quality));

    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        bool
    >(nameof(IsSelected));

    public static readonly StyledProperty<bool> IsDimmedProperty =
        AvaloniaProperty.Register<ElectrodePoint, bool>(nameof(IsDimmed));

    public bool IsDimmed
    {
        get => GetValue(IsDimmedProperty);
        set => SetValue(IsDimmedProperty, value);
    }

    public static readonly StyledProperty<bool> IsAvailableProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        bool
    >(nameof(IsAvailable), true);

    public static readonly StyledProperty<bool> IsBackViewProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        bool
    >(nameof(IsBackView));

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<
        ElectrodePoint,
        ICommand?
    >(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<ElectrodePoint, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<ICommand?> DoubleClickCommandProperty =
        AvaloniaProperty.Register<ElectrodePoint, ICommand?>(nameof(DoubleClickCommand));

    public static readonly StyledProperty<object?> DoubleClickCommandParameterProperty =
        AvaloniaProperty.Register<ElectrodePoint, object?>(nameof(DoubleClickCommandParameter));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? ToolTipText
    {
        get => GetValue(ToolTipTextProperty);
        set => SetValue(ToolTipTextProperty, value);
    }

    public ElectrodeRole Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public StimulationChannelRole? StimulationChannelRole
    {
        get => GetValue(StimulationChannelRoleProperty);
        set => SetValue(StimulationChannelRoleProperty, value);
    }

    public ResolvedElectrodeRole? ResolvedStimulationRole
    {
        get => GetValue(ResolvedStimulationRoleProperty);
        set => SetValue(ResolvedStimulationRoleProperty, value);
    }

    public bool IsStimulus
    {
        get => GetValue(IsStimulusProperty);
        set => SetValue(IsStimulusProperty, value);
    }

    public ImpedanceQuality Quality
    {
        get => GetValue(QualityProperty);
        set => SetValue(QualityProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool IsAvailable
    {
        get => GetValue(IsAvailableProperty);
        set => SetValue(IsAvailableProperty, value);
    }

    public bool IsBackView
    {
        get => GetValue(IsBackViewProperty);
        set => SetValue(IsBackViewProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public ICommand? DoubleClickCommand
    {
        get => GetValue(DoubleClickCommandProperty);
        set => SetValue(DoubleClickCommandProperty, value);
    }

    public object? DoubleClickCommandParameter
    {
        get => GetValue(DoubleClickCommandParameterProperty);
        set => SetValue(DoubleClickCommandParameterProperty, value);
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryHandleDoubleClick(e.ClickCount))
            e.Handled = true;
    }

    internal bool TryHandleDoubleClick(int clickCount)
    {
        if (clickCount != 2)
            return false;

        var command = DoubleClickCommand;
        var parameter = DoubleClickCommandParameter;
        if (command?.CanExecute(parameter) != true)
            return false;

        command.Execute(parameter);
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == LabelProperty
            || change.Property == RoleProperty
            || change.Property == StimulationChannelRoleProperty
            || change.Property == ResolvedStimulationRoleProperty
            || change.Property == IsStimulusProperty
            || change.Property == QualityProperty
            || change.Property == IsSelectedProperty
            || change.Property == IsDimmedProperty
            || change.Property == IsAvailableProperty
            || change.Property == IsBackViewProperty
        )
            UpdatePseudoClasses();
    }

    private void UpdatePseudoClasses()
    {
        PseudoClasses.Set(":shared", !IsDimmed && IsStimulus && Role != ElectrodeRole.None);
        var displayRole = IsDimmed ? ElectrodeRole.None : Role;
        var displayQuality = IsDimmed ? ImpedanceQuality.Unknown : Quality;
        PseudoClasses.Set(":assigned", !IsDimmed && (IsStimulus || Role is ElectrodeRole.Acquisition or ElectrodeRole.Reference or ElectrodeRole.Ground));
        PseudoClasses.Set(":multiline", Label?.Contains('\n') == true);
        PseudoClasses.Set(":stimulus", !IsDimmed && IsStimulus);
        PseudoClasses.Set(
            ":fixed-active",
            StimulationChannelRole
                == global::EGGtCSPlatform.ViewModels.Pages.StimulationChannelRole.FixedActive
        );
        PseudoClasses.Set(
            ":selectable",
            StimulationChannelRole
                == global::EGGtCSPlatform.ViewModels.Pages.StimulationChannelRole.Selectable
        );
        PseudoClasses.Set(
            ":blue-stimulus",
            ResolvedStimulationRole is { } blueRole && ElectrodeRoleDisplay.IsAnode(blueRole)
        );
        PseudoClasses.Set(
            ":red-stimulus",
            ResolvedStimulationRole is { } redRole && ElectrodeRoleDisplay.IsCathode(redRole)
        );
        PseudoClasses.Set(":acquisition", displayRole == ElectrodeRole.Acquisition);
        PseudoClasses.Set(":reference", displayRole == ElectrodeRole.Reference);
        PseudoClasses.Set(":ground", displayRole == ElectrodeRole.Ground);
        PseudoClasses.Set(":excellent", displayQuality == ImpedanceQuality.Excellent);
        PseudoClasses.Set(":good", displayQuality == ImpedanceQuality.Good);
        PseudoClasses.Set(":medium", displayQuality == ImpedanceQuality.Medium);
        PseudoClasses.Set(":poor", displayQuality == ImpedanceQuality.Poor);
        PseudoClasses.Set(":bad", displayQuality == ImpedanceQuality.Bad);
        PseudoClasses.Set(":selected", !IsDimmed && IsSelected);
        PseudoClasses.Set(":unavailable", !IsAvailable);
        PseudoClasses.Set(":back", IsBackView);
    }
}
