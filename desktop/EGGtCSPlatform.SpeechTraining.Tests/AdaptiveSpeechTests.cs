using System.Collections.Immutable;
using System.Text.Json;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class AdaptiveSpeechTests
{
    [Fact]
    public void ResearchReferenceSequencesMatchIncludingReversalBoundaries()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "AdaptiveSpeechReference.json")
            )
        );
        foreach (var sequence in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var state = new AdaptiveSpeechState();
            var i = 0;
            foreach (var row in sequence.GetProperty("rows").EnumerateArray())
            {
                var passed = row.GetProperty("passed").GetBoolean();
                var record = state.Submit($"MSP/{++i}", i, new(7, 4), passed ? 4 : 3, 7);
                Assert.Equal(row.GetProperty("r").GetDouble(), record.R, 12);
                Assert.Equal(row.GetProperty("multiplier").GetDouble(), record.Multiplier, 12);
                Assert.Equal(row.GetProperty("reversals").GetInt32(), record.Reversals);
                Assert.Equal(row.GetProperty("factor").GetDouble(), record.Factor);
                Assert.Equal(row.GetProperty("nextR").GetDouble(), state.R, 12);
                Assert.Equal(passed, state.LastPassed);
            }
            Assert.Equal(i, state.Submissions.Length);
            Assert.Throws<InvalidOperationException>(() =>
                state.Submit("MSP/1", i + 1, new(7, 4), 4, 7)
            );
            Assert.Equal(i, state.Submissions.Length);
        }
    }

    [Theory]
    [InlineData("MSP", 3, 7, false)]
    [InlineData("MSP", 4, 7, true)]
    [InlineData("MHINT", 7, 10, false)]
    [InlineData("MHINT", 8, 10, true)]
    [InlineData("MHINT-P", 7, 10, false)]
    [InlineData("MHINT-P", 8, 10, true)]
    public void CorpusBoundaries(string corpus, int correct, int total, bool passed) =>
        Assert.Equal(
            passed,
            new FileSpeechCorpusProvider()
                .Corpora.Single(c => c.Id == corpus)
                .AdaptiveScoring!.IsPassed(correct, total)
        );

    [Theory]
    [InlineData(7, 4, 4, 10)]
    [InlineData(10, 8, 7, 7)]
    [InlineData(10, 8, 8, 11)]
    [InlineData(7, 4, -1, 7)]
    [InlineData(7, 4, 8, 7)]
    public void InvalidScoringLeavesStateUnchanged(
        int expected,
        int minimum,
        int correct,
        int total
    )
    {
        var state = new AdaptiveSpeechState();
        Assert.ThrowsAny<ArgumentException>(() =>
            state.Submit("s", 1, new(expected, minimum), correct, total)
        );
        Assert.Empty(state.Submissions);
        Assert.Equal(0.5, state.R);
        Assert.Null(state.LastPassed);
    }

    [Theory]
    [InlineData(16001, 1, 1.5)]
    [InlineData(16003, 2, 1.75)]
    [InlineData(1001, 2, 2)]
    [InlineData(16001, 1, 100)]
    public void MultiplierUsesOriginalFramesAndUnroundedRates(
        int frames,
        int channels,
        double multiplier
    )
    {
        var audio = new AudioBuffer(16000, channels, new byte[frames * channels * 2]);
        var prepared = TestAudio.Prepare(
            audio,
            new(Mode: SpeechTrainingMode.AdaptiveRate),
            Sentence(1),
            multiplier
        );
        var timing = Assert.IsType<SpeechTimeStretchMetadata>(prepared.TimeStretch);
        var expected = (int)Math.Round(frames / multiplier, MidpointRounding.AwayFromZero);
        Assert.Equal(expected * channels * 2, prepared.Playback.Data.Length);
        Assert.Same(prepared.Playback, prepared.EnvelopeSource.Audio);
        Assert.Equal(7d * 16000 / frames * multiplier, timing.TargetSpeechRate, 10);
        Assert.Equal(7d * 16000 / expected, timing.ActualSpeechRate, 10);
        Assert.Equal(frames, timing.OriginalFrames);
        Assert.Equal(expected, timing.OutputFrames);
    }

    [Fact]
    public void InvalidResolvedMultiplierAndAudioNeverFallBack()
    {
        foreach (
            var multiplier in new[]
            {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                0,
                -1,
                double.MaxValue,
                double.Epsilon,
            }
        )
            Assert.ThrowsAny<ArgumentException>(() =>
                TestAudio.Prepare(
                    Audio(),
                    new(Mode: SpeechTrainingMode.AdaptiveRate),
                    multiplier: multiplier
                )
            );
        Assert.Throws<ArgumentException>(() =>
            TestAudio.Prepare(new(16000, 1, []), new(Mode: SpeechTrainingMode.AdaptiveRate))
        );
        Assert.Throws<ArgumentException>(() =>
            new TrainingAudioSettings(SpeechRate: 1.5m).Validate()
        );
    }

    [Fact]
    public void VocoderTailDoesNotChangeTimeStretchMetadataOrReference()
    {
        var prepared = TestAudio.Prepare(
            Audio(16003),
            new(
                Mode: SpeechTrainingMode.AdaptiveRate,
                Vocoder: new(VocoderAlgorithm.Ace, 1, VocoderCarrier.Tone, 1)
            ),
            Sentence(1),
            1.5
        );
        Assert.Equal(
            new[]
            {
                "solafs-1",
                "sinc-resample-1",
                "sinc-resample-1",
                "acoustic-vocoder-1",
                "pcm16-double-decode-1",
                "channel-mix-1",
                "hilbert-analytic-1",
                "complex-magnitude-1",
            },
            prepared.Graph.Records.Select(r => r.ProcessorIdentity)
        );
        Assert.Equal(10669, prepared.TimeStretch!.OutputFrames);
        Assert.Equal(7d / (16003d / 16000) * 1.5, prepared.TimeStretch.TargetSpeechRate, 12);
        Assert.True(prepared.Playback.Duration > prepared.EnvelopeSource.Audio.Duration);
        Assert.Equal(
            prepared.TimeStretch.OutputFrames * 2,
            prepared.EnvelopeSource.Audio.Data.Length
        );
    }

    [Fact]
    public async Task EarlySaveRetryReplayAndPendingCarrierUseOneStaircaseStep()
    {
        var provider = new Provider(3);
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(provider, player);
        session.SetSpeechRate(3);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.False(session.CanAdjustSpeechRate);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(3, session.SpeechRate);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await session.StartAsync();
        Assert.Equal(1.5, session.CurrentTiming!.Multiplier);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(SpeechTrainingMode.AdaptiveRate, session.Mode);
        session.RateAll(true);
        session.RateAll(null);
        Assert.Empty(session.AdaptiveSubmissions);
        session.RateAll(true);
        provider.FailSentence = 2;
        await session.SaveNextAsync(); // interrupts playback; next load fails
        Assert.Equal(0.75, session.AdaptiveR);
        Assert.Single(session.AdaptiveSubmissions);
        Assert.NotNull(session.Error);
        Assert.Null(session.CurrentTiming);
        await session.SaveNextAsync();
        Assert.Single(session.AdaptiveSubmissions);
        provider.FailSentence = null;
        await session.ReplayAsync();
        Assert.Equal(1.75, session.CurrentTiming!.Multiplier);
        player.Complete();
        var wave = session.Waveform;
        await session.ReplayAsync();
        Assert.Same(wave, session.Waveform);
        player.Complete();
        session.SetVocoder(new(Bands: 1, Maxima: 1)); // pending, not heard
        Assert.Equal(0.75, session.AdaptiveR);
        session.RateAll(false);
        await session.SaveNextAsync();
        await session.SaveNextAsync();
        Assert.Equal(2, session.AdaptiveSubmissions.Length);
        Assert.Equal(0.5, session.AdaptiveR);
        session.End();
        Assert.False(session.Result!.Sentences[0].PlaybackCompleted);
        Assert.True(session.Result.Sentences[1].PlaybackCompleted);
        Assert.Null(session.Result.Sentences[1].AudioSnapshot!.Settings.Vocoder);
        Assert.Equal("测试未完成", session.Result.AdaptiveSummary!.Status);
        Assert.False(session.Result.AdaptiveSummary.MixedCarrier); // manual carrier edits are locked for the round
    }

    [Theory]
    [InlineData(7, 7, "样本不足", false)]
    [InlineData(10, 8, "测试未完成", false)]
    [InlineData(8, 8, "测试完成", true)]
    [InlineData(10, 10, "测试完成", true)]
    public async Task SummaryUsesOnlySavedUnroundedTargets(
        int total,
        int saved,
        string status,
        bool hasValue
    )
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new Provider(total), player);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await session.StartAsync();
        for (var i = 0; i < saved; i++)
        {
            player.Complete();
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
        }
        session.End();
        var result = session.Result!;
        Assert.Equal(status, result.AdaptiveSummary!.Status);
        Assert.Equal(hasValue, result.AdaptiveSummary.MeanTargetRate.HasValue);
        Assert.False(result.AdaptiveSummary.MixedCarrier);
        if (hasValue)
            Assert.Equal(
                result.Sentences.TakeLast(8).Average(s => s.Adaptive!.Timing.TargetSpeechRate),
                result.AdaptiveSummary.MeanTargetRate
            );
    }

    [Fact]
    public async Task MixedCarrierTracksReplayAndSeedButNotPendingSettingsOrFailedStart()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new Provider(1), player);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        session.SetVocoder(new(Bands: 1, Maxima: 1));
        await session.StartAsync();
        player.Complete();
        session.SetVocoder(new(Bands: 1, Maxima: 1, Seed: 23));
        player.FailStart = true;
        await session.ReplayAsync();
        player.FailStart = false;
        await session.ReplayAsync();
        player.Complete();
        session.SetVocoder(null);
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.False(session.Result!.AdaptiveSummary!.MixedCarrier);
        Assert.Equal(2, session.Result.Playbacks.Length);
        Assert.Equal(
            new VocoderOptions().Seed,
            session.Result.Sentences[0].AudioSnapshot!.Settings.Vocoder!.Seed
        );
        Assert.Single(session.AdaptiveSubmissions);
    }

    [Fact]
    public async Task PendingCarrierAloneDoesNotMarkMixedAndBadCorpusCanBeCorrectedBeforeStart()
    {
        var player = new ControllableSpeechAudioPlayer();
        using var bad = new SpeechTrainingSession(
            new Provider(1, "custom", withoutRule: true),
            player
        );
        bad.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await bad.StartAsync();
        Assert.False(bad.Started);
        Assert.Contains("缺少自适应评分规则", bad.Error);
        bad.SelectMode(SpeechTrainingMode.FixedRate);
        await bad.StartAsync();
        Assert.True(bad.Started);

        var player2 = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(new Provider(1), player2);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await session.StartAsync();
        player2.Complete();
        session.SetVocoder(new());
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.False(session.Result!.AdaptiveSummary!.MixedCarrier);
    }

    [Fact]
    public async Task PartialFinalListCompletesWithoutReversalStop()
    {
        using var session = new SpeechTrainingSession(
            new Provider(27),
            new ControllableSpeechAudioPlayer(),
            20
        );
        session.SelectSentenceList("02");
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        await session.StartAsync();
        for (var i = 0; i < 7; i++)
        {
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
        }
        Assert.True(session.Ended);
        Assert.Equal(6, session.AdaptiveReversals);
        Assert.Equal("MSP/27", session.Result!.Sentences[^1].SentenceId);
        Assert.Equal("样本不足", session.Result.AdaptiveSummary!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledAdaptivePreparationCannotOverwriteOrAdvance(bool dispose)
    {
        var preparation = new DelayedPreparation();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new Provider(2),
            player,
            preparation: preparation
        );
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        var first = session.StartAsync();
        try
        {
            await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Stop();
            Assert.True(preparation.Token.IsCancellationRequested);
            if (dispose)
                session.Dispose();
            else
                await session.ReplayAsync();
            var waveform = session.Waveform;
            preparation.Release.TrySetResult();
            await first;
            Assert.Same(waveform, session.Waveform);
            Assert.Equal(dispose ? 0 : 1, player.PlayCount);
            Assert.Equal(0.5, session.AdaptiveR);
            Assert.Empty(session.AdaptiveSubmissions);
            if (!dispose)
            {
                var oldId = player.PlaybackId;
                session.RateAll(true);
                await session.SaveNextAsync();
                player.Emit(oldId, AudioPlaybackState.Completed, TimeSpan.FromSeconds(99));
                Assert.True(session.IsPlaying);
                Assert.Equal(0.75, session.AdaptiveR);
                Assert.Single(session.AdaptiveSubmissions);
            }
        }
        finally
        {
            preparation.Release.TrySetResult();
        }
    }

    private sealed class DelayedPreparation : TestPreparationBase
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        )
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Token = token;
                Entered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return Service.Prepare(request); // deliberately ignores cancellation
        }
    }

    private static SpeechSentence Sentence(int n) =>
        new($"MSP/{n}", n, "一二三四五六七", $"{n}.wav");

    private static AudioBuffer Audio(int frames = 3201) => new(16000, 1, new byte[frames * 2]);

    private sealed class Provider(int count, string corpus = "MSP", bool withoutRule = false)
        : ISpeechCorpusProvider
    {
        public IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new(
                corpus,
                Enumerable.Range(1, count).Select(Sentence).ToArray(),
                withoutRule ? null : new(7, 4)
            ),
        ];
        public int? FailSentence { get; set; }

        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            sentence.Number == FailSentence
                ? throw new IOException("test load failed")
                : Audio(3201 + sentence.Number * 103);
    }
}
