using System.Collections.Generic;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public sealed record ToleranceTestContext(string SubjectId, string Remarks);

public sealed record ToleranceTestRouteData(
    ToleranceTestContext Context,
    StimulusConfigurationSnapshot Configuration,
    IReadOnlyList<StimulusElectrodeAssignment> Electrodes,
    IReadOnlyList<ImpedanceSnapshotValue> Impedances
);
