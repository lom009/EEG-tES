using System.Collections.Immutable;

namespace EGGtCSPlatform.SpeechTraining;

public enum SpeechTrainingMode
{
    FixedRate,
    AdaptiveRate,
    AdaptiveSnr,
    FixedSnr,
    FixedRir,
}

public sealed record AdaptiveSpeechSubmission(
    string SentenceId,
    int Number,
    double R,
    bool Passed,
    int Reversals,
    double Factor,
    double NextR
)
{
    public double Multiplier => 1 + R;
}

/// <summary>Research staircase. Only accepted, sequential submissions change its state.</summary>
public sealed class AdaptiveSpeechState
{
    public double R { get; private set; } = 0.5;
    public bool? LastPassed { get; private set; }
    public int Reversals { get; private set; }
    public ImmutableArray<AdaptiveSpeechSubmission> Submissions { get; private set; } = [];

    public AdaptiveSpeechSubmission Submit(
        string sentenceId,
        int number,
        AdaptiveScoringRule rule,
        int correct,
        int total
    )
    {
        var submission = Preview(sentenceId, number, rule, correct, total);
        Submissions = Submissions.Add(submission);
        R = submission.NextR;
        LastPassed = submission.Passed;
        Reversals = submission.Reversals;
        return submission;
    }

    internal AdaptiveSpeechSubmission Preview(
        string sentenceId,
        int number,
        AdaptiveScoringRule rule,
        int correct,
        int total
    )
    {
        if (string.IsNullOrWhiteSpace(sentenceId))
            throw new ArgumentException("提交需要句子标识。", nameof(sentenceId));
        if (number != Submissions.Length + 1 || Submissions.Any(s => s.SentenceId == sentenceId))
            throw new InvalidOperationException("自适应评分必须按句序且每句只提交一次。");
        ArgumentNullException.ThrowIfNull(rule);
        var passed = rule.IsPassed(correct, total);
        var reversals = Reversals + (LastPassed.HasValue && LastPassed != passed ? 1 : 0);
        var factor =
            reversals < 2 ? 1.5
            : reversals < 4 ? 1.26
            : 1.1;
        var nextR = passed ? R * factor : R / factor;
        if (!double.IsFinite(nextR) || nextR <= 0 || !double.IsFinite(1 + nextR))
            throw new ArgumentOutOfRangeException(nameof(nextR), "自适应 R 超出有效数值范围。");
        return new(sentenceId, number, R, passed, reversals, factor, nextR);
    }
}

public sealed record AdaptiveSpeechRecord(
    AdaptiveSpeechSubmission Submission,
    SpeechTimeStretchMetadata Timing
);

public sealed record SpeechPlaybackRecord(
    long PlaybackId,
    string SentenceId,
    TrainingAudioSnapshot AudioSnapshot,
    SpeechNoiseMetadata? Noise = null,
    SpeechRirMetadata? Rir = null
);

public sealed record AdaptiveSpeechSummary(string Status, double? MeanTargetRate, bool MixedCarrier)
{
    public string Label => MixedCarrier ? "末 8 句平均语速（描述性）" : "自适应语速阈值（SRT）";

    public static AdaptiveSpeechSummary Create(
        int sentenceCount,
        IReadOnlyList<SpeechSentenceResult> sentences,
        IEnumerable<SpeechPlaybackRecord> playbacks
    )
    {
        var mixed = playbacks
            .Select(p => p.AudioSnapshot.Settings.Vocoder)
            .Distinct()
            .Skip(1)
            .Any();
        if (sentences.Count != sentenceCount)
            return new("测试未完成", null, mixed);
        if (sentences.Count < 8)
            return new("样本不足", null, mixed);
        var last = sentences.TakeLast(8).Select(s => s.Adaptive?.Timing.TargetSpeechRate).ToArray();
        if (last.Any(r => r is null || !double.IsFinite(r.Value)))
            return new("自适应记录不完整", null, mixed);
        return new("测试完成", last.Average(r => r!.Value), mixed);
    }
}
