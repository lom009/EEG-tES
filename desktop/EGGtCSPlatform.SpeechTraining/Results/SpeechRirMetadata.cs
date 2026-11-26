using EGGtCSPlatform.Audio.Dsp.Reverb;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record TrainingRirSnapshot(
    TrainingRoomSettings Settings,
    double CalibrationRms,
    SpeechNoiseSettings? Noise = null,
    string AlgorithmVersion = RirSettings.AlgorithmVersion
);

public sealed record SpeechRirMetadata(
    TrainingRirSnapshot Configuration,
    string FullHash,
    string EffectiveHash,
    int DirectFrame,
    int DivisionFrame,
    int RirFrames,
    int OutputFrames,
    double EarlyCoefficientRms,
    double CalibrationGain,
    double PeakGain,
    double EarlySpeechRms,
    double NoiseGain,
    SpeechNoiseMetadata? Noise = null
)
{
    public int SampleRate => RirSettings.SampleRate;
}
