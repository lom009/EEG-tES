using EGGtCSPlatform.Audio.Core.Buffers;

namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Frozen before DSP starts: no execution step reads mutable session state.</summary>
public sealed record TrainingAudioRequest(
    SpeechSentence Sentence,
    TrainingAudioSettings Settings,
    AudioBuffer Source,
    double SpeedMultiplier,
    int Seed,
    SpeechNoiseSettings? Noise,
    SpeechNoiseResource? NoiseResource,
    double CalibrationRms,
    string? SourceFileSha256 = null,
    TrainingResourceSnapshot? CalibrationResource = null
)
{
    public void Validate()
    {
        Settings.Validate();
        ArgumentNullException.ThrowIfNull(Sentence);
        ArgumentNullException.ThrowIfNull(Source);
        var room = Settings.Room ?? TrainingRoomSettings.Default;
        bool needsNoise =
            Settings.Mode is SpeechTrainingMode.FixedSnr or SpeechTrainingMode.AdaptiveSnr
            || Settings.Mode == SpeechTrainingMode.FixedRir && room.AddNoise;
        if (needsNoise != (Noise is not null) || needsNoise != (NoiseResource is not null))
            throw new ArgumentException("噪声资源元数据与训练模式不一致。");
        if (
            (needsNoise || Settings.Mode == SpeechTrainingMode.FixedRir)
            && (!double.IsFinite(CalibrationRms) || CalibrationRms <= 0)
        )
            throw new ArgumentException("校准 RMS 无效。");
        if (Noise is { } n)
        {
            n.Validate();
            if (n.Sha256 != NoiseResource!.Sha256)
                throw new InvalidDataException("噪声资源与请求快照不一致。");
            if (
                n.Seed != Seed
                || n.CalibrationRms != CalibrationRms
                || n.Kind
                    != (
                        Settings.Mode == SpeechTrainingMode.FixedRir
                            ? room.NoiseKind
                            : Settings.NoiseKind
                    )
                || n.SnrDb
                    != (Settings.Mode == SpeechTrainingMode.FixedRir ? room.SnrDb : Settings.SnrDb)
            )
                throw new ArgumentException("噪声元数据与训练设置不一致。");
        }
        if (
            Settings.Mode == SpeechTrainingMode.AdaptiveRate
            && (!double.IsFinite(SpeedMultiplier) || SpeedMultiplier <= 0)
        )
            throw new ArgumentException("自适应倍率必须为有限正数。");
    }
}

public interface ITrainingAudioPreparationService
{
    TrainingAudioRequest CreateRequest(
        SpeechSentence sentence,
        TrainingAudioSettings settings,
        double speedMultiplier,
        int seed,
        SpeechNoiseMapping? noiseMapping,
        CancellationToken token = default
    );

    TrainingAudioPreparationResult Prepare(
        TrainingAudioRequest request,
        TrainingAudioPreparationResult? cached = null,
        CancellationToken token = default
    );
}
