using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Core.Timing;
using EGGtCSPlatform.Audio.Dsp.Conversion;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Filtering;
using EGGtCSPlatform.Audio.Dsp.IO;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Resampling;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.TimeStretch;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class NoiseInputTests
{
    private static readonly RirSettings Room = RirSettings.Default with
    {
        T60 = .15,
        LengthSeconds = .15,
    };

    [Fact]
    public async Task PcmPathsMatchFrozenPreMigrationMatchingMixingAndRir()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "NoiseInputBaseline.json")
            )
        );
        foreach (var item in fixture.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var speech = new AudioBuffer(
                16000,
                item.GetProperty("SpeechChannels").GetInt32(),
                Convert.FromHexString(item.GetProperty("SpeechPcm").GetString()!)
            );
            var noise = new AudioBuffer(
                item.GetProperty("NoiseRate").GetInt32(),
                item.GetProperty("NoiseChannels").GetInt32(),
                Convert.FromHexString(item.GetProperty("NoisePcm").GetString()!)
            );
            var rir = item.GetProperty("Rir").GetBoolean();
            var offset = item.GetProperty("Offset").GetInt32();
            var frames =
                speech.Frames + (rir ? new RirGenerator().Generate(Room).Effective.Length - 1 : 0);
            var matched = NoiseMixer.MatchNoise(
                noise,
                frames,
                speech.SampleRate,
                speech.Channels,
                offset
            );
            Assert.Equal(
                item.GetProperty("MatchedHash").GetString(),
                Convert.ToHexString(
                    SHA256.HashData(
                        MemoryMarshal.AsBytes(matched.SelectMany(c => c).ToArray().AsSpan())
                    )
                )
            );
            var result = await new AudioPipelineExecutor().ExecuteAsync(
                Graph(speech, rir, offset),
                noise
            );
            Assert.Equal(
                item.GetProperty("PlaybackHash").GetString(),
                result.GetAudio("out").ContentHash
            );
            var actual = JsonSerializer.SerializeToElement(
                result.Records.Single(r => r.NodeId == "mix").Metadata
            );
            foreach (var expected in item.GetProperty("Metadata").EnumerateObject())
                Assert.Equal(
                    expected.Value.GetRawText(),
                    actual.GetProperty(expected.Name).GetRawText()
                );
            var identity = InputMetadata(result, rir);
            Assert.Equal(NoiseInputMetadata.FromBuffer(noise), identity);
            Assert.Same(speech, result.GetAudio("clean"));
            if (noise.SampleRate == speech.SampleRate)
            {
                var channels = Pcm16Codec.Decode(noise);
                var floats = new FloatBuffer(
                    noise.SampleRate,
                    noise.Channels,
                    Enumerable
                        .Range(0, noise.Frames)
                        .SelectMany(i => channels.Select(c => c[i]))
                        .ToArray()
                );
                var floatMatch = NoiseMixer.MatchNoise(
                    floats,
                    frames,
                    speech.SampleRate,
                    speech.Channels,
                    offset
                );
                for (var channel = 0; channel < matched.Length; channel++)
                    Assert.Equal(matched[channel], floatMatch[channel]);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredNoiseIsConsumedWithoutQuantizationAndCleanEnvelopeIsUnchanged(
        bool rir
    )
    {
        var speech = Tone(16000, 53);
        var source = Tone(11025, 31);
        var original = source.ToArray();
        var graph = new AudioPipelineBuilder().Source("speech", speech).Output("clean");
        graph
            .Branch(new("speech"))
            .Then("envelope", new EnvelopeProcessor(), new EnvelopeOptions())
            .Output("envelope");
        graph
            .Branch(AudioOutputRef.Source)
            .Then("noise-rate", new ResampleProcessor(), new ResampleOptions(16000))
            .Then("noise-decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
            .Then(
                "noise-filter",
                new ButterworthFilterProcessor(),
                new ButterworthFilterOptions(500, 4)
            )
            .Output("processed");
        AddMix(graph, "mix", new("speech"), new("noise-filter"), rir, 0);
        graph.Output("out");
        AddMix(graph, "second-mix", new("speech"), new("noise-filter"), rir, 0);
        graph.Output("second");
        var result = await new AudioPipelineExecutor().ExecuteAsync(graph.Build(), source);
        var processed = result.GetFloat("processed");
        var response = new RirGenerator().Generate(Room);
        var frames = speech.Frames + (rir ? response.Effective.Length - 1 : 0);
        var noise = NoiseMixer.MatchNoise(processed, frames, 16000, 1, 0);
        var expected = rir
            ? EGGtCSPlatform.Audio.Dsp.Reverb.Reverb.Apply(speech, response, .05, noise, -3).Audio
            : NoiseMixer.Mix(speech, noise, -3, .05).Audio;
        Assert.Equal(expected.ToArray(), result.GetAudio("out").ToArray());
        Assert.Equal(result.GetAudio("out").ToArray(), result.GetAudio("second").ToArray());
        var plain = await new AudioPipelineExecutor().ExecuteAsync(Graph(speech, rir), source);
        Assert.NotEqual(plain.GetAudio("out").ContentHash, result.GetAudio("out").ContentHash);
        Assert.Equal(EnvelopeExtractor.Extract(speech), result.GetFloat("envelope").ToArray());
        Assert.Single(result.Records.Where(r => r.NodeId == "noise-filter"));
        Assert.Equal(NoiseInputMetadata.FromBuffer(processed), InputMetadata(result, rir));
        Assert.Equal(original, source.ToArray());

        // Every sample is below one PCM16 LSB. It must still be valid noise when supplied as Float64.
        var tiny = new FloatBuffer(
            16000,
            1,
            Enumerable.Range(0, 13).Select(i => 1e-8 * (i + 1)).ToArray()
        );
        var before = tiny.ToArray();
        var tinyResult = await new AudioPipelineExecutor().ExecuteAsync(Graph(speech, rir), tiny);
        Assert.Contains(tinyResult.GetAudio("out").ToArray(), x => x != 0);
        Assert.Equal(before, tiny.ToArray());
        var quantized = Pcm16Codec.Encode([tiny.ToArray()], 16000, out _);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AudioPipelineExecutor().ExecuteAsync(Graph(speech, rir), quantized).AsTask()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentNoiseStretchIsAllowedButPlacementMetadataIsRejected(bool rir)
    {
        var speech = Tone(16000, 53);
        var graph = new AudioPipelineBuilder().Source("speech", speech);
        graph
            .Branch(AudioOutputRef.Source)
            .Then("noise-speed", new TimeStretchProcessor(), new TimeStretchOptions(71));
        AddMix(graph, "mix", new("speech"), new("noise-speed"), rir, 0);
        var result = await new AudioPipelineExecutor().ExecuteAsync(
            graph.Output("out").Build(),
            Tone(16000, 100)
        );
        Assert.Equal("resource:speech", result.Outputs["out"].Timeline.TimeDomain);
        foreach (
            var timeline in new[]
            {
                new AudioTimeline(StartSeconds: .01),
                new AudioTimeline(ZeroRegions: [new(0, .01)]),
            }
        )
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                new AudioPipelineExecutor()
                    .ExecuteAsync(Graph(speech, rir), new AudioSignal(Tone(16000, 53), timeline))
                    .AsTask()
            );
            Assert.Equal("mix", error.Data["Audio.NodeId"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidTypesAndPortsFailBeforeAnyNodeExecutes(bool rir)
    {
        var speech = Tone(16000, 53);
        var graph = Graph(speech, rir);
        var probe = new Probe();
        foreach (
            var noise in new AudioSignal[]
            {
                new FloatSignal(new(8000, 1, [1d]), new()),
                new ComplexSignal(new(16000, 1, [new(1, 0)]), new()),
            }
        )
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                new AudioPipelineExecutor([probe]).ExecuteAsync(graph, noise).AsTask()
            );
            Assert.Equal("mix", error.Data["Audio.NodeId"]);
        }
        foreach (
            var inputs in new[]
            {
                new Dictionary<string, AudioOutputRef> { ["audio"] = new("speech") },
                new Dictionary<string, AudioOutputRef> { ["noise"] = AudioOutputRef.Source },
                new Dictionary<string, AudioOutputRef>
                {
                    ["audio"] = new("speech"),
                    ["noise"] = AudioOutputRef.Source,
                    ["extra"] = AudioOutputRef.Source,
                },
                new Dictionary<string, AudioOutputRef>
                {
                    ["audio"] = AudioOutputRef.Source,
                    ["noise"] = new("speech"),
                },
            }
        )
        {
            AudioNodeDefinition invalid = rir
                ? new AudioNodeDefinition<ReverbOptions>(
                    "mix",
                    new ReverbProcessor(),
                    new(Room, .05, new(0, -3)),
                    inputs
                )
                : new AudioNodeDefinition<NoiseMixOptions>(
                    "mix",
                    new NoiseMixProcessor(),
                    new(0, -3, .05),
                    inputs
                );
            var altered = graph with
            {
                Nodes = graph.Nodes.SetItem(graph.Nodes.Length - 1, invalid),
            };
            var root =
                inputs.TryGetValue("noise", out var reference)
                && reference == new AudioOutputRef("speech")
                    ? (AudioSignal)new FloatSignal(new(16000, 1, [1d]), new())
                    : new AudioSignal(Tone(16000, 5), new());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new AudioPipelineExecutor([probe]).ExecuteAsync(altered, root).AsTask()
            );
        }
        Assert.Empty(probe.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySilentOutOfRangeAndCancellationCannotProduceOutput(bool rir)
    {
        var speech = Tone(16000, 53);
        foreach (
            var noise in new[]
            {
                new AudioBuffer(16000, 1, []),
                new AudioBuffer(16000, 1, new byte[16]),
            }
        )
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AudioPipelineExecutor().ExecuteAsync(Graph(speech, rir), noise).AsTask()
            );
        int frames =
            speech.Frames + (rir ? new RirGenerator().Generate(Room).Effective.Length - 1 : 0);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new AudioPipelineExecutor()
                .ExecuteAsync(Graph(speech, rir, frames), Tone(16000, 10))
                .AsTask()
        );
        using var cancel = new CancellationTokenSource();
        var probe = new Probe(cancel);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioPipelineExecutor([probe])
                .ExecuteAsync(Graph(speech, rir), Tone(16000, 10), cancel.Token)
                .AsTask()
        );
        Assert.Equal("mix", error.Data["Audio.NodeId"]);
        Assert.Equal(new[] { "speech", "mix" }, probe.Ids);
    }

    [Fact]
    public async Task RirNoiseConfigurationAndPortMustAgree()
    {
        var source = Tone(16000, 53);
        var graph = new AudioPipelineBuilder()
            .Then("room", new ReverbProcessor(), new ReverbOptions(Room, .05))
            .Output("out");
        var result = await new AudioPipelineExecutor().ExecuteAsync(graph.Build(), source);
        Assert.Null(Assert.IsType<ReverbMetadata>(result.Records[0].Metadata).NoiseInput);
        var inputs = new Dictionary<string, AudioOutputRef>
        {
            ["audio"] = AudioOutputRef.Source,
            ["noise"] = AudioOutputRef.Source,
        };
        var extra = new AudioPipelineBuilder()
            .Add(
                new AudioNodeDefinition<ReverbOptions>(
                    "room",
                    new ReverbProcessor(),
                    new(Room, .05),
                    inputs
                )
            )
            .Output("out")
            .Build();
        Assert.Throws<ArgumentException>(() => new AudioPipelineExecutor().Validate(extra, source));
    }

    [Fact]
    public void NoisePreprocessingParametersAndRoutesParticipateInGraphEquivalence()
    {
        AudioPipelineDefinition Build(double cutoff, bool filtered)
        {
            var graph = new AudioPipelineBuilder().Source("noise-source", Tone(16000, 53));
            var raw = graph.Tap();
            graph
                .Then("decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
                .Then(
                    "filter",
                    new ButterworthFilterProcessor(),
                    new ButterworthFilterOptions(cutoff, 4)
                );
            AddMix(graph, "mix", AudioOutputRef.Source, filtered ? graph.Tap() : raw, false, 0);
            return graph.Output("out").Build();
        }
        Assert.True(Build(500, true).EquivalentTo(Build(500, true)));
        Assert.False(Build(500, true).EquivalentTo(Build(700, true)));
        Assert.False(Build(500, true).EquivalentTo(Build(500, false)));
    }

    private static AudioBuffer Tone(int rate, int frames) =>
        Pcm16Codec.Encode(
            [
                Enumerable
                    .Range(0, frames)
                    .Select(i => .3 * Math.Cos(i * 2 * Math.PI * 713 / rate))
                    .ToArray(),
            ],
            rate,
            out _
        );

    private static AudioPipelineDefinition Graph(AudioBuffer speech, bool rir, int offset = 0)
    {
        var graph = new AudioPipelineBuilder().Source("speech", speech).Output("clean");
        AddMix(graph, "mix", graph.Tap(), AudioOutputRef.Source, rir, offset);
        return graph.Output("out").Build();
    }

    private static void AddMix(
        AudioPipelineBuilder graph,
        string id,
        AudioOutputRef speech,
        AudioOutputRef noise,
        bool rir,
        int offset
    )
    {
        var inputs = new Dictionary<string, AudioOutputRef>
        {
            ["audio"] = speech,
            ["noise"] = noise,
        };
        if (rir)
            graph.Add(
                new AudioNodeDefinition<ReverbOptions>(
                    id,
                    new ReverbProcessor(),
                    new(Room, .05, new(offset, -3)),
                    inputs
                )
            );
        else
            graph.Add(
                new AudioNodeDefinition<NoiseMixOptions>(
                    id,
                    new NoiseMixProcessor(),
                    new(offset, -3, .05),
                    inputs
                )
            );
    }

    private static NoiseInputMetadata InputMetadata(AudioPipelineResult result, bool rir)
    {
        var metadata = result.Records.Single(r => r.NodeId == "mix").Metadata;
        return rir
            ? Assert.IsType<ReverbMetadata>(metadata).NoiseInput!
            : Assert.IsType<NoiseMixMetadata>(metadata).NoiseInput;
    }

    private sealed class Probe(CancellationTokenSource? cancel = null) : IAudioExecutionMiddleware
    {
        public List<string> Ids { get; } = [];

        public async ValueTask<AudioProcessResult> InvokeAsync(
            AudioNodeDefinition node,
            AudioExecutionDelegate next,
            CancellationToken token
        )
        {
            Ids.Add(node.Id);
            if (node.Id == "mix")
                cancel?.Cancel();
            return await next();
        }
    }
}
