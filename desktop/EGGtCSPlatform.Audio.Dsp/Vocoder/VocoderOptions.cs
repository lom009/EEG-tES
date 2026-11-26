namespace EGGtCSPlatform.Audio.Dsp.Vocoder;

public enum VocoderAlgorithm
{
    ClassicalCis,
    Ace,
    ClassicalTle,
    AceTle,
}

public enum VocoderCarrier
{
    Noise,
    Tone,
}

public sealed record VocoderOptions(
    VocoderAlgorithm Algorithm = VocoderAlgorithm.ClassicalCis,
    int Bands = 8,
    VocoderCarrier Carrier = VocoderCarrier.Noise,
    int Maxima = 8,
    int Seed = 1729
)
{
    public bool UsesMaxima => Algorithm is VocoderAlgorithm.Ace or VocoderAlgorithm.AceTle;
    public string Summary =>
        $"{Algorithm switch { VocoderAlgorithm.ClassicalCis => "经典 CIS", VocoderAlgorithm.Ace => "ACE", VocoderAlgorithm.ClassicalTle => "经典 TLE", _ => "ACE-TLE" }} · {Bands} 频带 · {(Carrier == VocoderCarrier.Noise ? "噪声" : "纯音")}"
        + (UsesMaxima ? $" · {Maxima} 激活" : "");

    public void Validate()
    {
        if (
            !Enum.IsDefined(Algorithm)
            || !Enum.IsDefined(Carrier)
            || Bands is < 1 or > 22
            || Maxima < 1
            || Maxima > Bands
        )
            throw new ArgumentException(
                "声码器参数无效：频带数为 1～22，最大激活通道数为 1～频带数。"
            );
    }
}
