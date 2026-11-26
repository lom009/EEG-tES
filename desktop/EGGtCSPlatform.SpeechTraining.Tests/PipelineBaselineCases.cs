using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Reverb;
using EGGtCSPlatform.Audio.Dsp.Vocoder;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.SpeechTraining.Tests;

// Generates actual outputs only. The expected fixture was captured from frozen pre-migration
// assemblies; tests never regenerate it. See Fixtures/README.md.
public static class PipelineBaselineCases
{
    public static Dictionary<string, string> Capture(Action<string, string>? inspect = null)
    {
        var result = new Dictionary<string, string>();
        var provider = new Noise();
        var preparation = new TrainingAudioPreparationService(
            new FixtureCorpus(),
            noiseProvider: provider
        );
        var sentence = new SpeechSentence("routing-baseline", 1, "今天好。", "");
        var noise = new SpeechNoiseSettings(
            SpeechNoiseKind.Ssn,
            "noise.wav",
            "fixture",
            -3,
            1729,
            .05
        );
        foreach (var rate in new[] { 11025, 16000 })
        foreach (var channels in new[] { 1, 2 })
        {
            var source = Tone(rate, channels);
            var configurations = new Dictionary<string, TrainingAudioSettings>
            {
                ["original"] = new(),
                ["fixed"] = new(SpeechRate: 5),
                ["adaptive"] = new(Mode: SpeechTrainingMode.AdaptiveRate),
                ["noise"] = new(Mode: SpeechTrainingMode.FixedSnr, SnrDb: -3),
            };
            foreach (var algorithm in Enum.GetValues<VocoderAlgorithm>())
            {
                var config = new TrainingAudioSettings(
                    Mode: SpeechTrainingMode.FixedSnr,
                    SnrDb: -3,
                    Vocoder: new(algorithm, 4, Maxima: 3)
                );
                configurations[$"noise-{algorithm}"] = config;
                configurations[$"clean-vocoded-{algorithm}"] = config;
            }
            foreach (var addNoise in new[] { false, true })
            {
                var room = new TrainingRoomSettings(
                    RirSettings.Default with
                    {
                        T60 = .15,
                        LengthSeconds = .15,
                    },
                    addNoise,
                    SnrDb: -3
                );
                var config = new TrainingAudioSettings(
                    Mode: SpeechTrainingMode.FixedRir,
                    Room: room
                );
                configurations[$"rir-{addNoise}"] = config;
                configurations[$"rir-vocoder-{addNoise}"] = config with
                {
                    Vocoder = new(Bands: 4, Maxima: 3),
                };
            }
            foreach (var (name, settings) in configurations)
            {
                // The frozen pre-migration baseline used the low-pass filter. Keep that
                // configuration explicit now that production defaults to unfiltered analysis.
                var config = settings with { Envelope = new(EnableFilter: true) };
                var request = new TrainingAudioRequest(
                    sentence,
                    config,
                    source,
                    1.3,
                    1729,
                    config.Mode == SpeechTrainingMode.FixedSnr || config.Room?.AddNoise == true
                        ? noise
                        : null,
                    config.Mode == SpeechTrainingMode.FixedSnr || config.Room?.AddNoise == true
                        ? provider.Read("noise.wav")
                        : null,
                    .05
                );
                var prepared = preparation.Prepare(request);
                if (name.StartsWith("clean-vocoded-"))
                {
                    // Explicit generic routing: vocode the CLEAN branch, not the playback mix.
                    var input = prepared.Pipeline.Outputs["envelopeSource"].Source;
                    var builder = new AudioPipelineBuilder();
                    foreach (
                        var existing in prepared.Pipeline.Nodes.Where(n =>
                            !n.Id.StartsWith("envelope/")
                        )
                    )
                        builder.Add(existing);
                    var branch = builder
                        .Branch(input)
                        .Then("clean-vocoder", new VocoderProcessor(), config.Vocoder!)
                        .Output("envelopeSource", alignWith: "playback");
                    branch
                        .Then("envelope", new EnvelopeProcessor(), config.Envelope)
                        .Output("envelope", alignWith: "playback");
                    builder.Output("playback", prepared.Pipeline.Outputs["playback"].Source);
                    var graph = builder.Build();
                    prepared = prepared with
                    {
                        Pipeline = graph,
                        Graph = new AudioPipelineExecutor()
                            .ExecuteAsync(graph, source)
                            .AsTask()
                            .GetAwaiter()
                            .GetResult(),
                    };
                }
                var waveform = SpeechWaveformData.Prepare(prepared);
                var snapshot = new
                {
                    Playback = Convert.ToHexString(SHA256.HashData(prepared.Playback.ToArray())),
                    Reference = Convert.ToHexString(
                        SHA256.HashData(prepared.EnvelopeSource.Audio.ToArray())
                    ),
                    prepared.Playback.SampleRate,
                    prepared.Playback.Channels,
                    ReferenceRate = prepared.EnvelopeSource.Audio.SampleRate,
                    Alignment = new
                    {
                        prepared.EnvelopeSource.Timeline.StartSeconds,
                        ZeroRegions = prepared.EnvelopeSource.Timeline.Regions,
                    },
                    prepared.TimeStretch,
                    prepared.Noise,
                    prepared.Rir,
                    FullEnvelope = Convert.ToHexString(
                        SHA256.HashData(
                            System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                                prepared.Envelope.ToArray().AsSpan()
                            )
                        )
                    ),
                    waveform.Voice,
                    waveform.Envelope,
                    waveform.Stimulus,
                };
                var canonical = CanonicalJson(snapshot);
                inspect?.Invoke(
                    $"{rate}-{channels}-{name}",
                    System.Text.Encoding.UTF8.GetString(canonical)
                );
                result[$"{rate}-{channels}-{name}"] = Convert.ToHexString(
                    SHA256.HashData(canonical)
                );
            }
        }
        return result;
    }

    private static byte[] CanonicalJson(object snapshot)
    {
        // ImmutableDictionary traversal order varies between processes. Sort object keys,
        // retaining the original JSON number representations and all array/sample ordering.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            void Write(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (
                        var p in value
                            .EnumerateObject()
                            .OrderBy(p => p.Name, StringComparer.Ordinal)
                    )
                    {
                        writer.WritePropertyName(p.Name);
                        Write(p.Value);
                    }
                    writer.WriteEndObject();
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                        Write(item);
                    writer.WriteEndArray();
                }
                else
                    value.WriteTo(writer);
            }
            Write(JsonSerializer.SerializeToElement(snapshot));
        }
        return stream.ToArray();
    }

    private static AudioBuffer Tone(int rate, int channels)
    {
        var bytes = new byte[(rate / 20 + 17) * channels * 2];
        for (var i = 0; i < bytes.Length / (channels * 2); i++)
        for (var c = 0; c < channels; c++)
            BinaryPrimitives.WriteInt16LittleEndian(
                bytes.AsSpan((i * channels + c) * 2),
                (short)(6000 * Math.Sin(i * 2 * Math.PI * (317 + c * 97) / rate))
            );
        return new(rate, channels, bytes);
    }

    private sealed class FixtureCorpus : ISpeechCorpusProvider
    {
        public IReadOnlyList<SpeechCorpus> Corpora => [];

        public AudioBuffer ReadAudio(SpeechSentence sentence) => throw new NotSupportedException();
    }

    private sealed class Noise : ISpeechNoiseProvider
    {
        public double CalibrationRms => .05;

        public SpeechNoiseResource Read(string fileName) => new(Tone(22050, 2), "fixture");
    }
}
