using System.Collections.Immutable;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record AdaptiveSnrSubmission(
    string SentenceId,
    int Number,
    double SnrDb,
    bool Passed,
    int Reversals,
    double StepDb,
    double NextSnrDb
);

public sealed class AdaptiveSnrState
{
    public AdaptiveSnrState(double initialSnrDb = 10)
    {
        if (!double.IsFinite(initialSnrDb) || initialSnrDb is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(initialSnrDb));
        SnrDb = initialSnrDb;
    }

    public double SnrDb { get; private set; }
    public bool? LastPassed { get; private set; }
    public int Reversals { get; private set; }
    public ImmutableArray<AdaptiveSnrSubmission> Submissions { get; private set; } = [];

    internal AdaptiveSnrSubmission Preview(
        string id,
        int number,
        AdaptiveScoringRule rule,
        int correct,
        int total
    )
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("缺少句子标识。", nameof(id));
        if (number != Submissions.Length + 1 || Submissions.Any(s => s.SentenceId == id))
            throw new InvalidOperationException("自适应评分必须按句序且每句只提交一次。");
        var passed = rule.IsPassed(correct, total);
        var reversals = Reversals + (LastPassed.HasValue && LastPassed != passed ? 1 : 0);
        var step =
            reversals < 2 ? 8
            : reversals < 4 ? 4
            : 2;
        return new(
            id,
            number,
            SnrDb,
            passed,
            reversals,
            step,
            passed ? SnrDb - step : Math.Min(20, SnrDb + step)
        );
    }

    public AdaptiveSnrSubmission Submit(
        string id,
        int number,
        AdaptiveScoringRule rule,
        int correct,
        int total
    )
    {
        var result = Preview(id, number, rule, correct, total);
        Submissions = Submissions.Add(result);
        SnrDb = result.NextSnrDb;
        LastPassed = result.Passed;
        Reversals = result.Reversals;
        return result;
    }
}

public sealed record AdaptiveSnrRecord(AdaptiveSnrSubmission Submission, SpeechNoiseMetadata Noise);

public sealed record AdaptiveSnrSummary(string Status, double? MeanSnrDb, bool MixedCarrier)
{
    public string Label => MixedCarrier ? "末 8 句平均 SNR（描述性）" : "自适应信噪比阈值（SRT）";

    public static AdaptiveSnrSummary Create(
        int count,
        IReadOnlyList<SpeechSentenceResult> sentences,
        IEnumerable<SpeechPlaybackRecord> playbacks
    )
    {
        var mixed = playbacks
            .Select(p => p.AudioSnapshot.Settings.Vocoder)
            .Distinct()
            .Skip(1)
            .Any();
        if (sentences.Count != count)
            return new("测试未完成", null, mixed);
        if (sentences.Count < 8)
            return new("样本不足", null, mixed);
        var values = sentences.TakeLast(8).Select(s => s.AdaptiveSnr?.Submission.SnrDb).ToArray();
        if (values.Any(v => v is null || !double.IsFinite(v.Value)))
            return new("自适应记录不完整", null, mixed);
        return new("测试完成", values.Average(v => v!.Value), mixed);
    }
}
