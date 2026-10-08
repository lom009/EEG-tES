using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Controls;

public class ExperimentConfigurationDialog : TemplatedControl
{
    public static readonly StyledProperty<EnvelopeConfirmationData?> EnvelopeConfirmationProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, EnvelopeConfirmationData?>(
            nameof(EnvelopeConfirmation));

    public EnvelopeConfirmationData? EnvelopeConfirmation
    {
        get => GetValue(EnvelopeConfirmationProperty);
        set => SetValue(EnvelopeConfirmationProperty, value);
    }

    public static readonly StyledProperty<bool> ShowStimulationProperty = AvaloniaProperty.Register<
        ExperimentConfigurationDialog,
        bool
    >(nameof(ShowStimulation), true);
    public bool ShowStimulation
    {
        get => GetValue(ShowStimulationProperty);
        set => SetValue(ShowStimulationProperty, value);
    }
    public static readonly StyledProperty<bool> ShowAcquisitionProperty = AvaloniaProperty.Register<
        ExperimentConfigurationDialog,
        bool
    >(nameof(ShowAcquisition), true);
    public bool ShowAcquisition
    {
        get => GetValue(ShowAcquisitionProperty);
        set => SetValue(ShowAcquisitionProperty, value);
    }

    public static readonly StyledProperty<string?> SubjectIdProperty = AvaloniaProperty.Register<
        ExperimentConfigurationDialog,
        string?
    >(nameof(SubjectId));

    public static readonly StyledProperty<string?> ExperimentIdProperty = AvaloniaProperty.Register<
        ExperimentConfigurationDialog,
        string?
    >(nameof(ExperimentId));

    public static readonly StyledProperty<string?> StimulusSummaryProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, string?>(nameof(StimulusSummary));

    public static readonly StyledProperty<string?> ParameterSummaryProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, string?>(nameof(ParameterSummary));

    public static readonly StyledProperty<string?> StimulusElectrodesProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, string?>(
            nameof(StimulusElectrodes)
        );

    public static readonly StyledProperty<string?> AcquisitionElectrodesProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, string?>(
            nameof(AcquisitionElectrodes)
        );

    public static readonly StyledProperty<string?> SafetySummaryProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, string?>(nameof(SafetySummary));

    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, ICommand?>(nameof(CloseCommand));

    public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
        AvaloniaProperty.Register<ExperimentConfigurationDialog, ICommand?>(nameof(ConfirmCommand));

    public string? SubjectId
    {
        get => GetValue(SubjectIdProperty);
        set => SetValue(SubjectIdProperty, value);
    }

    public string? ExperimentId
    {
        get => GetValue(ExperimentIdProperty);
        set => SetValue(ExperimentIdProperty, value);
    }

    public string? StimulusSummary
    {
        get => GetValue(StimulusSummaryProperty);
        set => SetValue(StimulusSummaryProperty, value);
    }

    public string? ParameterSummary
    {
        get => GetValue(ParameterSummaryProperty);
        set => SetValue(ParameterSummaryProperty, value);
    }

    public string? StimulusElectrodes
    {
        get => GetValue(StimulusElectrodesProperty);
        set => SetValue(StimulusElectrodesProperty, value);
    }

    public string? AcquisitionElectrodes
    {
        get => GetValue(AcquisitionElectrodesProperty);
        set => SetValue(AcquisitionElectrodesProperty, value);
    }

    public string? SafetySummary
    {
        get => GetValue(SafetySummaryProperty);
        set => SetValue(SafetySummaryProperty, value);
    }

    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public ICommand? ConfirmCommand
    {
        get => GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }
}
