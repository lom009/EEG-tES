using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class TrainingAudioApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoiseSourceIdentityAndConnectionsInvalidateCacheWithoutChangingCleanAnalysis(
        bool rir
    )
    {
        var provider = new AdaptiveSnrTests.NoiseProvider();
        var service = new TrainingAudioPreparationService(new TestAudio.Corpus(), provider);
        var settings = rir
            ? new TrainingAudioSettings(
                Mode: SpeechTrainingMode.FixedRir,
                Room: new(
                    RirSettings.Default with
                    {
                        T60 = .15,
                        LengthSeconds = .15,
                    },
                    AddNoise: true
                )
            )
            : new TrainingAudioSettings(Mode: SpeechTrainingMode.FixedSnr);
        var sentence = new SpeechSentence("noise", 1, "测试。", "");
        var request = service.CreateRequest(
            sentence,
            settings,
            1,
            1729,
            new("noise.wav", "noise.wav")
        );
        var first = service.Prepare(request);
        Assert.Same(first, service.Prepare(request, first));
        var source = first.Pipeline.Nodes.Single(n => n.Id == "noise-source");
        Assert.Empty(source.Inputs);
        var mix = first.Pipeline.Nodes.Single(n => n.Id == (rir ? "room" : "noise"));
        Assert.Equal(new AudioOutputRef("noise-source"), mix.Inputs["noise"]);
        var snapshot = JsonSerializer.Serialize(first.Snapshot);
        Assert.DoesNotContain("\"Buffer\":", snapshot);
        Assert.DoesNotContain("\"Data\":", snapshot);
        Assert.Contains(provider.Audio.ContentHash, snapshot);
        // A file identity alone is insufficient: a changed decoded buffer also invalidates the cache.
        provider.Audio = AdaptiveSnrTests.Tone(2000, 16000, frequency: 1200);
        var changed = service.CreateRequest(
            sentence,
            settings,
            1,
            1729,
            new("noise.wav", "noise.wav")
        );
        Assert.Equal(request.NoiseResource!.Sha256, changed.NoiseResource!.Sha256);
        var second = service.Prepare(changed, first);
        Assert.NotSame(first, second);
        Assert.NotEqual(first.Playback.ContentHash, second.Playback.ContentHash);
        Assert.Equal(first.Envelope.ToArray(), second.Envelope.ToArray());
        Assert.Equal(first.EnvelopeSource.Audio.ToArray(), second.EnvelopeSource.Audio.ToArray());

        AudioNodeDefinition rewired = rir
            ? new AudioNodeDefinition<ReverbOptions>(
                mix.Id,
                new EGGtCSPlatform.Audio.Dsp.Reverb.ReverbProcessor(),
                ((AudioNodeDefinition<ReverbOptions>)mix).Options,
                mix.Inputs.SetItem("noise", AudioOutputRef.Source)
            )
            : new AudioNodeDefinition<EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixOptions>(
                mix.Id,
                new EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixProcessor(),
                ((AudioNodeDefinition<EGGtCSPlatform.Audio.Dsp.Noise.NoiseMixOptions>)mix).Options,
                mix.Inputs.SetItem("noise", AudioOutputRef.Source)
            );
        var tampered = first with
        {
            Pipeline = first.Pipeline with { Nodes = first.Pipeline.Nodes.Replace(mix, rewired) },
        };
        Assert.NotSame(tampered, service.Prepare(request, tampered));
    }

    [Fact]
    public void EnvelopeOptionsInvalidateCacheAndExecutedSnapshotDescribesAnalysis()
    {
        var service = new TrainingAudioPreparationService(new TestAudio.Corpus());
        var request = service.CreateRequest(new("env", 1, "测试。", ""), new(), 1, 1729, null);
        var first = service.Prepare(request);
        Assert.Same(first, service.Prepare(request, first));
        Assert.False(first.Snapshot.Settings.Envelope.EnableFilter);
        Assert.Equal(4, first.Snapshot.Execution.Count(n => n.NodeId.StartsWith("envelope/")));
        Assert.DoesNotContain(first.Snapshot.Nodes, n => n.Id == "envelope/filter");
        var changed = request with
        {
            Settings = request.Settings with
            {
                Envelope = new(CutoffHz: 350, FilterOrder: 4, EnableFilter: true),
            },
        };
        var second = service.Prepare(changed, first);
        Assert.NotSame(first, second);
        Assert.Equal(first.Playback.ToArray(), second.Playback.ToArray());
        Assert.Equal(first.EnvelopeSource.Audio.ToArray(), second.EnvelopeSource.Audio.ToArray());
        Assert.Equal(
            EnvelopeExtractor.Extract(second.EnvelopeSource.Audio, changed.Settings.Envelope),
            second.Envelope.ToArray()
        );
        Assert.Equal(350, second.Snapshot.Settings.Envelope.CutoffHz);
        Assert.True(second.Snapshot.Settings.Envelope.EnableFilter);
        Assert.NotEqual(first.Envelope.ToArray(), second.Envelope.ToArray());
        Assert.Equal(
            EGGtCSPlatform.Audio.Core.Buffers.SignalSampleType.Float64,
            second.Snapshot.OutputFormats!["envelope"].SampleType
        );
        var hilbert = second.Snapshot.Nodes.Single(n => n.Id == "envelope/hilbert");
        Assert.Equal(
            EGGtCSPlatform.Audio.Core.Buffers.SignalSampleType.Complex128,
            hilbert.OutputFormats!["audio"].SampleType
        );
        Assert.Equal(5, second.Snapshot.Execution.Count(n => n.NodeId.StartsWith("envelope/")));
        var display = SpeechAudioAnalysis.Prepare(second);
        Assert.Equal(second.Envelope.ToArray(), display.Envelope);
        Assert.Throws<OperationCanceledException>(() =>
            SpeechAudioAnalysis.Prepare(second, new(true))
        );
        Assert.Same(second, service.Prepare(changed, second));
        var restored = service.Prepare(request, second);
        Assert.NotSame(second, restored);
        Assert.Equal(first.Envelope.ToArray(), restored.Envelope.ToArray());
        Assert.DoesNotContain(restored.Snapshot.Nodes, n => n.Id == "envelope/filter");
    }

    [Fact]
    public void BusinessConfigurationAndIndividualControlsKeepPendingSettingsConsistent()
    {
        using var session = new SpeechTrainingSession(
            new FileSpeechCorpusProvider(),
            new ControllableSpeechAudioPlayer()
        );
        session.ConfigureAudio(
            new(Mode: SpeechTrainingMode.AdaptiveSnr, SnrDb: 3, NoiseKind: SpeechNoiseKind.Babble)
        );
        Assert.Equal(3, session.CurrentSnrDb);
        Assert.Equal(3, session.InitialSnrDb);
        Assert.Equal(3, session.AudioSettings.SnrDb);
        session.SetInitialSnr(5);
        Assert.Equal(5, session.AudioSettings.SnrDb);
        session.SelectMode(SpeechTrainingMode.FixedSnr);
        session.SetFixedSnr(-7);
        Assert.Equal(-7, session.AudioSettings.SnrDb);
        session.SelectNoise(SpeechNoiseKind.Ssn);
        Assert.Equal(SpeechNoiseKind.Ssn, session.AudioSettings.NoiseKind);
        session.SelectMode(SpeechTrainingMode.FixedRir);
        var room = new TrainingRoomSettings(RirSettings.Default with { Early = false });
        session.SetRir(room);
        Assert.Equal(room, session.AudioSettings.Room);
    }

    [Fact]
    public void CacheChecksInputParametersResourceIdentityAndActualRoutes()
    {
        var audio = AdaptiveSnrTests.Tone();
        var provider = new AdaptiveSnrTests.NoiseProvider();
        var service = new TrainingAudioPreparationService(new TestAudio.Corpus(audio), provider);
        var request = service.CreateRequest(
            new("cache", 1, "今天好。", "fixture.wav"),
            new(Mode: SpeechTrainingMode.FixedSnr),
            1,
            1729,
            new("noise.wav")
        );
        var first = service.Prepare(request);
        Assert.Same(
            first,
            service.Prepare(
                request with
                {
                    Source = new(audio.SampleRate, audio.Channels, audio.Data),
                },
                first
            )
        );
        var changed = request with { Source = AdaptiveSnrTests.Tone(frequency: 630) };
        Assert.NotSame(first, service.Prepare(changed, first));
        changed = request with
        {
            Noise = request.Noise! with { Sha256 = "replacement-file" },
            NoiseResource = request.NoiseResource! with { Sha256 = "replacement-file" },
        };
        var replaced = service.Prepare(changed, first);
        Assert.NotSame(first, replaced);
        Assert.Equal(first.Playback.ToArray(), replaced.Playback.ToArray()); // Same PCM, different file identity.
        changed = request with { Seed = 2, Noise = request.Noise! with { Seed = 2 } };
        Assert.NotSame(first, service.Prepare(changed, first));
        changed = request with
        {
            Settings = request.Settings with { SnrDb = -3 },
            Noise = request.Noise! with { SnrDb = -3 },
        };
        Assert.NotSame(first, service.Prepare(changed, first));
        var tampered = first with
        {
            Pipeline = first.Pipeline with
            {
                Outputs = first.Pipeline.Outputs.SetItem(
                    "envelopeSource",
                    new(first.Pipeline.Outputs["playback"].Source, "playback")
                ),
            },
        };
        Assert.NotSame(tampered, service.Prepare(request, tampered));
        Assert.Throws<OperationCanceledException>(() => service.Prepare(request, first, new(true)));
    }

    [Fact]
    public void RirIsGeneratedOnceAndSnapshotsContainDataOnly()
    {
        var generator = new CountingGenerator();
        var service = new TrainingAudioPreparationService(
            new TestAudio.Corpus(),
            new AdaptiveSnrTests.NoiseProvider(),
            generator
        );
        var settings = new TrainingAudioSettings(
            Mode: SpeechTrainingMode.FixedRir,
            Room: new(RirSettings.Default with { T60 = .15, LengthSeconds = .15 }),
            Vocoder: new(Bands: 4, Maxima: 3)
        );
        var request = service.CreateRequest(new("rir", 1, "今天好。", ""), settings, 1, 1729, null);
        var first = service.Prepare(request);
        Assert.Equal(1, generator.Calls);
        Assert.Same(first, service.Prepare(request, first));
        Assert.Equal(1, generator.Calls);
        var reverb = Assert.IsType<ReverbMetadata>(
            first.Graph.Records.Single(x => x.NodeId == "room").Metadata
        );
        Assert.Same(first.RirResponse, reverb.Response);
        Assert.Equal(first.RirResponse!.FullHash, first.Snapshot.Rir!.FullHash);
        Assert.Equal(first.Graph.Records.Length, first.Snapshot.Execution.Length);
        var json = JsonSerializer.Serialize(first.Snapshot);
        Assert.DoesNotContain("\"Full\":", json);
        Assert.DoesNotContain("\"Effective\":", json);
        Assert.DoesNotContain("\"Data\":", json);
        Assert.DoesNotContain("ProcessorInstance", json);
        var bad = first with { Snapshot = first.Snapshot with { Rir = null } };
        Assert.Throws<InvalidOperationException>(bad.ValidateRequest);
    }

    [Fact]
    public async Task ExplicitCueReferenceExcludesCueBeforeNormalizationAndPreservesCarrierPhase()
    {
        var original = SpeechAudioPreparationTests.Tone(frames: 3200);
        var graph = new AudioPipelineBuilder()
            .Then("cue", new Cue(), .05)
            .Output("playback")
            .Output("envelopeSource", new("cue", "clean"), "playback")
            .Branch(new("cue", "clean"))
            .Then("envelope", new EnvelopeProcessor(), new EnvelopeOptions())
            .Output("envelope", alignWith: "playback")
            .Build();
        var result = await new AudioPipelineExecutor().ExecuteAsync(graph, original);
        var prepared = TestAudio.Prepare(original) with { Pipeline = graph, Graph = result };
        var before = TestAudio.Display(original);
        var after = SpeechWaveformData.Prepare(prepared);
        Assert.Equal(before.Stimulus.Amplitudes, after.Stimulus.Amplitudes);
        var beforePlan = EnvelopePlaybackPlan.Create(1, before, new(2, 0, 500, 3));
        var afterPlan = EnvelopePlaybackPlan.Create(2, after, new(2, 125, 500, 3));
        Assert.All(afterPlan.Samples.Take(5), b => Assert.Equal(0, b));
        Assert.Equal(beforePlan.Samples.ToArray(), afterPlan.Samples.Skip(5).ToArray());
        Assert.Equal(
            after.Audio.Duration.TotalSeconds + .125,
            afterPlan.DisplayDurationSeconds,
            10
        );
        Assert.Same(original, prepared.EnvelopeSource.Audio);
        Assert.All(
            after.Stimulus.Waveform.Where(p => p.Seconds < .05),
            p => Assert.Equal(0, p.Value)
        );
        Assert.All(
            after.Envelope.Where(p => p.Seconds + .001 < .05),
            p => Assert.Equal(0, p.Amplitude)
        );
        var point = before.Stimulus.Waveform[17];
        Assert.Contains(
            after.Stimulus.Waveform,
            p =>
                Math.Abs(p.Seconds - point.Seconds - .05) < 1e-10
                && Math.Abs(p.Value - point.Value) < 1e-10
        );
        Assert.Equal(
            prepared.Playback.Duration.TotalSeconds,
            after.Stimulus.Waveform[^1].Seconds,
            10
        );
    }

    private sealed class CountingGenerator : IRirGenerator
    {
        public int Calls;

        public RirResponse Generate(RirSettings settings, CancellationToken token = default)
        {
            Calls++;
            return new RirGenerator().Generate(settings, token);
        }
    }

    private sealed class Cue : SingleInputAudioProcessor<double>
    {
        public override string Identity => "test-cue-1";

        protected override AudioSignalFormat Describe(
            AudioSignalFormat input,
            double options,
            string nodeId
        ) => input;

        protected override IReadOnlyDictionary<string, AudioSignalFormat> DescribeOutputs(
            AudioSignalFormat input,
            double options,
            string nodeId
        ) => new Dictionary<string, AudioSignalFormat> { ["audio"] = input, ["clean"] = input };

        protected override AudioProcessResult Process(
            AudioSignal input,
            double seconds,
            AudioProcessingContext context,
            CancellationToken token
        )
        {
            int length = (int)(seconds * input.Audio.SampleRate) * input.Audio.Channels * 2;
            var data = new byte[length + input.Audio.Data.Length];
            for (int i = 0; i < length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), 30000);
            input.Audio.Data.CopyTo(data.AsSpan(length));
            return new(
                ImmutableDictionary<string, AudioSignal>
                    .Empty.Add(
                        "audio",
                        input with
                        {
                            Audio = new(input.Audio.SampleRate, input.Audio.Channels, data),
                        }
                    )
                    .Add(
                        "clean",
                        input with
                        {
                            Timeline = input.Timeline with
                            {
                                StartSeconds = seconds,
                                ZeroRegions = [new(0, seconds)],
                            },
                        }
                    )
            );
        }
    }
}
