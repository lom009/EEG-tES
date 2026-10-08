using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

/// <summary>Compatibility values for the shared configuration format; never sent to stimulation hardware.</summary>
public static class AcquisitionOnlyConfiguration
{
    public static StimulusConfigurationSnapshot StimulusPlaceholder { get; } =
        new(
            StimulusKind.TDcs,
            StimulusArrayMode.DualChannel,
            StimulusDirection.Positive,
            ShamWaveformMode.Direct,
            0,
            0,
            0,
            []
        );

    public static ExperimentTimingTemplate NormalizeTiming(ExperimentTimingTemplate timing) =>
        timing with
        {
            StimulationMilliseconds = 0,
            RecoveryMilliseconds = 0,
        };

    public static ExperimentConfigurationTemplate Normalize(
        ExperimentConfigurationTemplate template
    ) =>
        template.CreationMode == ExperimentCreationMode.AcquisitionOnly
            ? template with
            {
                StimulusConfiguration = StimulusPlaceholder,
                StimulusElectrodes = [],
                Timing = NormalizeTiming(template.Timing),
            }
            : template;
}
