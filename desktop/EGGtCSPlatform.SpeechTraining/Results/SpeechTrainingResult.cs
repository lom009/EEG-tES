using System.Collections.Immutable;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record SpeechTrainingSettings(
    string Corpus,
    string SentenceList,
    string Mode = "固定语速测试(安静)",
    string Carrier = "原始语音",
    decimal SpeechRate = 0,
    int ListSize = 20,
    int ResourceStart = 1,
    int ResourceEnd = 0,
    TrainingAudioSettings? AudioSettings = null,
    SpeechTrainingMode TrainingMode = SpeechTrainingMode.FixedRate,
    AdaptiveScoringRule? AdaptiveScoring = null,
    double? InitialSnrDb = null,
    double? FixedSnrDb = null,
    TrainingRoomSettings? Rir = null
);

public sealed record SpeechSentenceResult(
    string SentenceId,
    int Number,
    string Text,
    int Correct,
    int Total,
    decimal? SpeechRate = null,
    TrainingAudioSnapshot? AudioSnapshot = null,
    AdaptiveSpeechRecord? Adaptive = null,
    bool? PlaybackCompleted = null,
    AdaptiveSnrRecord? AdaptiveSnr = null,
    SpeechNoiseMetadata? Noise = null,
    SpeechRirMetadata? Rir = null
);

public sealed record SpeechTrainingResult(
    SpeechTrainingSettings Settings,
    DateTimeOffset StartedAt,
    int SentenceCount,
    IReadOnlyList<SpeechSentenceResult> Sentences,
    AdaptiveSpeechSummary? AdaptiveSummary = null,
    System.Collections.Immutable.ImmutableArray<SpeechPlaybackRecord> Playbacks = default,
    AdaptiveSnrSummary? SnrSummary = null
)
{
    public int CorrectCount => Sentences.Sum(x => x.Correct);
    public int RatedCount => Sentences.Sum(x => x.Total);
    public double? Accuracy => RatedCount == 0 ? null : (double)CorrectCount / RatedCount;
}
