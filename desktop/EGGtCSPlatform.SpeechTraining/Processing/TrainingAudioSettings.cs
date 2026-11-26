using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.Vocoder;

namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Business choices only. A preset owns ordering and routing.</summary>
public sealed record TrainingAudioSettings(
    SpeechTrainingMode Mode = SpeechTrainingMode.FixedRate,
    decimal SpeechRate = 0,
    VocoderOptions? Vocoder = null,
    SpeechNoiseKind NoiseKind = SpeechNoiseKind.Ssn,
    double SnrDb = 10,
    TrainingRoomSettings? Room = null,
    EnvelopeOptions? Envelope = null
)
{
    public EnvelopeOptions Envelope { get; init; } = Envelope ?? new();

    public string CarrierSummary => Vocoder?.Summary ?? "原始语音";

    public void Validate()
    {
        if (
            !Enum.IsDefined(Mode)
            || !Enum.IsDefined(NoiseKind)
            || SpeechRate is < 0 or > 5
            || SpeechRate != decimal.Truncate(SpeechRate)
            || !double.IsFinite(SnrDb)
        )
            throw new ArgumentException("训练音频设置无效。");
        Vocoder?.Validate();
        Envelope.Validate();
        if (Mode == SpeechTrainingMode.FixedRir)
            (Room ?? TrainingRoomSettings.Default).Validate();
    }
}

/// <summary>Room acoustics use the reusable DSP model; noise selection belongs to training.</summary>
public sealed record TrainingRoomSettings(
    RirSettings Acoustics,
    bool AddNoise = false,
    SpeechNoiseKind NoiseKind = SpeechNoiseKind.Ssn,
    double SnrDb = 10
)
{
    public static TrainingRoomSettings Default { get; } = new(RirSettings.Default);

    public void Validate()
    {
        Acoustics.Validate();
        if (!Enum.IsDefined(NoiseKind) || !double.IsFinite(SnrDb))
            throw new ArgumentException("RIR 噪声选择无效。");
    }
}
