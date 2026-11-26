namespace EGGtCSPlatform.ViewModels.Pages;

public sealed record ElectrodePositionDefinition(
    string Id,
    string Position,
    double ReferenceX,
    double ReferenceY,
    ElectrodeRole DefaultRole = ElectrodeRole.None,
    ElectrodeVector3? Position3D = null,
    ElectrodeVector3? Normal3D = null
);

// Anatomical coordinates: millimetres, +X wearer right, +Y anterior, +Z superior.
public sealed record ElectrodeVector3(double X = double.NaN, double Y = double.NaN, double Z = double.NaN)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
    public bool IsDirection => IsFinite && double.IsFinite(Length) && Length > 1e-9;
}
