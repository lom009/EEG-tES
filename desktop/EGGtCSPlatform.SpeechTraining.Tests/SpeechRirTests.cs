using System.Collections.Immutable;
using System.Text.Json;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechRirTests
{
    private static readonly SpeechSentence Sentence = new("rir/1", 1, "测试句", "1.wav");
    internal static RirSettings Fast => RirSettings.Default with { T60 = .15, LengthSeconds = .15 };

    [Fact]
    public void PhysicalArrivalReciprocityAndImpulseConvolution()
    {
        var settings = Fast with { T60 = 0 };
        var response = new RirGenerator().Generate(settings);
        var expectedArrival = 2d / 340 * 16000; // Default positions are exactly two metres apart.
        Assert.InRange(Math.Abs(response.DirectFrame - expectedArrival), 0, 1);
        var reverse = new RirGenerator().Generate(
            settings with
            {
                Source = settings.Listener,
                Listener = settings.Source,
            }
        );
        Assert.InRange(
            response.Full.Zip(reverse.Full).Max(p => Math.Abs(p.First - p.Second)),
            0,
            1e-10
        );
        var convolution = Reverb.Convolve([1, 2, -1], [0, 1, .5], default);
        var expected = new[] { 0d, 1, 2.5, 0, -.5 };
        Assert.InRange(convolution.Zip(expected).Max(p => Math.Abs(p.First - p.Second)), 0, 1e-12);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledStartAndLatePlaybackCallbackCannotEnableScoring(bool dispose)
    {
        var generator = new DelayedGenerator();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            new Corpus(),
            player,
            10,
            noiseProvider: new AdaptiveSnrTests.NoiseProvider(),
            rirGenerator: generator
        );
        session.SelectMode(SpeechTrainingMode.FixedRir);
        session.SetRir(new(Fast));
        var start = session.StartAsync();
        await generator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose)
            session.Dispose();
        else
            session.CancelRir();
        generator.Release.TrySetResult();
        await start;
        player.Emit(0, AudioPlaybackState.Completed, TimeSpan.FromSeconds(5));
        Assert.Null(session.Waveform);
        Assert.False(session.CanScore);
        if (!dispose)
        {
            await session.ReplayAsync();
            Assert.Null(session.Error);
            Assert.True(session.CanScore);
            player.Emit(player.PlaybackId, AudioPlaybackState.Failed, TimeSpan.Zero, "failure");
            Assert.False(session.CanScore);
            await session.ReplayAsync();
            Assert.Null(session.Error);
            session.End();
            Assert.Empty(session.Result!.Sentences);
            Assert.NotEmpty(session.Result.Playbacks);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void SegmentSelectionCalibrationAndCleanEnvelope(bool early, bool late)
    {
        var settings = Fast with { Early = early, Late = late };
        var generator = new RirGenerator();
        var response = generator.Generate(settings);
        Assert.Same(response, generator.Generate(settings));
        Assert.Equal(
            late ? response.Full.Length : response.DivisionFrame + 1,
            response.Effective.Length
        );
        Assert.Equal(
            response.Full.Take(response.DirectFrame + 11),
            response.Effective.Take(response.DirectFrame + 11)
        );
        Assert.All(
            response
                .Effective.Skip(response.DirectFrame + 11)
                .Take(response.DivisionFrame - response.DirectFrame - 10),
            x =>
            {
                if (!early)
                    Assert.Equal(0, x);
            }
        );
        var input = AdaptiveSnrTests.Tone();
        var prepared = TestAudio.Prepare(
            input,
            new(Mode: SpeechTrainingMode.FixedRir, Room: new(settings)),
            Sentence,
            generator: generator
        );
        var m = prepared.Rir!;
        Assert.Equal(input.Data.Length / 2 + response.Effective.Length - 1, m.OutputFrames);
        Assert.Same(input, prepared.EnvelopeSource.Audio);
        Assert.Equal(response.DirectFrame / 16000d, prepared.EnvelopeSource.Timeline.StartSeconds);
        Assert.Equal(.05 / response.EarlyCoefficientRms, m.CalibrationGain);
        Assert.Equal(new RirGenerator().Generate(Fast).EarlyCoefficientRms, m.EarlyCoefficientRms);
        var waveform = SpeechWaveformData.Prepare(prepared);
        var devicePlan = EnvelopePlaybackPlan.Create(1, waveform, new(2, 300, 1000, 3));
        var referenceStart = prepared.EnvelopeSource.Timeline.StartSeconds;
        for (var i = 0; i < devicePlan.Samples.Length; i++)
            if (
                i / 100d < referenceStart
                || i / 100d >= referenceStart + input.Duration.TotalSeconds
            )
                Assert.Equal(0, devicePlan.Samples[i]);
        Assert.Equal(devicePlan.Samples.Length / 100d + .3, devicePlan.DisplayDurationSeconds, 10);
        Assert.All(
            waveform.Stimulus.Waveform.Where(p =>
                p.Seconds < prepared.EnvelopeSource.Timeline.StartSeconds - 1e-8
                || p.Seconds
                    > input.Duration.TotalSeconds
                        + prepared.EnvelopeSource.Timeline.StartSeconds
                        + 1e-8
            ),
            p => Assert.Equal(0, p.Value)
        );
        Assert.NotEmpty(waveform.Voice);
        Assert.Null(prepared.Noise);
    }

    [Theory]
    [InlineData(-10, SpeechNoiseKind.Ssn)]
    [InlineData(0, SpeechNoiseKind.Ssn)]
    [InlineData(10, SpeechNoiseKind.Ssn)]
    [InlineData(30, SpeechNoiseKind.Ssn)]
    [InlineData(-10, SpeechNoiseKind.Babble)]
    [InlineData(0, SpeechNoiseKind.Babble)]
    [InlineData(10, SpeechNoiseKind.Babble)]
    [InlineData(30, SpeechNoiseKind.Babble)]
    public void NoiseUsesEnabledEarlySpeechEnergyAndPreservesCalibration(
        double snr,
        SpeechNoiseKind kind
    )
    {
        var provider = new AdaptiveSnrTests.NoiseProvider();
        var input = AdaptiveSnrTests.Tone();
        foreach (var early in new[] { true, false })
        {
            var settings = Fast with { Early = early };
            var noise = new SpeechNoiseSettings(
                kind,
                "ssn.wav",
                provider.Read("ssn.wav").Sha256,
                snr,
                321,
                .5
            );
            var config = new TrainingAudioSettings(
                Mode: SpeechTrainingMode.FixedRir,
                Room: new(settings, true, kind, snr)
            );
            var prepared = TestAudio.Prepare(
                input,
                config,
                Sentence,
                noise: noise,
                provider: provider
            );
            var m = prepared.Rir!;
            var offset = TrainingNoiseOffset.Calculate(noise.Seed, Sentence.Id, m.OutputFrames);
            var shifted = NoiseMixer.MatchNoise(
                provider.Read(noise.FileName).Audio,
                m.OutputFrames,
                16000,
                1,
                offset
            );
            var response = new RirGenerator().Generate(settings);
            var clean = Pcm16Codec.Decode(input)[0];
            var earlySignal = Reverb.Convolve(clean, response.EarlyReference, default);
            var referenceRms =
                Math.Sqrt(earlySignal.Sum(x => x * x) / m.OutputFrames) * m.CalibrationGain;
            Assert.Equal(referenceRms, m.EarlySpeechRms, 10);
            var wet = Reverb.Convolve(clean, response.Effective, default);
            var decoded = Pcm16Codec.Decode(prepared.Playback)[0];
            Assert.InRange(
                decoded
                    .Select(
                        (v, i) =>
                            Math.Abs(
                                v
                                    - (wet[i] * m.CalibrationGain + shifted[0][i] * m.NoiseGain)
                                        * m.PeakGain
                            )
                    )
                    .Max(),
                0,
                1d / 32768
            );
            var achieved =
                20 * Math.Log10(m.EarlySpeechRms / (NoiseMixer.Rms(shifted) * m.NoiseGain));
            Assert.Equal(snr, achieved, 9);
            Assert.Equal(offset, prepared.Noise!.OffsetFrames);
            var replay = TestAudio.Prepare(
                input,
                config,
                Sentence,
                noise: noise,
                provider: provider
            );
            Assert.Equal(prepared.Playback.ToArray(), replay.Playback.ToArray());
            Assert.InRange(m.PeakGain, double.Epsilon, 1);
            Assert.True(m.PeakGain < 1); // Deliberately high calibration exercises whole-mix attenuation.
            Assert.Equal(1, prepared.Noise.NormalizationGain);
        }
    }

    [Theory]
    [InlineData(VocoderAlgorithm.ClassicalCis, VocoderCarrier.Noise)]
    [InlineData(VocoderAlgorithm.ClassicalCis, VocoderCarrier.Tone)]
    [InlineData(VocoderAlgorithm.Ace, VocoderCarrier.Noise)]
    [InlineData(VocoderAlgorithm.Ace, VocoderCarrier.Tone)]
    [InlineData(VocoderAlgorithm.ClassicalTle, VocoderCarrier.Noise)]
    [InlineData(VocoderAlgorithm.ClassicalTle, VocoderCarrier.Tone)]
    [InlineData(VocoderAlgorithm.AceTle, VocoderCarrier.Noise)]
    [InlineData(VocoderAlgorithm.AceTle, VocoderCarrier.Tone)]
    public void ResampleStereoTailAndEveryVocoder(
        VocoderAlgorithm algorithm,
        VocoderCarrier carrier
    )
    {
        var original = SpeechAudioPreparationTests.Tone(24000, 2400, 2);
        var config = new TrainingAudioSettings(
            Mode: SpeechTrainingMode.FixedRir,
            Room: new(Fast),
            Vocoder: new(algorithm, 8, carrier)
        );
        var prepared = TestAudio.Prepare(original, config, Sentence);
        Assert.Equal(16000, prepared.Playback.SampleRate);
        Assert.Equal(2, prepared.Playback.Channels);
        Assert.Equal(1600 + Fast.Frames - 1, prepared.Rir!.OutputFrames);
        Assert.Equal(
            SincResampler.Resample(original, 16000, default).ToArray(),
            prepared.EnvelopeSource.Audio.ToArray()
        );
        Assert.Equal(
            prepared.Rir.DirectFrame / 16000d,
            prepared.EnvelopeSource.Timeline.StartSeconds
        );
    }

    [Fact]
    public void PhysicalAndComputationBoundsRejectInvalidRequests()
    {
        var invalid = new[]
        {
            Fast with
            {
                T60 = double.NaN,
            },
            Fast with
            {
                T60 = .001,
            },
            Fast with
            {
                LengthSeconds = 11,
            },
            Fast with
            {
                Source = Fast.Listener,
            },
            Fast with
            {
                Source = new(0, 1, 1),
            },
            Fast with
            {
                EarlyLateMilliseconds = .5,
            },
            Fast with
            {
                EarlyLateMilliseconds = 1000,
            },
            Fast with
            {
                Room = new(.1, .1, .1),
                Source = new(.02, .03, .02),
                Listener = new(.02, .01, .02),
                T60 = 1,
            },
        };
        foreach (var settings in invalid)
            Assert.ThrowsAny<ArgumentException>(() => new RirGenerator().Generate(settings));
        Assert.Throws<OperationCanceledException>(() =>
            new RirGenerator().Generate(Fast, new(true))
        );
        foreach (var db in new[] { double.MaxValue, -double.MaxValue })
        {
            var noiseProvider = new AdaptiveSnrTests.NoiseProvider();
            var settings = new TrainingRoomSettings(Fast, true, SnrDb: db);
            var noise = new SpeechNoiseSettings(
                SpeechNoiseKind.Ssn,
                "ssn.wav",
                noiseProvider.Read("ssn.wav").Sha256,
                db,
                0,
                .05
            );
            Assert.ThrowsAny<ArgumentException>(() =>
                TestAudio.Prepare(
                    AdaptiveSnrTests.Tone(),
                    new(Mode: SpeechTrainingMode.FixedRir, Room: settings),
                    Sentence,
                    noise: noise,
                    provider: noiseProvider
                )
            );
        }
    }

    [Fact]
    public async Task FullSessionLocksConditionsReplaysRetriesAndRecordsActualCarriers()
    {
        var provider = new Corpus();
        var player = new ControllableSpeechAudioPlayer();
        using var session = new SpeechTrainingSession(
            provider,
            player,
            10,
            noiseProvider: new AdaptiveSnrTests.NoiseProvider()
        );
        session.SetSpeechRate(4);
        session.SetFixedSnr(-10);
        session.SetInitialSnr(12);
        session.SelectMode(SpeechTrainingMode.FixedRir);
        session.SetRir(new(Fast, AddNoise: true, SnrDb: -5));
        Assert.True(session.CanStart);
        Assert.False(session.CanUseAdaptiveMode);
        await session.GenerateRirAsync();
        Assert.NotNull(session.RirPreview);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        Assert.Equal(-10, session.CurrentSnrDb);
        session.SelectMode(SpeechTrainingMode.AdaptiveSnr);
        Assert.Equal(12, session.CurrentSnrDb);
        session.SelectMode(SpeechTrainingMode.AdaptiveRate);
        Assert.Equal(1.5, 1 + session.AdaptiveR);
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(4, session.SpeechRate);
        Assert.NotEqual(SpeechTrainingMode.FixedRir, session.Mode);
        session.SelectMode(SpeechTrainingMode.FixedRir);
        await session.StartAsync();
        Assert.Null(session.Error);
        var frozen = session.RirSettings;
        session.SetRir(new(Fast));
        session.SelectMode(SpeechTrainingMode.FixedRate);
        Assert.Equal(frozen, session.RirSettings);
        var audio = session.Audio;
        player.Complete();
        await session.ReplayAsync();
        Assert.Same(audio, session.Audio);
        player.Complete();
        session.SetVocoder(new());
        await session.ReplayAsync();
        Assert.Null(session.Error);
        provider.FailSentence = 2;
        session.RateAll(true);
        await session.SaveNextAsync();
        Assert.NotNull(session.Error);
        Assert.False(session.CanSaveNext);
        await session.SaveNextAsync();
        provider.FailSentence = null;
        await session.ReplayAsync();
        Assert.Null(session.Error);
        for (var i = 1; i < 10; i++)
        {
            session.RateAll(i % 2 == 0);
            await session.SaveNextAsync();
            Assert.Null(session.Error);
        }
        var result = session.Result!;
        Assert.Equal(10, result.Sentences.Count);
        Assert.Equal(35, result.RatedCount);
        Assert.Equal(15, result.CorrectCount);
        Assert.Null(result.SnrSummary);
        Assert.Null(result.AdaptiveSummary);
        Assert.Empty(session.SnrSubmissions);
        Assert.Empty(session.AdaptiveSubmissions);
        Assert.Equal(frozen, result.Settings.Rir);
        Assert.All(result.Sentences, s => Assert.Equal(frozen, s.Rir!.Configuration.Settings));
        Assert.Equal(12, result.Playbacks.Length);
        Assert.Equal(
            1,
            result.Playbacks.Select(p => p.AudioSnapshot.Settings.Vocoder).Distinct().Count()
        );
        Assert.All(result.Playbacks, p => Assert.NotNull(p.Rir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledGenerationCannotPublishStalePreview(bool dispose)
    {
        var generator = new DelayedGenerator();
        using var session = new SpeechTrainingSession(
            new Corpus(),
            new ControllableSpeechAudioPlayer(),
            10,
            noiseProvider: new AdaptiveSnrTests.NoiseProvider(),
            rirGenerator: generator
        );
        session.SelectMode(SpeechTrainingMode.FixedRir);
        session.SetRir(new(Fast));
        var task = session.GenerateRirAsync();
        await generator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.CanStart);
        if (dispose)
            session.Dispose();
        else
        {
            session.CancelRir();
            session.SetRir(new(Fast with { Early = false }));
        }
        generator.Release.TrySetResult();
        await task;
        Assert.Null(session.RirPreview);
        Assert.False(session.IsGeneratingRir);
        Assert.False(session.CanScore);
    }

    [Fact]
    public async Task NoiseIsOptionalAndBadPreparationCannotBeScored()
    {
        var provider = new Corpus(false);
        using var noNoise = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer(),
            10,
            noiseProvider: new AdaptiveSnrTests.NoiseProvider()
        );
        noNoise.SelectMode(SpeechTrainingMode.FixedRir);
        noNoise.SetRir(new(Fast));
        Assert.True(noNoise.CanStart);
        noNoise.SetRir(new(Fast, AddNoise: true));
        Assert.False(noNoise.CanStart);
        using var bad = new SpeechTrainingSession(
            provider,
            new ControllableSpeechAudioPlayer(),
            10,
            preparation: new WrongPreparation(),
            noiseProvider: new AdaptiveSnrTests.NoiseProvider()
        );
        bad.SelectMode(SpeechTrainingMode.FixedRir);
        bad.SetRir(new(Fast));
        await bad.StartAsync();
        Assert.Contains("元数据", bad.Error);
        Assert.False(bad.CanScore);
        bad.End();
        Assert.Empty(bad.Result!.Sentences);
    }

    private sealed class Corpus(bool noise = true) : ISpeechCorpusProvider
    {
        public int? FailSentence { get; set; }
        public IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new(
                "custom",
                Enumerable
                    .Range(1, 10)
                    .Select(i => new SpeechSentence(
                        $"custom/{i}",
                        i,
                        i % 2 == 1 ? "一二三" : "一二三四",
                        $"{i}.wav"
                    ))
                    .ToArray(),
                SnrNoise: noise ? new("ssn.wav", "ssn.wav") : null
            ),
        ];

        public AudioBuffer ReadAudio(SpeechSentence sentence) =>
            sentence.Number == FailSentence
                ? throw new IOException("retry")
                : AdaptiveSnrTests.Tone();
    }

    private sealed class DelayedGenerator : IRirGenerator
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RirResponse Generate(RirSettings settings, CancellationToken token)
        {
            Entered.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(10));
            return new RirGenerator().Generate(settings);
        }
    }

    private sealed class WrongPreparation : TestPreparationBase
    {
        private static TrainingAudioPreparationResult RemoveMetadata(
            TrainingAudioPreparationResult r
        ) => r with { Snapshot = r.Snapshot with { Rir = null } };

        public override TrainingAudioPreparationResult Prepare(
            TrainingAudioRequest request,
            TrainingAudioPreparationResult? cached = null,
            CancellationToken token = default
        ) => RemoveMetadata(Service.Prepare(request, token: token));
    }
}
