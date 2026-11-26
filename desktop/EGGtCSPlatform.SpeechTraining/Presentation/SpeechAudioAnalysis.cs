namespace EGGtCSPlatform.SpeechTraining;

/// <summary>Full-resolution analysis independent of display buckets or stimulus rendering.</summary>
public sealed record SpeechAudioAnalysis(
    TrainingAudioPreparationResult Preparation,
    double[] Envelope
)
{
    public static SpeechAudioAnalysis Prepare(
        TrainingAudioPreparationResult audio,
        CancellationToken token = default
    )
    {
        audio.Validate();
        token.ThrowIfCancellationRequested();
        return new(audio, audio.Envelope.ToArray());
    }
}
