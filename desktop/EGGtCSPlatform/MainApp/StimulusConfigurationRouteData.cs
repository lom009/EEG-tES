using System;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public sealed record StimulusConfigurationRouteData(
    string ExperimentId,
    string SubjectId,
    long ExperimentDatabaseId = 0,
    DateTimeOffset? ScheduledAt = null,
    string Remarks = "",
    ExperimentCreationMode CreationMode = ExperimentCreationMode.AcquisitionAndStimulation
);
