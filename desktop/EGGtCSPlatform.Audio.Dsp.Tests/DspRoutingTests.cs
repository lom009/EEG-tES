using System.Buffers.Binary;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Resampling;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.TimeStretch;
using EGGtCSPlatform.Audio.Dsp.Vocoder;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class DspRoutingTests
{
    [Fact]
    public void DspAssemblyDoesNotReferenceTrainingUiOrNativePlayback()
    {
        Assert.All(
            typeof(ResampleProcessor).Assembly.GetReferencedAssemblies(),
            reference =>
                Assert.True(
                    reference.Name == "EGGtCSPlatform.Audio.Core"
                        || reference.Name!.StartsWith("System.", StringComparison.Ordinal)
                        || reference.Name == "System"
                        || reference.Name == "netstandard"
                )
        );
    }

    private static AudioBuffer Tone(int rate = 16000, double hz = 317)
    {
        var bytes = new byte[rate / 10 * 2];
        for (var i = 0; i < bytes.Length / 2; i++)
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(i * 2),
                (short)(8000 * Math.Sin(i * 2 * Math.PI * hz / rate))
            );
        return new(rate, 1, bytes);
    }

    [Theory]
    [InlineData(VocoderAlgorithm.ClassicalCis)]
    [InlineData(VocoderAlgorithm.ClassicalTle)]
    [InlineData(VocoderAlgorithm.Ace)]
    [InlineData(VocoderAlgorithm.AceTle)]
    public async Task CleanBranchRemainsIndependentOfNoisyPlayback(VocoderAlgorithm algorithm)
    {
        var input = Tone(22050);
        var b = new AudioPipelineBuilder();
        AddNoise(b, AudioOutputRef.Source, Tone(hz: 900), new(17, -3, .05))
            .Then("resample", new ResampleProcessor(), new(16000))
            .Then("vocoder", new VocoderProcessor(), new(algorithm, 4, Maxima: 3))
            .Output("playback");
        b.Branch(AudioOutputRef.Source)
            .Then("clean-rate", new ResampleProcessor(), new(16000))
            .Then("clean-vocoder", new VocoderProcessor(), new(algorithm, 4, Maxima: 3))
            .Output("envelopeSource", alignWith: "playback");
        var result = await new AudioPipelineExecutor().ExecuteAsync(b.Build(), input);
        var expected = EGGtCSPlatform.Audio.Dsp.Vocoder.Vocoder.Apply(
            SincResampler.Resample(input, 16000),
            new(algorithm, 4, Maxima: 3),
            default,
            out _
        );
        Assert.Equal(expected.ToArray(), result.Outputs["envelopeSource"].Audio.ToArray());
        Assert.NotEqual(expected.ContentHash, result.Outputs["playback"].Audio.ContentHash);
        Assert.Equal(expected.Frames, EnvelopeExtractor.Extract(expected).Length);
    }

    [Fact]
    public async Task SharedStretchAlignsBothOutputsButOriginalReferenceRequiresMismatch()
    {
        var b = new AudioPipelineBuilder().Then("stretch", new TimeStretchProcessor(), new(700));
        var stretched = b.Tap();
        AddNoise(b, stretched, Tone(), new(0, 3, .05)).Output("playback");
        b.Output("reference", stretched, "playback");
        var result = await new AudioPipelineExecutor().ExecuteAsync(b.Build(), Tone());
        Assert.Equal(700, result.Outputs["reference"].Audio.Frames);
        var wrong = new AudioPipelineBuilder()
            .Then("stretch", new TimeStretchProcessor(), new(700))
            .Output("playback")
            .Output("reference", AudioOutputRef.Source, "playback")
            .Build();
        Assert.Throws<ArgumentException>(() => new AudioPipelineExecutor().Validate(wrong, Tone()));
    }

    [Fact]
    public async Task ReverbExposesDryReferenceWithDirectDelayAndTailZeros()
    {
        var settings = RirSettings.Default with { T60 = .15, LengthSeconds = .15 };
        var b = new AudioPipelineBuilder()
            .Then("room", new ReverbProcessor(), new(settings, .05))
            .Output("playback")
            .Output("reference", new("room", "dryReference"), "playback");
        var input = Tone();
        var result = await new AudioPipelineExecutor().ExecuteAsync(b.Build(), input);
        var response = new RirGenerator().Generate(settings);
        var reference = result.Outputs["reference"];
        Assert.Same(input, reference.Audio);
        Assert.Equal(response.DirectFrame / 16000d, reference.Timeline.StartSeconds);
        Assert.Contains(reference.Timeline.Regions, r => r.StartSeconds == 0);
        Assert.Equal(
            input.Frames + response.Effective.Length - 1,
            result.Outputs["playback"].Audio.Frames
        );
        Assert.Equal(
            result.Outputs["playback"].Audio.Duration.TotalSeconds,
            reference.Timeline.Regions[^1].EndSeconds
        );
    }

    [Fact]
    public void VocoderRateAndInvalidParametersFailDuringPlanning()
    {
        var b = new AudioPipelineBuilder()
            .Then("vocoder", new VocoderProcessor(), new())
            .Output("out");
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(b.Build(), Tone(8000))
        );
        var invalid = new AudioPipelineBuilder()
            .Then("resample", new ResampleProcessor(), new(1))
            .Output("out");
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(invalid.Build(), Tone())
        );
    }

    [Fact]
    public void ResourceContentSeedAndOffsetParticipateInEquivalence()
    {
        AudioPipelineDefinition Plan(AudioBuffer noise, int offset, int seed) =>
            AddNoise(new AudioPipelineBuilder(), AudioOutputRef.Source, noise, new(offset, 3, .05))
                .Then("vocoder", new VocoderProcessor(), new(Bands: 4, Maxima: 3, Seed: seed))
                .Output("out")
                .Build();
        Assert.True(Plan(Tone(), 0, 1).EquivalentTo(Plan(Tone(), 0, 1)));
        Assert.False(Plan(Tone(), 0, 1).EquivalentTo(Plan(Tone(hz: 900), 0, 1)));
        Assert.False(Plan(Tone(), 0, 1).EquivalentTo(Plan(Tone(), 1, 1)));
        Assert.False(Plan(Tone(), 0, 1).EquivalentTo(Plan(Tone(), 0, 2)));
    }

    private static AudioPipelineBuilder AddNoise(
        AudioPipelineBuilder graph,
        AudioOutputRef speech,
        AudioBuffer noise,
        NoiseMixOptions options
    )
    {
        var source = graph.Source("noise-source", noise).Tap();
        return graph.Add(
            new AudioNodeDefinition<NoiseMixOptions>(
                "noise",
                new NoiseMixProcessor(),
                options,
                new Dictionary<string, AudioOutputRef> { ["audio"] = speech, ["noise"] = source }
            )
        );
    }
}
