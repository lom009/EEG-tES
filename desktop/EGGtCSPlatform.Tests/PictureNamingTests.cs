using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class PictureNamingTests
{
    [Fact]
    public void SetupUsesConfirmedConfigurationAndRejectsCombinedMode()
    {
        var route = SingleStimulusExperimentTests.Route();
        using var page = new PictureNamingPageViewModel(route);
        Assert.Contains(route.SubjectId, page.HeaderBadges.Select(x => x.Value));
        Assert.Contains(route.ExperimentId, page.HeaderBadges.Select(x => x.Value));
        Assert.Equal("2.00 mA", page.CurrentText);
        Assert.Contains("Fz · CH1", page.Electrodes);
        Assert.Throws<ArgumentException>(() => new PictureNamingPageViewModel(ExperimentRunRouteDataDefaults.Create()));
    }

    [Theory]
    [InlineData(0, 0, "apple")]
    [InlineData(1, 0, "scissors")]
    [InlineData(1, 1, null)]
    public async Task MaterialsTimingScoringAndRestart(int material, int order, string? first)
    {
        using var page = new PictureNamingPageViewModel(SingleStimulusExperimentTests.Route())
        { RampSeconds = 0, MaterialIndex = material, OrderIndex = order, FixationSeconds = 0, AnswerSeconds = 0, IntervalMs = 0 };
        Assert.True(page.CanConfigure);
        var seen = new HashSet<string>();
        for (int i = 0; i < 6; i++)
        {
            await page.AdvanceCommand.ExecuteAsync(null);
            Assert.True(seen.Add(page.PicturePath));
            if (first != null && i == 0) Assert.EndsWith(first + ".svg", page.PicturePath);
            Assert.False(page.CanConfigure);
            Assert.Equal("核对评分", page.Status);
            Assert.False(page.CanAct);
            page.ScoreCommand.Execute("错误");
            page.ScoreCommand.Execute("重置");
            Assert.False(page.CanAdvance);
            page.ScoreCommand.Execute("错误");
            page.ScoreCommand.Execute(i < 4 ? "正确" : i == 4 ? "错误" : "无反应");
            Assert.Equal(i + 1, page.Scored);
        }
        Assert.False(page.CanConfigure);
        Assert.Equal("已完成", page.Status);
        Assert.Equal((4, 1, 1), (page.Correct, page.Wrong, page.Unanswered));
        Assert.Equal((4d / 6).ToString("P0"), page.Accuracy);
        await page.AdvanceCommand.ExecuteAsync(null);
        Assert.Equal("0/6", page.CountText);
        var completed = Assert.Single(page.Rounds);
        Assert.True(completed.Complete);
        Assert.Equal(6, completed.Scored);
        Assert.Equal(4, completed.Rows.Count(row => row.Score == "正确"));
        page.EndCommand.Execute(null);
        Assert.True(page.IsDetail);
        Assert.Equal(2, page.Rounds.Count);
        Assert.False(page.SelectedRound!.Complete);
        Assert.Equal(0, page.SelectedRound.Scored);
    }

    [Fact]
    public async Task EndDuringFixationCancelsAnswerAndScoring()
    {
        using var page = new PictureNamingPageViewModel(SingleStimulusExperimentTests.Route()) { RampSeconds = 0, FixationSeconds = 4 };
        var pending = page.AdvanceCommand.ExecuteAsync(null);
        page.ScoreCommand.Execute("正确");
        Assert.Equal(0, page.Scored);
        page.EndCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(page.IsDetail);
        Assert.False(page.CanAct);
        Assert.False(page.CanScore);
        Assert.False(Assert.Single(page.Rounds).Complete);
        page.EndCommand.Execute(null);
        Assert.Single(page.Rounds);
    }

    [Fact]
    public async Task EndingAtLastPictureExportsConfirmedRowsAndMissingReaction()
    {
        using var page = new PictureNamingPageViewModel(SingleStimulusExperimentTests.Route())
        { RampSeconds = 0, FixationSeconds = 0, AnswerSeconds = 0, IntervalMs = 0 };
        for (int n = 0; n < 6; n++)
        {
            await page.AdvanceCommand.ExecuteAsync(null);
            page.ScoreCommand.Execute(n == 1 ? "无反应" : "正确");
        }
        page.EndCommand.Execute(null);
        using var stream = new System.IO.MemoryStream();
        await page.WriteResultsAsync(stream);
        stream.Position = 0;
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
        var rounds = doc.RootElement.GetProperty("Rounds");
        Assert.Equal(1, rounds.GetArrayLength());
        Assert.True(rounds[0].GetProperty("Complete").GetBoolean());
        var rows = rounds[0].GetProperty("Rows");
        Assert.Equal(6, rows.GetArrayLength());
        Assert.Equal("苹果", rows[0].GetProperty("Target").GetString());
        Assert.Equal("无反应", rows[1].GetProperty("Score").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[1].GetProperty("ReactionMs").ValueKind);
        Assert.Equal("鱼", rows[5].GetProperty("Target").GetString());
    }

    [Fact]
    public void ReactionEstimateRequiresSustainedSound()
    {
        var pcm = new byte[16000];
        EGGtCSPlatform.Audio.Core.Buffers.AudioBuffer Audio() => new(16000, 1, pcm);
        Assert.Null(PictureNamingPageViewModel.EstimateOnset(Audio()));
        pcm[101] = 32;
        Assert.Null(PictureNamingPageViewModel.EstimateOnset(Audio()));
        for (int i = 5120; i < 6400; i++) pcm[i * 2 + 1] = 4;
        Assert.Equal(320, PictureNamingPageViewModel.EstimateOnset(Audio()));
    }

    [Fact]
    public async Task RampRunsBeforeFirstFixation()
    {
        using var page = new PictureNamingPageViewModel(SingleStimulusExperimentTests.Route()) { RampSeconds = 0.2, FixationSeconds = 4 };
        Assert.Equal("刺激缓升", page.RampTitle);
        Assert.Equal("缓升时长", page.RampLabel);
        var pending = page.AdvanceCommand.ExecuteAsync(null);
        Assert.Equal("刺激缓升中", page.Status);
        Assert.Equal("剩余时间", page.RampLabel);
        Assert.Equal("1", page.RampText);
        Assert.True(page.ShowRampCard);
        Assert.False(page.CanConfigure);
        for (int n = 0; n < 50 && !page.IsFixation; n++) await Task.Delay(20);
        Assert.True(page.IsFixation);
        Assert.Equal("刺激缓升已结束", page.RampTitle);
        page.EndCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LeavingDuringFixationCancelsPendingPicture()
    {
        using var page = new PictureNamingPageViewModel(SingleStimulusExperimentTests.Route()) { RampSeconds = 0, FixationSeconds = 4 };
        var pending = page.AdvanceCommand.ExecuteAsync(null);
        Assert.Equal("注视点", page.Status);
        page.OnNavigatedFrom();
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(page.ShowPicture);
        Assert.False(page.CanAct);
    }

    private static readonly Dictionary<string, string[]> TestReadings = new()
    {
        ["苹"] = ["píng"], ["果"] = ["guǒ"], ["鱼"] = ["yú"], ["于"] = ["yú"], ["雨"] = ["yǔ"],
        ["香"] = ["xiāng"], ["蕉"] = ["jiāo"], ["杯"] = ["bēi"], ["子"] = ["zi"],
    };

    private static EGGtCSPlatform.Services.SpeechNamingSuggestion? Classify(string target, params (int, string)[] segments) =>
        EGGtCSPlatform.Services.LocalSpeechResponseRecognizer.ClassifyNaming(segments, target, TestReadings, new Dictionary<string, string[][]>());

    [Fact]
    public void NamingUsesFirstRealWordAndTimesTheCorrectWord()
    {
        var plain = Classify("苹果", (120, "苹果"))!;
        Assert.Equal(("正确", (int?)120, (int?)120, false), (plain.Outcome, plain.FirstOnsetMs, plain.CorrectOnsetMs, plain.Hesitated));

        var hesitant = Classify("苹果", (0, "问"), (740, "苹果"))!;
        Assert.Equal(("正确", (int?)740, (int?)740, true), (hesitant.Outcome, hesitant.FirstOnsetMs, hesitant.CorrectOnsetMs, hesitant.Hesitated));

        var corrected = Classify("苹果", (20, "香蕉"), (1340, "苹果"))!;
        Assert.Equal(("自我纠正", (int?)20, (int?)1340), (corrected.Outcome, corrected.FirstOnsetMs, corrected.CorrectOnsetMs));
        Assert.Equal("错误", PictureNamingPageViewModel.SuggestedScore(corrected));

        var joined = Classify("苹果", (0, "香蕉苹果"))!;
        Assert.Equal("待确认", joined.Outcome);
        Assert.Equal(0, joined.FirstOnsetMs);
        Assert.Null(joined.CorrectOnsetMs);
        Assert.Null(PictureNamingPageViewModel.SuggestedScore(joined));

        var fillerJoined = Classify("苹果", (60, "嗯苹果"))!;
        Assert.Equal("正确", fillerJoined.Outcome);
        Assert.True(fillerJoined.Hesitated);

        Assert.Equal("正确", Classify("鱼", (0, "于"))!.Outcome);
        Assert.Equal("错误", Classify("鱼", (0, "雨"))!.Outcome);
        var wrong = Classify("苹果", (0, ""), (300, "杯子"))!;
        Assert.Equal("错误", wrong.Outcome);
        Assert.Equal(300, wrong.FirstOnsetMs);

        var onlyFiller = Classify("苹果", (0, "嗯"), (600, "那个"))!;
        Assert.Null(onlyFiller.Outcome);
        Assert.Null(onlyFiller.FirstOnsetMs);
        Assert.True(onlyFiller.Hesitated);

        var marker = Classify("苹果", (0, "香蕉"), (500, "不对"), (900, "苹果"))!;
        Assert.Equal("待确认", marker.Outcome);
        Assert.Equal(EGGtCSPlatform.SpeechTraining.SpeechResponseReviewReason.SelfCorrection, marker.ReviewReason);
        Assert.Null(Classify("苹果", (0, "")));
    }

    [Fact]
    public void PauseSplitsAnswerIntoTimedSegments()
    {
        var pcm = new byte[16000 * 2 * 3 / 2];
        void Burst(int fromMs, int toMs)
        {
            for (int f = fromMs * 16; f < toMs * 16; f++)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 2), 1000);
        }
        Burst(100, 400);
        Burst(800, 1100);
        var parts = EGGtCSPlatform.Services.LocalSpeechResponseRecognizer.SplitSegments(new(16000, 1, pcm));
        Assert.Equal(new[] { (100, 400), (800, 1100) }, parts.Select(p => (p.StartMs, p.EndMs)).ToArray());

        Burst(400, 600);
        Assert.Single(EGGtCSPlatform.Services.LocalSpeechResponseRecognizer.SplitSegments(new(16000, 1, pcm)));
    }

    [Fact]
    public void MeanReactionCountsOnlyFirstTimeCorrectAnswers()
    {
        var round = new NamingRound("r", true,
        [
            new(1, "苹果", "正确", 600, "", Outcome: "正确"),
            new(2, "杯子", "正确", 800, "", CorrectOnsetMs: 1500, Outcome: "自我纠正"),
            new(3, "钥匙", "错误", 300, "", Outcome: "错误"),
            new(4, "雨伞", "正确", 1000, ""),
        ], 4, "", DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.Equal("800 ms", round.MeanReaction);
        Assert.Equal(1, round.ExcludedReactions);
        Assert.Equal("1500 ms", round.Rows[1].CorrectReaction);
    }

    [Theory]
    [InlineData(null, null, false, "—")]
    [InlineData("正确", null, false, "人工判分")]
    [InlineData("正确", "正确", true, "系统预选，未改动")]
    [InlineData("正确", "正确", false, "系统预选，人工确认")]
    [InlineData("错误", "正确", false, "人工改判（系统建议正确）")]
    [InlineData("无反应", "错误", false, "人工改判（系统建议错误）")]
    public void ScoreSourceSeparatesSystemAndExaminer(string? score, string? suggestion, bool untouched, string expected) =>
        Assert.Equal(expected, PictureNamingPageViewModel.ScoreSource(score, suggestion, untouched));
}
