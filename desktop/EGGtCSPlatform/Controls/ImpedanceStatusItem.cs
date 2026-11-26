using Avalonia.Input;
using Avalonia.Interactivity;
using System.Windows.Input;
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Controls;

public class ImpedanceStatusItem : TemplatedControl
{
    public static readonly StyledProperty<ICommand?> PointCommandProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, ICommand?>(nameof(PointCommand));
    public ICommand? PointCommand { get => GetValue(PointCommandProperty); set => SetValue(PointCommandProperty, value); }
    public static readonly StyledProperty<object?> PointCommandParameterProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, object?>(nameof(PointCommandParameter));
    public object? PointCommandParameter { get => GetValue(PointCommandParameterProperty); set => SetValue(PointCommandParameterProperty, value); }
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, bool>(nameof(IsSelected));
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    public ImpedanceStatusItem()
    {
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && PointCommand?.CanExecute(PointCommandParameter) == true)
                PointCommand.Execute(PointCommandParameter);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private ComboBox? _channelSelector;
    private bool _isSynchronizingChannelSelector;

    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<
        ImpedanceStatusItem,
        string?
    >(nameof(Label));

    public static readonly StyledProperty<string?> ImpedanceTextProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, string?>(nameof(ImpedanceText));

    public static readonly StyledProperty<string?> RoleTextProperty = AvaloniaProperty.Register<
        ImpedanceStatusItem,
        string?
    >(nameof(RoleText));

    public static readonly StyledProperty<string?> QualityTextProperty = AvaloniaProperty.Register<
        ImpedanceStatusItem,
        string?
    >(nameof(QualityText));

    public static readonly StyledProperty<ImpedanceQuality> QualityProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, ImpedanceQuality>(nameof(Quality));

    public static readonly StyledProperty<ResolvedElectrodeRole?> ResolvedStimulationRoleProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, ResolvedElectrodeRole?>(
            nameof(ResolvedStimulationRole)
        );

    public static readonly StyledProperty<IEnumerable?> StimulationChannelsProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, IEnumerable?>(nameof(StimulationChannels));

    public static readonly StyledProperty<StimulationChannelOptionViewModel?> SelectedStimulationChannelProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, StimulationChannelOptionViewModel?>(
            nameof(SelectedStimulationChannel),
            defaultBindingMode: BindingMode.TwoWay
        );

    public static readonly StyledProperty<bool> ShowStimulationChannelProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, bool>(nameof(ShowStimulationChannel));

    public static readonly StyledProperty<bool> HasChannelConflictProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, bool>(nameof(HasChannelConflict));

    public static readonly StyledProperty<string?> ChannelConflictTextProperty =
        AvaloniaProperty.Register<ImpedanceStatusItem, string?>(nameof(ChannelConflictText));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? ImpedanceText
    {
        get => GetValue(ImpedanceTextProperty);
        set => SetValue(ImpedanceTextProperty, value);
    }

    public string? RoleText
    {
        get => GetValue(RoleTextProperty);
        set => SetValue(RoleTextProperty, value);
    }

    public string? QualityText
    {
        get => GetValue(QualityTextProperty);
        set => SetValue(QualityTextProperty, value);
    }

    public ImpedanceQuality Quality
    {
        get => GetValue(QualityProperty);
        set => SetValue(QualityProperty, value);
    }

    public ResolvedElectrodeRole? ResolvedStimulationRole
    {
        get => GetValue(ResolvedStimulationRoleProperty);
        set => SetValue(ResolvedStimulationRoleProperty, value);
    }

    public IEnumerable? StimulationChannels
    {
        get => GetValue(StimulationChannelsProperty);
        set => SetValue(StimulationChannelsProperty, value);
    }

    public StimulationChannelOptionViewModel? SelectedStimulationChannel
    {
        get => GetValue(SelectedStimulationChannelProperty);
        set => SetValue(SelectedStimulationChannelProperty, value);
    }

    public bool ShowStimulationChannel
    {
        get => GetValue(ShowStimulationChannelProperty);
        set => SetValue(ShowStimulationChannelProperty, value);
    }

    public bool HasChannelConflict
    {
        get => GetValue(HasChannelConflictProperty);
        set => SetValue(HasChannelConflictProperty, value);
    }

    public string? ChannelConflictText
    {
        get => GetValue(ChannelConflictTextProperty);
        set => SetValue(ChannelConflictTextProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_channelSelector is not null)
            _channelSelector.SelectionChanged -= OnChannelSelectionChanged;

        base.OnApplyTemplate(e);
        _channelSelector = e.NameScope.Find<ComboBox>("StimulationChannelSelector");
        SynchronizeChannelSelector();
        if (_channelSelector is not null)
            _channelSelector.SelectionChanged += OnChannelSelectionChanged;
    }

    private void SynchronizeChannelSelector()
    {
        if (_channelSelector is null || _isSynchronizingChannelSelector)
            return;

        _isSynchronizingChannelSelector = true;
        try
        {
            // Loading/unloading items can temporarily clear the ComboBox selection.
            // Restore it only after the candidates are loaded, without writing back.
            _channelSelector.ItemsSource = StimulationChannels;
            _channelSelector.SelectedItem = SelectedStimulationChannel;
        }
        finally
        {
            _isSynchronizingChannelSelector = false;
        }
    }

    private void OnChannelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A null selection during teardown is not a request to clear the mapping.
        if (!_isSynchronizingChannelSelector
            && _channelSelector?.SelectedItem is StimulationChannelOptionViewModel channel)
            SetCurrentValue(SelectedStimulationChannelProperty, channel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StimulationChannelsProperty
            || change.Property == SelectedStimulationChannelProperty)
            SynchronizeChannelSelector();
        if (
            change.Property == QualityProperty
            || change.Property == ResolvedStimulationRoleProperty
            || change.Property == HasChannelConflictProperty
        )
            UpdatePseudoClasses();
        if (change.Property == IsSelectedProperty)
            PseudoClasses.Set(":selected", IsSelected);
    }

    private void UpdatePseudoClasses()
    {
        PseudoClasses.Set(":excellent", Quality == ImpedanceQuality.Excellent);
        PseudoClasses.Set(":good", Quality == ImpedanceQuality.Good);
        PseudoClasses.Set(":medium", Quality == ImpedanceQuality.Medium);
        PseudoClasses.Set(":poor", Quality == ImpedanceQuality.Poor);
        PseudoClasses.Set(":bad", Quality == ImpedanceQuality.Bad);
        PseudoClasses.Set(
            ":blue-stimulus",
            ResolvedStimulationRole is { } blueRole && ElectrodeRoleDisplay.IsAnode(blueRole)
        );
        PseudoClasses.Set(
            ":red-stimulus",
            ResolvedStimulationRole is { } redRole && ElectrodeRoleDisplay.IsCathode(redRole)
        );
        PseudoClasses.Set(":channel-conflict", HasChannelConflict);
    }
}
