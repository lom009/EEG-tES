using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class FixedSnrTests
{
    [Theory]
    [InlineData(-10, SpeechNoiseKind.Ssn)]
    [InlineData(0, SpeechNoiseKind.Ssn)]
    [InlineData(10, SpeechNoiseKind.Ssn)]
    [InlineData(30, SpeechNoiseKind.Ssn)]
    [InlineData(-10, SpeechNoiseKind.Babble)]
    [InlineData(0, SpeechNoiseKind.Babble)]
    [InlineData(10, SpeechNoiseKind.Babble)]
    [InlineData(30, SpeechNoiseKind.Babble)]
    public async Task FixedValueSurvivesWholeListAndUsesActualCharacterCounts(
        double db,
        SpeechNoiseKind kind
    )
    {
        var player = new ControllableSpeechAudioPlayer();
        using var session = Create(new Provider(), player);
        session.SetSpeechRate(4);
        session.SetInitialSnr(12);
        session.SetFixedSnr(db);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SelectNoise(kind);
        Assert.True(session.CanStart);
        Assert.False(session.CanUseAdaptiveMode);
        Assert.False(session.CanAdjustSpeechRate);
        Assert.Equal(0, session.SpeechRate);
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        Assert.Equal(12, session.CurrentSnrDb);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.Equal(1.5, 1 + session.AdaptiveR);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(4, session.SpeechRate);
        Assert.False(session.IsSnrMode);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        Assert.Equal(db, session.CurrentSnrDb);
        await session.StartAsync();
        Assert.Null(session.Error);
        session.SetFixedSnr(5);
        session.SetInitialSnr(3);
        session.SelectNoise(
            kind == SpeechNoiseKind.Ssn ? SpeechNoiseKind.Babble : SpeechNoiseKind.Ssn
        );
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(SpeechTrainingMode.FixedSnr, session.Mode);
        Assert.Equal(db, session.FixedSnrDb);
        Assert.Equal(12, session.InitialSnrDb);
        Assert.Equal(kind, session.NoiseKind);
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(session.Error);
            Assert.Equal(db, session.Waveform!.Preparation!.Noise!.Settings.SnrDb);
            if (i == 0)
            {
                var first = session.Audio;
                player.Complete();
                await session.ReplayAsync();
                Assert.Same(first, session.Audio);
            }
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
        }
        await session.SaveNextAsync();
        Assert.Empty(session.SnrSubmissions);
        Assert.Empty(session.AdaptiveSubmissions);
        Assert.Equal(0, session.SnrReversals);
        var result = session.Result!;
        Assert.Null(result.SnrSummary);
        Assert.Null(result.AdaptiveSummary);
        Assert.Null(result.Settings.InitialSnrDb);
        Assert.Null(result.Settings.AdaptiveScoring);
        Assert.Equal(db, result.Settings.FixedSnrDb);
        Assert.Equal(10, result.Sentences.Count);
        Assert.Equal(11, result.Playbacks.Length);
        Assert.Equal(35, result.RatedCount); // Five 3-character and five 4-character sentences.
        Assert.Equal(15, result.CorrectCount);
        Assert.Equal(15d / 35, result.Accuracy);
        Assert.All(
            result.Sentences,
            s =>
            {
                Assert.Null(s.AdaptiveSnr);
                Assert.Null(s.Adaptive);
                Assert.Equal(db, s.Noise!.Settings.SnrDb);
                Assert.Equal(kind, s.Noise.Settings.Kind);
            }
        );
        Assert.All(result.Playbacks, p => Assert.Equal(db, p.Noise!.Settings.SnrDb));
    }

    [Fact]
    public async Task FailedNextSentenceRetryAndCarrierReplayKeepFixedSnr()
    {
        var provider = new Provider { FailSentence = 2 };
        var player = new ControllableSpeechAudioPlayer();
        using var session = Create(provider, player);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SetFixedSnr(-5.25);
        await session.StartAsync();
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.NotNull(session.Error);
        Assert.False(session.CanSaveNext);
        provider.FailSentence = null;
        await session.ReplayAsync();
        var metadata = session.Waveform!.Preparation!.Noise;
        session.Stop();
        session.SetVocoder(new(Bands: 4, Maxima: 2));
        await session.ReplayAsync();
        Assert.Null(session.Error);
        Assert.Equal(metadata, session.Waveform!.Preparation!.Noise);
        player.Emit(player.PlaybackId, AudioPlaybackState.Failed, TimeSpan.Zero, "device failure");
        Assert.False(session.CanSaveNext);
        await session.ReplayAsync();
        Assert.Null(session.Error);
        session.End();
        Assert.Single(session.Result!.Sentences);
        Assert.Equal(3, session.Result.RatedCount);
        Assert.Equal(1, session.Result.Accuracy);
        Assert.Empty(session.SnrSubmissions);
        Assert.Equal(
            1,
            session
                .Result.Playbacks.Select(p => p.AudioSnapshot.Settings.Vocoder)
                .Distinct()
                .Count()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalePreparationAndCallbacksCannotReplaceNewPlayback(bool dispose)
    {
        var noise = new AdaptiveSnrTests.NoiseProvider();
        var prep = new DelayedPreparation(noise);
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new Provider(),
            player,
            preparation: prep,
            noiseProvider: noise
        );
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        var pending = session.StartAsync();
        try
        {
            await prep.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Stop();
            if (dispose)
                session.Dispose();
            else
                await session.ReplayAsync();
            var waveform = session.Waveform;
            prep.Release.TrySetResult();
            await pending;
            Assert.Same(waveform, session.Waveform);
            Assert.Equal(dispose ? 0 : 1, player.PlayCount);
            if (!dispose)
            {
                var oldId = player.PlaybackId;
                session.RateAll(true);
                await session.SaveNextAsync();
                player.Emit(oldId, AudioPlaybackState.Completed, TimeSpan.FromSeconds(40));
                Assert.True(session.IsPlaying);
                Assert.Equal(10, session.CurrentSnrDb);
            }
            Assert.Empty(session.SnrSubmissions);
        }
        finally
        {
            prep.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public async Task ExtremeFiniteValuesFailMixingWithoutSaving(double db)
    {
        using var session = Create(new Provider(), new ControllableSpeechAudioPlayer());
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SetFixedSnr(db);
        await session.StartAsync();
        Assert.NotNull(session.Error);
        Assert.False(session.CanScore);
        Assert.False(session.CanSaveNext);
        Assert.Equal(db, session.FixedSnrDb);
        session.End();
        Assert.Empty(session.Result!.Sentences);
        Assert.Empty(session.Result.Playbacks);
        Assert.Null(session.Result.Accuracy);
    }

    [Fact]
    public void NonFiniteValuesRejectedAndAdaptiveBoundsUnchanged()
    {
        using var session = Create(new Provider(), new ControllableSpeechAudioPlayer());
        foreach (var db in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFixedSnr(db));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetInitialSnr(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetInitialSnr(21));
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SelectCorpus("missing");
        Assert.False(session.CanStart);
        Assert.Contains("缺少", session.FixedSnrUnavailableReason);
    }

    [Fact]
    public async Task MismatchedMetadataCannotStartPlayback()
    {
        var noise = new AdaptiveSnrTests.NoiseProvider();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new Provider(),
            player,
            preparation: new WrongPreparation(noise),
            noiseProvider: noise
        );
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        await session.StartAsync();
        Assert.Contains("元数据", session.Error);
        Assert.Equal(0, player.PlayCount);
        Assert.False(session.CanScore);
    }

    private static SpeechTrainingSession Create(
        Provider provider,
        ControllableSpeechAudioPlayer player
    ) => new(provider, player, noiseProvider: new AdaptiveSnrTests.NoiseProvider());

    private sealed class Provider : ISpeechCorpusProvider
    {
        public int? FailSentence { get; set; }
        private static readonly SpeechSentence[] Sentences = Enumerable
            .Range(1, 10)
            .Select(n => new SpeechSentence(
                $"custom/{n}",
                n,
                n % 2 == 1 ? "一二三" : "一二三四",
                $"{n}.wav"
            ))
            .ToArray();
        public IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new("custom", Sentences, SnrNoise: new("ssn.wav", "babble.wav")),
            new("missing", Sentences),
        ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            sentence.Number == FailSentence
                ? throw new IOException("test failure")
                : AdaptiveSnrTests.Tone();
    }

    private sealed class WrongPreparation(ISpeechNoiseProvider noise) : TestPreparationBase(noise)
    {
        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        ) =>
            Service.Prepare(
                request with
                {
                    Noise = request.Noise! with { SnrDb = 11 },
                },
                token: token
            );
    }

    private sealed class DelayedPreparation(ISpeechNoiseProvider noise) : TestPreparationBase(noise)
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        )
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return Service.Prepare(request);
        }
    }
}
