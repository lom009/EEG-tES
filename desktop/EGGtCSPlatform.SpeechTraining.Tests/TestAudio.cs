using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.SpeechTraining.Tests;

internal static class TestAudio
{
    internal sealed class Corpus(AudioBuffer? audio = null) : ISpeechCorpusProvider
    {
        public IReadOnlyList<SpeechCorpus> Corpora => [];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => audio ?? AdaptiveSnrTests.Tone();
    }

    public static TrainingAudioPreparationResult Prepare(
        AudioBuffer audio,
        TrainingAudioSettings? settings = null,
        SpeechSentence? sentence = null,
        double multiplier = 1,
        SpeechNoiseSettings? noise = null,
        ISpeechNoiseProvider? provider = null,
        IRirGenerator? generator = null,
        double calibration = .05
    )
    {
        if (
            noise is not null
            && settings?.Mode is SpeechTrainingMode.FixedSnr or SpeechTrainingMode.AdaptiveSnr
        )
            settings = settings with { NoiseKind = noise.Kind, SnrDb = noise.SnrDb };
        var service = new TrainingAudioPreparationService(new Corpus(audio), provider, generator);
        return service.Prepare(
            new(
                sentence ?? new("test", 1, "今天好。", ""),
                settings ?? new(),
                audio,
                multiplier,
                noise?.Seed ?? 1729,
                noise,
                noise is null ? null : provider!.Read(noise.FileName),
                noise?.CalibrationRms ?? calibration
            )
        );
    }

    public static SpeechWaveformData Display(AudioBuffer audio) =>
        SpeechWaveformData.Prepare(Prepare(audio));
}

internal abstract class TestPreparationBase(ISpeechNoiseProvider? noise = null)
    : ITrainingAudioPreparationService
{
    protected TrainingAudioPreparationService Service { get; } =
        new(new TestAudio.Corpus(), noise ?? new AdaptiveSnrTests.NoiseProvider());

    public TrainingAudioRequest CreateRequest(
        SpeechSentence sentence,
        TrainingAudioSettings settings,
        double multiplier,
        int seed,
        SpeechNoiseMapping? mapping,
        CancellationToken token = default
    ) => Service.CreateRequest(sentence, settings, multiplier, seed, mapping, token);

    public abstract TrainingAudioPreparationResult Prepare(
        TrainingAudioRequest request,
        TrainingAudioPreparationResult? cached = null,
        CancellationToken token = default
    );
}
