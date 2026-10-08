using System;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public sealed record ElectrodeConfigurationRouteData(
    string ExperimentId,
    string SubjectId,
    StimulusConfigurationSnapshot StimulusConfiguration,
    string DeviceId = "simulator-default",
    long ExperimentDatabaseId = 0,
    DateTimeOffset? ScheduledAt = null,
    string Remarks = "",
    ExperimentConfigurationTemplate? ImportedTemplate = null,
    ExperimentCreationMode CreationMode = ExperimentCreationMode.AcquisitionAndStimulation
);

public static class ElectrodeConfigurationRouteDataDefaults
{
    public static ElectrodeConfigurationRouteData Create() =>
        CreateFor(StimulusArrayMode.DualChannel);

    public static ElectrodeConfigurationRouteData CreateHd() => CreateFor(StimulusArrayMode.Hd);

    private static ElectrodeConfigurationRouteData CreateFor(StimulusArrayMode arrayMode)
    {
        var targetId = Guid.NewGuid();
        return new ElectrodeConfigurationRouteData(
            arrayMode == StimulusArrayMode.DualChannel ? "未填写" : "EXP-PREVIEW-001",
            arrayMode == StimulusArrayMode.DualChannel ? "未填写" : "SUBJECT-PREVIEW-001",
            new StimulusConfigurationSnapshot(
                StimulusKind.TDcs,
                arrayMode,
                StimulusDirection.Positive,
                ShamWaveformMode.Direct,
                7,
                40,
                79,
                [new StimulusTargetSnapshot(targetId, 1, 2d, null, [])]
            )
        );
    }
}
