using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public partial class ExperimentConfigurationDialogViewModel(
    string subjectId,
    string experimentId,
    string stimulusSummary,
    string parameterSummary,
    string stimulusElectrodes,
    string acquisitionElectrodes,
    string safetySummary,
    bool showAcquisition = true,
    bool showStimulation = true,
    EnvelopeConfirmationData? envelopeConfirmation = null
) : DialogViewModel
{
    public EnvelopeConfirmationData? EnvelopeConfirmation { get; } = envelopeConfirmation;

    public bool ShowStimulation { get; } = showStimulation;
    public bool ShowAcquisition { get; } = showAcquisition;

    public string SubjectId { get; } = subjectId;

    public string ExperimentId { get; } = experimentId;

    public string StimulusSummary { get; } = stimulusSummary;

    public string ParameterSummary { get; } = parameterSummary;

    public string StimulusElectrodes { get; } = stimulusElectrodes;

    public string AcquisitionElectrodes { get; } = acquisitionElectrodes;

    public string SafetySummary { get; } = safetySummary;

    [ObservableProperty]
    private bool _confirmed;

    protected override void OnOverlayDismiss() => Cancel();

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        Close();
    }
}

public sealed record EnvelopeConfirmationData(
    string MaximumCurrent,
    string StartupDelay,
    string FixedChannel,
    string SelectableChannel
);
