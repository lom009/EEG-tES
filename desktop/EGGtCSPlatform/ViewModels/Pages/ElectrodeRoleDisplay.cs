namespace EGGtCSPlatform.ViewModels.Pages;

// Presentation only: never use these labels to determine physical channel roles.
public static class ElectrodeRoleDisplay
{
    public static string Label(ResolvedElectrodeRole role) =>
        IsAnode(role) ? "阳极" : "阴极";

    public static string Token(ResolvedElectrodeRole role) => IsAnode(role) ? "A" : "C";

    public static string Label(StimulusDirection direction, StimulationChannelRole role)
    {
        var resolved = StimulationElectrodeRoleResolver.Resolve(direction);
        return Label(role == StimulationChannelRole.FixedActive ? resolved.FixedActive : resolved.Selectable);
    }

    public static bool IsAnode(ResolvedElectrodeRole role) =>
        role is ResolvedElectrodeRole.Anode or ResolvedElectrodeRole.Stimulating;

    public static bool IsCathode(ResolvedElectrodeRole role) =>
        role is ResolvedElectrodeRole.Cathode or ResolvedElectrodeRole.FixedActive;
}
