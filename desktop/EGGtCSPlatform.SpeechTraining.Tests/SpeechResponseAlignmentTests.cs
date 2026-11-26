using EGGtCSPlatform.SpeechTraining;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechResponseAlignmentTests
{
    private static readonly Dictionary<string, string[]> CharacterReadings = new()
    {
        ["每"] = ["měi"],
        ["美"] = ["měi"],
        ["妹"] = ["mèi"],
        ["洗"] = ["xǐ", "xiǎn"],
        ["喜"] = ["xǐ"],
        ["长"] = ["cháng", "zhǎng"],
        ["掌"] = ["zhǎng"],
        ["行"] = ["háng", "xíng"],
        ["航"] = ["háng"],
        ["苹"] = ["píng"],
        ["平"] = ["píng"],
        ["果"] = ["guǒ"],
        ["鱼"] = ["yú"],
        ["于"] = ["yú"],
        ["雨"] = ["yǔ"],
    };

    private static readonly Dictionary<string, string[][]> PhraseReadings = new()
    {
        ["长跑"] = [["cháng"], ["pǎo"]],
        ["银行"] = [["yín"], ["háng"]],
        ["行走"] = [["xíng"], ["zǒu"]],
    };

    [Theory]
    [InlineData("这些衣服洗过吗？", "这些衣服洗过吗。", "1111111")]
    [InlineData("这些衣服洗过吗？", "这些衣服没洗过", "1111000")]
    [InlineData("这些衣服洗过吗？", "这些衣服过洗吗", "1111001")]
    [InlineData("这些衣服洗过吗？", "这些衣服喜过吗", "1111?11")]
    [InlineData("他家每年放鞭炮", "他家美年放鞭炮", "1111111")]
    [InlineData("他家每年放鞭炮", "他家妹年放鞭炮", "1101111")]
    [InlineData("表哥想训练长跑", "表哥想训练掌跑", "1111101")]
    [InlineData("去银行取钱", "去银航取钱", "11111")]
    [InlineData("在路上行走", "在路上航走", "11101")]
    [InlineData("这句话不对", "这句话不对", "11111")]
    public void ScoresOnlyCharactersAtTheSamePosition(string expected, string heard, string expectedPattern)
    {
        var ratings = Compare(expected, heard, out var reason);
        Assert.Null(reason);
        Assert.Equal(expectedPattern, string.Concat(ratings.Select(rating => rating switch
        {
            true => '1',
            false => '0',
            null => '?',
        })));
    }

    [Theory]
    [InlineData("苹果", "苹果", true)]
    [InlineData("苹果", "苹果苹果", true)]
    [InlineData("苹果", "嗯，是苹果吧", true)]
    [InlineData("苹果", "平果", true)]
    [InlineData("鱼", "于", true)]
    [InlineData("鱼", "雨", false)]
    [InlineData("苹果", "香蕉", false)]
    [InlineData("苹果", "果", false)]
    [InlineData("银行", "银航", true)]
    public void FindsTheTargetAnywhereInTheAnswer(string target, string heard, bool expected)
    {
        var match = SpeechResponseAlignment.FindWithin(
            SpeechSentence.GetScoringCharacters(target), SpeechSentence.GetScoringCharacters(heard),
            CharacterReadings, PhraseReadings, out var reason);
        Assert.Null(reason);
        Assert.Equal(expected, match);
    }

    [Fact]
    public void TargetSearchLeavesSelfCorrectionAndAmbiguousReadingsToTheExaminer()
    {
        Assert.Null(SpeechResponseAlignment.FindWithin(
            SpeechSentence.GetScoringCharacters("香蕉"), SpeechSentence.GetScoringCharacters("苹果，不对，香蕉"),
            CharacterReadings, PhraseReadings, out var reason));
        Assert.Equal(SpeechResponseReviewReason.SelfCorrection, reason);
        Assert.Null(SpeechResponseAlignment.FindWithin(
            SpeechSentence.GetScoringCharacters("喜"), SpeechSentence.GetScoringCharacters("洗"),
            CharacterReadings, PhraseReadings, out reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("嗯，他家每年放鞭炮", SpeechResponseReviewReason.LengthMismatch)]
    [InlineData("他家每年都放鞭炮", SpeechResponseReviewReason.LengthMismatch)]
    [InlineData("他家每年放炮", SpeechResponseReviewReason.LengthMismatch)]
    [InlineData("他家每年放鞭炮，对吧", SpeechResponseReviewReason.LengthMismatch)]
    [InlineData("他家每月，不对，他家每年放鞭炮", SpeechResponseReviewReason.SelfCorrection)]
    [InlineData("他家说错了鞭炮", SpeechResponseReviewReason.SelfCorrection)]
    public void RequiresReviewWhenTheTranscriptCannotBeScoredByPosition(
        string heard, SpeechResponseReviewReason expectedReason
    )
    {
        var ratings = Compare("他家每年放鞭炮", heard, out var reason);
        Assert.Equal(expectedReason, reason);
        Assert.Equal(7, ratings.Length);
        Assert.All(ratings, rating => Assert.Null(rating));
    }

    private static bool?[] Compare(string expected, string heard, out SpeechResponseReviewReason? reason) =>
        SpeechResponseAlignment.Compare(
            SpeechSentence.GetScoringCharacters(expected),
            SpeechSentence.GetScoringCharacters(heard),
            CharacterReadings, PhraseReadings, out reason
        );
}
