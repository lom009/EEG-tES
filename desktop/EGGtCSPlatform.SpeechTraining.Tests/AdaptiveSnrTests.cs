using System.Text.Json;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class AdaptiveSnrTests
{
    [Fact]
    public void StaircaseMatchesIndependentReferenceAndRejectsDuplicateSubmissions()
    {
        using var json = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "AdaptiveSnrReference.json")
            )
        );
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var state = new AdaptiveSnrState();
            int n = 0;
            foreach (var expected in item.GetProperty("Submissions").EnumerateArray())
            {
                n++;
                var result = state.Submit(
                    $"s/{n}",
                    n,
                    new(7, 4),
                    expected.GetProperty("Passed").GetBoolean() ? 4 : 3,
                    7
                );
                Assert.Equal(expected.GetProperty("Snr").GetDouble(), result.SnrDb);
                Assert.Equal(expected.GetProperty("Next").GetDouble(), state.SnrDb);
                Assert.Equal(expected.GetProperty("Step").GetDouble(), result.StepDb);
                Assert.Equal(expected.GetProperty("Reversals").GetInt32(), result.Reversals);
                Assert.Throws<InvalidOperationException>(() =>
                    state.Submit($"s/{n}", n, new(7, 4), 4, 7)
                );
            }
        }
        foreach (var value in new[] { -1d, 21, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveSnrState(value));
    }

    [Theory]
    [InlineData(SpeechNoiseKind.Ssn)]
    [InlineData(SpeechNoiseKind.Babble)]
    public async Task SessionLocksSettingsReplaysAndSummarizesSubmittedSnr(SpeechNoiseKind kind)
    {
        var provider = new Provider(10);
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            provider,
            player,
            noiseProvider: new NoiseProvider()
        );
        session.SetSpeechRate(3);
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        Assert.Equal(0, session.SpeechRate);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.False(session.IsSnrMode);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(3, session.SpeechRate);
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        session.SelectNoise(kind);
        session.SetInitialSnr(12);
        await session.StartAsync();
        Assert.Null(session.Error);
        session.SetInitialSnr(2);
        session.SelectNoise(
            kind == SpeechNoiseKind.Ssn ? SpeechNoiseKind.Babble : SpeechNoiseKind.Ssn
        );
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(12, session.InitialSnrDb);
        Assert.Equal(kind, session.NoiseKind);
        Assert.Equal(SpeechTrainingMode.AdaptiveSnr, session.Mode);
        var first = session.Audio;
        session.Stop();
        await session.ReplayAsync();
        Assert.Same(first, session.Audio);
        Assert.Empty(session.SnrSubmissions);
        for (var i = 0; i < 10; i++)
        {
            Assert.Null(session.Error);
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
        }
        await session.SaveNextAsync();
        var result = session.Result!;
        Assert.Equal(10, result.Sentences.Count);
        Assert.Equal(11, result.Playbacks.Length);
        Assert.Equal(12, result.Sentences[0].AdaptiveSnr!.Submission.SnrDb);
        Assert.Equal("测试完成", result.SnrSummary!.Status);
        Assert.Equal(
            result.Sentences.TakeLast(8).Average(s => s.AdaptiveSnr!.Submission.SnrDb),
            result.SnrSummary.MeanSnrDb
        );
        Assert.False(result.SnrSummary.MixedCarrier);
        Assert.All(result.Playbacks, p => Assert.Equal(kind, p.Noise!.Settings.Kind));
        Assert.All(
            result.Sentences,
            s => Assert.Equal(s.AdaptiveSnr!.Submission.SnrDb, s.AdaptiveSnr.Noise.Settings.SnrDb)
        );
    }

    [Fact]
    public async Task FailureRetryAndUnsavedMixedPlaybackDoNotAdvanceState()
    {
        var provider = new Provider(10) { FailSentence = 2 };
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            provider,
            player,
            noiseProvider: new NoiseProvider()
        );
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        await session.StartAsync();
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.NotNull(session.Error);
        Assert.False(session.CanSaveNext);
        Assert.Single(session.SnrSubmissions);
        Assert.Equal(2, session.CurrentSnrDb);
        provider.FailSentence = null;
        await session.ReplayAsync();
        var offset = session.Waveform!.Preparation!.Noise!.OffsetFrames;
        session.Stop();
        session.SetVocoder(new() { Bands = 2, Maxima = 2 });
        await session.ReplayAsync();
        Assert.Null(session.Error);
        Assert.Equal(offset, session.Waveform!.Preparation!.Noise!.OffsetFrames);
        Assert.Single(session.SnrSubmissions);
        session.End();
        Assert.False(session.Result!.SnrSummary!.MixedCarrier);
        Assert.Equal("测试未完成", session.Result.SnrSummary.Status);
        Assert.Null(session.Result.SnrSummary.MeanSnrDb);
    }

    [Fact]
    public async Task MissingNoiseOnlyDisablesSnrAndIncompleteMetadataCannotStartPlayback()
    {
        var provider = new Provider(1, false);
        using var missing = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer()
        );
        Assert.True(missing.CanUseAdaptiveMode);
        missing.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        Assert.False(missing.CanStart);
        await missing.StartAsync();
        Assert.False(missing.Started);
        missing.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.True(missing.CanStart);
        var player = new ControllableSpeechAudioPlayer();
        using var invalid = new SpeechTrainingSession(
            new Provider(1),
            player,
            preparation: new IgnoringPreparation(),
            noiseProvider: new NoiseProvider()
        );
        invalid.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        await invalid.StartAsync();
        Assert.Contains("元数据", invalid.Error);
        Assert.Equal(0, player.PlayCount);
        Assert.False(invalid.CanScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledPreparationCannotOverwriteReplayOrAdvance(bool dispose)
    {
        var noise = new NoiseProvider();
        var preparation = new DelayedPreparation(noise);
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new Provider(2),
            player,
            preparation: preparation,
            noiseProvider: noise
        );
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        var pending = session.StartAsync();
        try
        {
            await preparation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Stop();
            if (dispose)
                session.Dispose();
            else
                await session.ReplayAsync();
            var waveform = session.Waveform;
            preparation.Release.TrySetResult();
            await pending;
            Assert.Same(waveform, session.Waveform);
            Assert.Empty(session.SnrSubmissions);
            Assert.Equal(dispose ? 0 : 1, player.PlayCount);
            if (!dispose)
            {
                var oldId = player.PlaybackId;
                session.RateAll(true);
                await session.SaveNextAsync();
                player.Emit(oldId, AudioPlaybackState.Completed, TimeSpan.FromSeconds(20));
                Assert.True(session.IsPlaying);
                Assert.Single(session.SnrSubmissions);
            }
        }
        finally
        {
            preparation.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ShortCompletedListHasNoThreshold()
    {
        using var session = new SpeechTrainingSession(
            new Provider(1),
            new ControllableSpeechAudioPlayer(),
            noiseProvider: new NoiseProvider()
        );
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        await session.StartAsync();
        session.RateAll(false);
        await session.SaveNextAsync();
        Assert.Equal("样本不足", session.Result!.SnrSummary!.Status);
        Assert.Null(session.Result.SnrSummary.MeanSnrDb);
    }

    private sealed class IgnoringPreparation : TestPreparationBase
    {
        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        ) => Service.Prepare(request with { Settings = new() });
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

    internal static AudioBuffer Tone(
        int length = 3200,
        int rate = 16000,
        int channels = 1,
        double frequency = 400
    ) =>
        Pcm16Codec.Encode(
            Enumerable
                .Range(0, channels)
                .Select(c =>
                    Enumerable
                        .Range(0, length)
                        .Select(i => .1 * Math.Sin(2 * Math.PI * (frequency + c * 100) * i / rate))
                        .ToArray()
                )
                .ToArray(),
            rate,
            out _
        );

    internal sealed class NoiseProvider : ISpeechNoiseProvider
    {
        public AudioBuffer Audio { get; set; } = Tone(2000, 16000, frequency: 700);
        public double CalibrationRms { get; set; } = .03;

        public SpeechNoiseResource Read(string fileName) => new(Audio, "test-hash");
    }

    private sealed class Provider(int count, bool withNoise = true) : ISpeechCorpusProvider
    {
        public int? FailSentence { get; set; }
        public IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new(
                "custom",
                Enumerable
                    .Range(1, count)
                    .Select(n => new SpeechSentence($"custom/{n}", n, "一二三四五六七", $"{n}.wav"))
                    .ToArray(),
                new(7, 4),
                SnrNoise: withNoise ? new("ssn.wav", "babble.wav") : null
            ),
        ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            sentence.Number == FailSentence ? throw new IOException("test failure") : Tone();
    }
}
