namespace EGGtCSPlatform.ViewModels.Pages;

public sealed record ElectrodePositionDefinition(
    string Id,
    string Position,
    double ReferenceX,
    double ReferenceY,
    ElectrodeRole DefaultRole = ElectrodeRole.None
);
