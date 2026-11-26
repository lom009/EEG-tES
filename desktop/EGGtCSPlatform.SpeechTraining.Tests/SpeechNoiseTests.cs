using System.Security.Cryptography;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechNoiseTests
{
    [Theory]
    [InlineData(-10, 2000)]
    [InlineData(0, 8000)]
    [InlineData(20, 8000)]
    [InlineData(10, 8000)]
    [InlineData(30, 8000)]
    public void MixMatchesIndependentRmsFormulaAndKeepsCleanReference(double snr, int noiseLength)
    {
        var provider = new AdaptiveSnrTests.NoiseProvider
        {
            Audio = AdaptiveSnrTests.Tone(noiseLength, frequency: 700),
        };
        var audio = AdaptiveSnrTests.Tone(3200);
        var settings = new SpeechNoiseSettings(
            SpeechNoiseKind.Ssn,
            "ssn.wav",
            "test-hash",
            snr,
            123,
            .03
        );
        var sentence = new SpeechSentence("test/1", 1, "测试", "1.wav");

        var result = TestAudio.Prepare(
            audio,
            new(Mode: SpeechTrainingMode.FixedSnr),
            sentence,
            noise: settings,
            provider: provider
        );
        Assert.Same(audio, result.EnvelopeSource.Audio);
        Assert.Equal(audio.Duration, result.Playback.Duration);
        var x = Pcm16Codec.Decode(audio)[0];
        var rawNoise = Pcm16Codec.Decode(provider.Audio)[0];
        var n = Enumerable
            .Range(0, x.Length)
            .Select(i =>
            {
                var j = (i + result.Noise!.OffsetFrames) % x.Length;
                return j < rawNoise.Length ? rawNoise[j] : 0d;
            })
            .ToArray();
        double Power(double[] a) => a.Sum(v => v * v) / a.Length;
        var gain = Math.Sqrt(Power(n) / Power(x)) * Math.Pow(10, snr / 20);
        Assert.Equal(snr, 10 * Math.Log10(Power(x) * gain * gain / Power(n)), 10);
        var expected = x.Select((v, i) => v * gain + n[i]).ToArray();
        var factor = .03 / Math.Sqrt(Power(expected));
        var actual = Pcm16Codec.Decode(result.Playback)[0];
        for (var i = 0; i < x.Length; i++)
            Assert.InRange(Math.Abs(actual[i] - expected[i] * factor), 0, 1d / 32768);
        Assert.InRange(result.Noise!.OutputRms, .03 - 1d / 32768, .03 + 1d / 32768);
        Assert.Equal(1, result.Noise.PeakGain);
        var again = TestAudio.Prepare(
            audio,
            new(Mode: SpeechTrainingMode.FixedSnr),
            sentence,
            noise: settings,
            provider: provider
        );
        Assert.Equal(result.Playback.ToArray(), again.Playback.ToArray());
        Assert.Equal(result.Noise, again.Noise);
        var changed = TestAudio.Prepare(
            audio,
            new(Mode: SpeechTrainingMode.FixedSnr),
            sentence,
            noise: settings with
            {
                Seed = 124,
            },
            provider: provider
        );
        Assert.NotEqual(result.Request, changed.Request);
    }

    [Theory]
    [InlineData(8000, 1, 2)]
    [InlineData(48000, 2, 1)]
    public void NoiseMatchesRateAndChannelsWithoutChangingReference(
        int rate,
        int targetChannels,
        int noiseChannels
    )
    {
        var provider = new AdaptiveSnrTests.NoiseProvider
        {
            Audio = AdaptiveSnrTests.Tone(3200, 16000, noiseChannels),
        };
        var audio = AdaptiveSnrTests.Tone(rate / 10, rate, targetChannels);
        var settings = new SpeechNoiseSettings(
            SpeechNoiseKind.Babble,
            "noise.wav",
            "test-hash",
            0,
            4,
            .03
        );
        var result = TestAudio.Prepare(
            audio,
            new(Mode: SpeechTrainingMode.FixedSnr),
            new("id", 1, "测试", ""),
            noise: settings,
            provider: provider
        );
        Assert.Equal(rate, result.Playback.SampleRate);
        Assert.Equal(targetChannels, result.Playback.Channels);
        Assert.Equal(audio.Data.Length, result.Playback.Data.Length);
        Assert.Same(audio, result.EnvelopeSource.Audio);
    }

    [Fact]
    public void PeakProtectionPreventsClippingAndRejectsInvalidAudioAndHash()
    {
        var provider = new AdaptiveSnrTests.NoiseProvider();
        var settings = new SpeechNoiseSettings(
            SpeechNoiseKind.Ssn,
            "noise.wav",
            "test-hash",
            10,
            3,
            2
        );

        var sentence = new SpeechSentence("id", 1, "测试", "");
        TrainingAudioPreparationResult Prepare(AudioBuffer audio, SpeechNoiseSettings? n = null) =>
            TestAudio.Prepare(
                audio,
                new(Mode: SpeechTrainingMode.FixedSnr),
                sentence,
                noise: n ?? settings,
                provider: provider
            );
        var result = Prepare(AdaptiveSnrTests.Tone());
        Assert.InRange(result.Noise!.PeakGain, 0.001, .99);
        Assert.All(
            Pcm16Codec.Decode(result.Playback).SelectMany(x => x),
            v => Assert.InRange(v, -1, 1)
        );
        Assert.Throws<InvalidDataException>(() => Prepare(new(16000, 1, new byte[6400])));
        Assert.Throws<InvalidDataException>(() => Prepare(new(16000, 1, [])));
        Assert.Throws<InvalidDataException>(() =>
            Prepare(AdaptiveSnrTests.Tone(), settings with { Sha256 = "changed" })
        );
        provider.Audio = new(16000, 1, new byte[6400]);
        Assert.Throws<InvalidDataException>(() => Prepare(AdaptiveSnrTests.Tone()));
    }

    [Theory]
    [InlineData(VocoderAlgorithm.ClassicalCis)]
    [InlineData(VocoderAlgorithm.Ace)]
    [InlineData(VocoderAlgorithm.ClassicalTle)]
    [InlineData(VocoderAlgorithm.AceTle)]
    public void VocoderFollowsNoiseAndRetainsMetadata(VocoderAlgorithm algorithm)
    {
        var provider = new AdaptiveSnrTests.NoiseProvider();
        var settings = new SpeechNoiseSettings(
            SpeechNoiseKind.Ssn,
            "noise.wav",
            "test-hash",
            10,
            3,
            .03
        );
        var original = AdaptiveSnrTests.Tone();
        var result = TestAudio.Prepare(
            original,
            new(Mode: SpeechTrainingMode.FixedSnr, Vocoder: new(algorithm, 4, Maxima: 2)),
            new("id", 1, "测试", ""),
            noise: settings,
            provider: provider
        );
        Assert.Equal(settings, result.Noise!.Settings);
        Assert.Equal(
            new[]
            {
                "noise-source",
                "noise",
                "playback-rate",
                "reference-rate",
                "vocoder",
                "envelope/decode",
                "envelope/channels",
                "envelope/hilbert",
                "envelope/magnitude",
            },
            result.Graph.Records.Select(r => r.NodeId)
        );
        Assert.Equal(original.ToArray(), result.EnvelopeSource.Audio.ToArray());
        Assert.NotEqual(original.ToArray(), result.Playback.ToArray());
    }

    [Fact]
    public void BuiltinResourcesMatchHashesAndMappingsAndCanRunFromCopiedDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "snr-resources-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            foreach (
                var line in File.ReadAllLines(
                        Path.Combine(AppContext.BaseDirectory, "SpeechNoiseHashes.csv")
                    )
                    .Skip(1)
            )
            {
                var parts = line.Split(',');
                var path = Path.Combine(AppContext.BaseDirectory, "SpeechNoises", parts[0]);
                Assert.Equal(
                    parts[1],
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                );
                File.Copy(path, Path.Combine(root, parts[0]));
            }
            var provider = new FileSpeechNoiseProvider(root);
            Assert.InRange(provider.CalibrationRms, .001, .5);
            var corpora = new FileSpeechCorpusProvider();
            foreach (var corpus in corpora.Corpora)
            foreach (var kind in Enum.GetValues<SpeechNoiseKind>())
            {
                var name = corpus.SnrNoise!.File(kind)!;
                var resource = provider.Read(name);
                var settings = new SpeechNoiseSettings(
                    kind,
                    name,
                    resource.Sha256,
                    10,
                    0,
                    provider.CalibrationRms
                );
                var result = TestAudio.Prepare(
                    corpora.ReadAudio(corpus.Sentences[0]),
                    new(Mode: SpeechTrainingMode.FixedSnr),
                    corpus.Sentences[0],
                    noise: settings,
                    provider: provider
                );
                Assert.Equal(settings, result.Noise!.Settings);
            }
            Assert.Throws<ArgumentException>(() => provider.Read("../escape.wav"));
            Assert.Throws<FileNotFoundException>(() => provider.Read("missing.wav"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
