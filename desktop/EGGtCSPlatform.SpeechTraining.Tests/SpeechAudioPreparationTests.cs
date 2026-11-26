using System.Buffers.Binary;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class SpeechAudioPreparationTests
{
    internal static AudioBuffer Tone(
        int rate = 16000,
        int frames = 16000,
        int channels = 1,
        double frequency = 200
    )
    {
        var data = new byte[frames * channels * 2];
        for (var i = 0; i < frames; i++)
        for (var c = 0; c < channels; c++)
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan((i * channels + c) * 2, 2),
                (short)((c == 0 ? 1 : -1) * 12000 * Math.Sin(2 * Math.PI * frequency * i / rate))
            );
        return new(rate, channels, data);
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void StretchPreservesPitchChannelsAndExactDuration(int sampleRate)
    {
        var original = Tone(sampleRate, sampleRate, 2);
        foreach (var scale in new[] { 0.5, 2d })
        {
            var frames = (int)(sampleRate * scale);
            var output = Solafs.Stretch(original, frames);
            Assert.Equal(sampleRate, output.SampleRate);
            Assert.Equal(frames * 4, output.Data.Length);
            var crossings = 0;
            var from = frames / 5;
            var to = frames * 4 / 5;
            for (var i = 1; i < frames; i++)
            {
                var a = BinaryPrimitives.ReadInt16LittleEndian(output.Data.Slice(i * 4, 2));
                var b = BinaryPrimitives.ReadInt16LittleEndian(output.Data.Slice(i * 4 + 2, 2));
                Assert.Equal(-a, b);
                var previous = BinaryPrimitives.ReadInt16LittleEndian(
                    output.Data.Slice((i - 1) * 4, 2)
                );
                if (i >= from && i < to && previous <= 0 && a > 0)
                    crossings++;
            }
            var frequency = (double)crossings * sampleRate / (to - from);
            Assert.InRange(frequency, 196, 204);
            Assert.Contains(
                output.ToArray().TakeLast(Math.Max(8, sampleRate / 100)).ToArray(),
                x => x != 0
            );
        }
    }

    [Fact]
    public void OriginalSpeedShortSilenceAndTargetRateAreHandled()
    {
        var original = Tone();
        Assert.Same(original, TestAudio.Prepare(original).Playback);
        for (var rate = 1; rate <= 5; rate++)
        {
            var result = TestAudio.Prepare(original, new(SpeechRate: rate));
            Assert.Same(result.Playback, result.EnvelopeSource.Audio);
            Assert.Equal(
                (int)Math.Round(16000d * 3 / rate, MidpointRounding.AwayFromZero) * 2,
                result.Playback.Data.Length
            );
        }
        foreach (var length in new[] { 1, 2, 79, 80, 201 })
        foreach (var target in new[] { 1, 2, 99, 401 })
        {
            var output = Solafs.Stretch(new(16000, 1, new byte[length * 2]), target);
            Assert.Equal(target * 2, output.Data.Length);
            Assert.All(output.ToArray(), x => Assert.Equal(0, x));
        }
        var single = Solafs.Stretch(new(16000, 1, new byte[] { 1, 0 }), 8);
        Assert.Equal(
            Enumerable.Repeat(new byte[] { 1, 0 }, 8).SelectMany(x => x),
            single.ToArray()
        );
        Assert.Throws<OperationCanceledException>(() => Solafs.Stretch(original, 20000, new(true)));
    }
}
