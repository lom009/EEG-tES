namespace EGGtCSPlatform.MainApp;

/// <summary>Prepared configuration and observed impedances; no stimulation run has been created.</summary>
public sealed record EnvelopeStimulationRouteData(
    ExperimentRunRouteData Configuration,
    string DetectionStatus
);
