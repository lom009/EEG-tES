using System.Numerics;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Timing;
using EGGtCSPlatform.Audio.Dsp.Conversion;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Filtering;
using EGGtCSPlatform.Audio.Dsp.IO;
using EGGtCSPlatform.Audio.Dsp.Transforms;
using EGGtCSPlatform.Audio.Dsp.Vocoder;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class EnvelopePipelineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task CompositeEqualsPublicNodesAndKeepsTimeline(int frames)
    {
        var input = Source(frames);
        var original = input.ToArray();
        var timeline = new AudioTimeline("shared-speed", .025, [new(.2, .3)]);
        var options = new EnvelopeOptions(EnableFilter: true);
        var graph = new AudioPipelineBuilder()
            .Then("envelope", new EnvelopeProcessor(), options)
            .Output("envelope")
            .Output("filtered", new("envelope/filter"))
            .Output("analytic", new("envelope/hilbert"));
        var probe = new Probe();
        var result = await new AudioPipelineExecutor([probe]).ExecuteAsync(
            graph.Build(),
            new AudioSignal(input, timeline)
        );
        var manual = new AudioPipelineBuilder()
            .Then("decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
            .Then("channels", new ChannelMixProcessor(), new ChannelMixOptions())
            .Then("filter", new ButterworthFilterProcessor(), new ButterworthFilterOptions())
            .Then("hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
            .Then("magnitude", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions())
            .Output("out");
        var expected = await new AudioPipelineExecutor().ExecuteAsync(manual.Build(), input);
        Assert.Equal(expected.GetFloat("out").ToArray(), result.GetFloat("envelope").ToArray());
        Assert.Equal(
            EnvelopeExtractor.Extract(input, options),
            result.GetFloat("envelope").ToArray()
        );
        Assert.Equal(frames, result.GetComplex("analytic").Frames);
        Assert.Equal(1, result.GetFloat("envelope").Channels);
        Assert.All(result.Outputs.Values, signal => Assert.Same(timeline, signal.Timeline));
        Assert.Equal(original, input.ToArray());
        Assert.Equal(
            new[]
            {
                "envelope/decode",
                "envelope/channels",
                "envelope/filter",
                "envelope/hilbert",
                "envelope/magnitude",
            },
            probe.Ids
        );
        Assert.Equal(5, result.Records.Length); // Three outlets do not execute shared nodes again.
        Assert.Throws<InvalidOperationException>(() => result.GetAudio("envelope"));
    }

    [Fact]
    public async Task SelectionAndDisabledFilterUseOnlyChosenChannel()
    {
        var input = Source(31);
        var options = new EnvelopeOptions(
            EnableFilter: false,
            ChannelMode: ChannelMixMode.Select,
            ChannelIndex: 1
        );
        var graph = new AudioPipelineBuilder()
            .Then("env", new EnvelopeProcessor(), options)
            .Output("out");
        var actual = await new AudioPipelineExecutor().ExecuteAsync(graph.Build(), input);
        var decoded = Pcm16Codec.Decode(input);
        var mono = Pcm16Codec.Encode([decoded[1]], input.SampleRate, out _);
        Assert.Equal(
            EnvelopeExtractor.Extract(mono, new EnvelopeOptions(EnableFilter: false)),
            actual.GetFloat("out").ToArray()
        );
        Assert.Equal(4, actual.Records.Length);
        Assert.DoesNotContain(actual.Records, r => r.NodeId.EndsWith("/filter"));
        Assert.Equal(
            EnvelopeExtractor.Extract(input),
            EnvelopeExtractor.Extract(input, new EnvelopeOptions(ChannelIndex: 999))
        );
        Assert.Throws<ArgumentException>(() =>
            EnvelopeExtractor.Extract(
                input,
                new EnvelopeOptions(ChannelMode: ChannelMixMode.Select, ChannelIndex: 2)
            )
        );
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(8000, 5)]
    [InlineData(double.NaN, 5)]
    [InlineData(150, 0)]
    [InlineData(150, 13)]
    public void InvalidParametersAreRejectedBeforeExecution(double cutoff, int order)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            EnvelopeExtractor.Extract(
                Source(30),
                new EnvelopeOptions(cutoff, order, EnableFilter: true)
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisCannotFeedPcmProcessorAndNothingExecutes(bool complex)
    {
        var graph = new AudioPipelineBuilder().Then(
            "decode",
            new PcmDecodeProcessor(),
            new PcmDecodeOptions()
        );
        if (complex)
            graph.Then("hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions());
        graph.Then("invalid-vocoder", new VocoderProcessor(), new VocoderOptions()).Output("out");
        var probe = new Probe();
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AudioPipelineExecutor([probe]).ExecuteAsync(graph.Build(), Source(32)).AsTask()
        );
        Assert.Equal("invalid-vocoder", error.Data["Audio.NodeId"]);
        Assert.Empty(probe.Ids);
    }

    [Fact]
    public async Task StandaloneFloatAndComplexSourcesAreImmutableAndProcessPerChannel()
    {
        double[] samples = [1, -2, 3, -4, 5, -6];
        var source = new FloatBuffer(16000, 2, samples);
        samples[0] = 999;
        var before = source.ToArray();
        var graph = new AudioPipelineBuilder()
            .Then("filter", new ButterworthFilterProcessor(), new ButterworthFilterOptions(700, 2))
            .Then("hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
            .Output("complex")
            .Then("abs", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions())
            .Output("out");
        var result = await new AudioPipelineExecutor().ExecuteAsync(graph.Build(), source);
        for (var channel = 0; channel < 2; channel++)
        {
            var mono = new FloatBuffer(
                16000,
                1,
                before.Where((_, i) => i % 2 == channel).ToArray()
            );
            var one = await new AudioPipelineExecutor().ExecuteAsync(graph.Build(), mono);
            Assert.Equal(
                one.GetFloat("out").ToArray(),
                result.GetFloat("out").ToArray().Where((_, i) => i % 2 == channel)
            );
        }
        var abs = new AudioPipelineBuilder()
            .Then("abs", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions())
            .Output("out")
            .Build();
        var replay = await new AudioPipelineExecutor().ExecuteAsync(
            abs,
            result.GetComplex("complex")
        );
        Assert.Equal(result.GetFloat("out").ToArray(), replay.GetFloat("out").ToArray());
        Assert.Equal(before, source.ToArray());
        Complex[] complex = [new(3, 4)];
        var immutable = new ComplexBuffer(16000, 1, complex);
        complex[0] = Complex.Zero;
        Assert.Equal(
            5d,
            (await new AudioPipelineExecutor().ExecuteAsync(abs, immutable)).GetFloat("out").Data[0]
        );
    }

    [Fact]
    public async Task ExpandedNodeCancellationStopsFollowingNodes()
    {
        using var cancel = new CancellationTokenSource();
        var probe = new Probe(cancel);
        var graph = new AudioPipelineBuilder()
            .Then("env", new EnvelopeProcessor(), new EnvelopeOptions(EnableFilter: true))
            .Output("out")
            .Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioPipelineExecutor([probe])
                .ExecuteAsync(graph, Source(100), cancel.Token)
                .AsTask()
        );
        Assert.Equal(new[] { "env/decode", "env/channels", "env/filter" }, probe.Ids);
    }

    [Fact]
    public void DuplicateCompositeIsAtomicAndGraphParametersAffectEquivalence()
    {
        var graph = new AudioPipelineBuilder()
            .Then("env", new EnvelopeProcessor(), new EnvelopeOptions(EnableFilter: true))
            .Output("out");
        var before = graph.Build();
        Assert.Throws<ArgumentException>(() =>
            graph.Then("env", new EnvelopeProcessor(), new EnvelopeOptions())
        );
        Assert.True(before.EquivalentTo(graph.Build()));
        var different = new AudioPipelineBuilder()
            .Then("env", new EnvelopeProcessor(), new EnvelopeOptions(300, EnableFilter: true))
            .Output("out")
            .Build();
        Assert.False(before.EquivalentTo(different));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task DefaultAnalysisSkipsFilterAndMatchesPublicHilbertNodes(int frames)
    {
        var input = Source(frames);
        var options = new EnvelopeOptions();
        Assert.False(options.EnableFilter);
        var composite = new AudioPipelineBuilder()
            .Then("env", new EnvelopeProcessor(), options)
            .Output("out")
            .Build();
        var manual = new AudioPipelineBuilder()
            .Then("decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
            .Then("channels", new ChannelMixProcessor(), new ChannelMixOptions())
            .Then("hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
            .Then("magnitude", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions())
            .Output("out")
            .Build();
        var executor = new AudioPipelineExecutor();
        var actual = await executor.ExecuteAsync(composite, input);
        var expected = await executor.ExecuteAsync(manual, input);
        Assert.Equal(expected.GetFloat("out").ToArray(), actual.GetFloat("out").ToArray());
        Assert.Equal(EnvelopeExtractor.Extract(input), actual.GetFloat("out").ToArray());
        Assert.Equal(4, actual.Records.Length);
        Assert.DoesNotContain(actual.Records, r => r.NodeId.EndsWith("/filter"));
    }

    [Fact]
    public async Task ParameterizedFilterAndHilbertMatchIndependentScipyFixtures()
    {
        using var json = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Fixtures",
                    "EnvelopeParametersReference.json"
                )
            )
        );
        foreach (var item in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = Read(item, "input");
            var graph = new AudioPipelineBuilder()
                .Then(
                    "filter",
                    new ButterworthFilterProcessor(),
                    new ButterworthFilterOptions(
                        item.GetProperty("cutoffHz").GetDouble(),
                        item.GetProperty("order").GetInt32()
                    )
                )
                .Output("filtered")
                .Then("hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
                .Then("abs", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions())
                .Output("envelope");
            var result = await new AudioPipelineExecutor().ExecuteAsync(
                graph.Build(),
                new FloatBuffer(item.GetProperty("sampleRate").GetInt32(), 1, input)
            );
            foreach (var name in new[] { "filtered", "envelope" })
            {
                var expected = Read(item, name);
                var actual = result.GetFloat(name).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                Assert.All(
                    expected.Zip(actual),
                    pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1e-9)
                );
            }
        }
    }

    private static double[] Read(JsonElement item, string name) =>
        item.GetProperty(name).EnumerateArray().Select(v => v.GetDouble()).ToArray();

    private static AudioBuffer Source(int frames) =>
        Pcm16Codec.Encode(
            [
                Enumerable.Range(0, frames).Select(i => .3 * Math.Cos(i * .3)).ToArray(),
                Enumerable.Range(0, frames).Select(i => -.2 * Math.Sin(i * .7)).ToArray(),
            ],
            16000,
            out _
        );

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
            if (node.Id.EndsWith("/filter"))
                cancel?.Cancel();
            return await next();
        }
    }
}
