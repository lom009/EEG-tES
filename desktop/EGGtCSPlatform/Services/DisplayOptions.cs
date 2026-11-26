namespace EGGtCSPlatform.Services;

public enum FontSizePreset
{
    Small,
    Standard,
    Large,
    ExtraLarge,
}

public sealed class DisplayOptions
{
    public const string SectionName = "Display";
    public const double DefaultWaveformStrokeThickness = 2.5d;
    public const double MinimumWaveformStrokeThickness = 0.1d;
    public const double MaximumWaveformStrokeThickness = 10d;

    public bool EnvelopePlayheadEnabled { get; set; } = false;

    public bool EnvelopeWaveformRealtimeEnabled { get; set; } = true;

    public FontSizePreset FontSizePreset { get; set; } = FontSizePreset.Standard;

    public double WaveformStrokeThickness { get; set; } = DefaultWaveformStrokeThickness;

    public static double NormalizeWaveformStrokeThickness(double value) =>
        double.IsFinite(value)
        && value >= MinimumWaveformStrokeThickness
        && value <= MaximumWaveformStrokeThickness
            ? value
            : DefaultWaveformStrokeThickness;
}
