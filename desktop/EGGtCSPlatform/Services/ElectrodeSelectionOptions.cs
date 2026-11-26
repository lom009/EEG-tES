namespace EGGtCSPlatform.Services;

public sealed class ElectrodeSelectionOptions
{
    public const string SectionName = "ElectrodeSelection";

    public bool AllowSharedStimulationAcquisitionSite { get; set; } = true;
}
