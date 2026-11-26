using System.Buffers.Binary;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Dsp.Conversion;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Filtering;
using EGGtCSPlatform.Audio.Dsp.IO;
using EGGtCSPlatform.Audio.Dsp.Noise;
using EGGtCSPlatform.Audio.Dsp.Resampling;
using EGGtCSPlatform.Audio.Dsp.Transforms;
using EGGtCSPlatform.Audio.Dsp.Vocoder;

// No training assembly, built-in corpus, native library, or output device is required.
var manual = args.Contains("--manual");
var filterNoise = args.Contains("--filter-noise");
var arguments = args.Where(a => a != "--manual" && a != "--filter-noise").ToArray();
var reference = arguments.Length == 0 ? "clean" : arguments[0];
if (reference is not ("clean" or "vocoded" or "mix") || arguments.Length > 2)
    throw new ArgumentException(
        "Usage: Audio.Sample [clean|vocoded|mix] [input.wav] [--manual] [--filter-noise]"
    );
var input = arguments.Length == 2 ? Pcm16WaveReader.Read(arguments[1]) : Tone(11025, 317);
var pipeline = new AudioPipelineBuilder()
    .Then("resample", new ResampleProcessor(), new(16000))
    .Then("custom-gain", new GainProcessor(), new(.8));
var clean = pipeline.Tap();
var noiseBranch = pipeline.Source("noise-source", Tone(16000, 911));
if (filterNoise)
    noiseBranch
        .Then("noise-rate", new ResampleProcessor(), new ResampleOptions(16000))
        .Then("noise-decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
        .Then(
            "noise-filter",
            new ButterworthFilterProcessor(),
            new ButterworthFilterOptions(700, 4)
        );
var noise = noiseBranch.Tap();
pipeline
    .Add(
        new AudioNodeDefinition<NoiseMixOptions>(
            "noise",
            new NoiseMixProcessor(),
            new(0, -3, .05),
            new Dictionary<string, AudioOutputRef> { ["audio"] = clean, ["noise"] = noise }
        )
    )
    .Then("vocoder", new VocoderProcessor(), new(Bands: 4, Maxima: 3))
    .Output("playback");
var mix = pipeline.Tap();
if (reference == "vocoded")
    pipeline
        .Branch(clean)
        .Then("clean-vocoder", new VocoderProcessor(), new(Bands: 4, Maxima: 3))
        .Output("envelopeSource", alignWith: "playback");
else
    pipeline.Output("envelopeSource", reference == "clean" ? clean : mix, "playback");
var analysis = pipeline.Branch(pipeline.Build().Outputs["envelopeSource"].Source);
if (manual)
    analysis
        .Then("envelope/decode", new PcmDecodeProcessor(), new PcmDecodeOptions())
        .Then("envelope/channels", new ChannelMixProcessor(), new ChannelMixOptions())
        .Then("envelope/filter", new ButterworthFilterProcessor(), new ButterworthFilterOptions())
        .Then("envelope/hilbert", new HilbertTransformProcessor(), new HilbertTransformOptions())
        .Then("envelope/magnitude", new ComplexMagnitudeProcessor(), new ComplexMagnitudeOptions());
else
    // This sample exposes the filtered child port and mirrors the manual filter chain.
    analysis.Then("envelope", new EnvelopeProcessor(), new EnvelopeOptions(EnableFilter: true));
analysis.Output("envelope", alignWith: "playback");

// Tap a child port even when using the composite processor.
pipeline.Output("filtered", new("envelope/filter"));
var result = await new AudioPipelineExecutor().ExecuteAsync(pipeline.Build(), input);
var playback = result.GetAudio("playback"); // Pass this PCM16 buffer to IAudioPlayer.PlayAsync.
var envelope = result.GetFloat("envelope"); // Pass double samples to visualization or stimulation.
Console.WriteLine($"Reference route: {reference}; filtered noise: {filterNoise}");
Console.WriteLine($"Playback SHA256: {playback.ContentHash}");
Console.WriteLine($"Reference SHA256: {result.Outputs["envelopeSource"].Audio.ContentHash}");
Console.WriteLine($"Envelope samples: {envelope.Frames}");
Console.WriteLine($"Executed nodes: {string.Join(", ", result.Records.Select(r => r.NodeId))}");

static AudioBuffer Tone(int sampleRate, int frequency)
{
    var bytes = new byte[sampleRate / 5 * 2];
    for (var i = 0; i < bytes.Length / 2; i++)
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(i * 2),
            (short)(6000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate))
        );
    return new(sampleRate, 1, bytes);
}

public sealed record GainOptions(double Gain);

/// <summary>A consumer-defined processor: registering it means supplying its instance to Then.</summary>
public sealed class GainProcessor : IAudioProcessor<GainOptions>
{
    public string Identity => "sample-gain-1";

    public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        GainOptions options,
        string nodeId
    )
    {
        if (
            !double.IsFinite(options.Gain)
            || options.Gain < 0
            || inputs.Count != 1
            || inputs["audio"].SampleType != SignalSampleType.Pcm16
        )
            throw new ArgumentException("Invalid gain/input.");
        return new Dictionary<string, AudioSignalFormat> { ["audio"] = inputs["audio"] };
    }

    public ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        GainOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var input = inputs["audio"];
        var bytes = input.Audio.ToArray();
        var clipped = false;
        for (var i = 0; i < bytes.Length; i += 2)
        {
            if ((i & 4095) == 0)
                token.ThrowIfCancellationRequested();
            var value = Math.Round(
                BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i)) * options.Gain
            );
            clipped |= value < short.MinValue || value > short.MaxValue;
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan(i),
                (short)Math.Clamp(value, short.MinValue, short.MaxValue)
            );
        }
        return ValueTask.FromResult(
            AudioProcessResult.Single(
                input with
                {
                    Audio = new(input.Audio.SampleRate, input.Audio.Channels, bytes),
                },
                new GainMetadata(clipped)
            )
        );
    }
}

public sealed record GainMetadata(bool IntroducedClipping);
