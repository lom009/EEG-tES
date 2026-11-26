namespace EGGtCSPlatform.SpeechTraining;

/// <summary>An immutable, corpus-supplied rule. No corpus names imply a scoring threshold.</summary>
public sealed record AdaptiveScoringRule
{
    public int ExpectedCharacterCount { get; }
    public int MinimumCorrect { get; }

    public AdaptiveScoringRule(int expectedCharacterCount, int minimumCorrect)
    {
        if (expectedCharacterCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedCharacterCount),
                "每句字数必须是正整数。"
            );
        if (minimumCorrect <= 0 || minimumCorrect > expectedCharacterCount)
            throw new ArgumentOutOfRangeException(
                nameof(minimumCorrect),
                "至少答对字数必须在 1 到每句字数之间。"
            );
        ExpectedCharacterCount = expectedCharacterCount;
        MinimumCorrect = minimumCorrect;
    }

    public string Description =>
        $"每句 {ExpectedCharacterCount} 字，至少答对 {MinimumCorrect} 字通过";

    public void ValidateSentence(int total)
    {
        if (total != ExpectedCharacterCount)
            throw new ArgumentException(
                $"评分规则要求每句 {ExpectedCharacterCount} 字，实际为 {total} 字。"
            );
    }

    public bool IsPassed(int correct, int total)
    {
        ValidateSentence(total);
        if (correct < 0 || correct > total)
            throw new ArgumentOutOfRangeException(nameof(correct));
        return correct >= MinimumCorrect;
    }
}
