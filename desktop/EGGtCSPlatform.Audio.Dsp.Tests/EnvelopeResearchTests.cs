using System.Text.Json;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.IO;
using Xunit;

namespace EGGtCSPlatform.Audio.Dsp.Tests;

public sealed class EnvelopeResearchTests
{
    [Fact]
    public void FilterAndHilbertMatchUnchangedScipyReference()
    {
        using var data = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "SpeechEnvelopeReference.json")
            )
        );
        foreach (var item in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = item.GetProperty("pcm")
                .EnumerateArray()
                .Select(x => x.GetInt16() / 32768d)
                .ToArray();
            int rate = item.GetProperty("sampleRate").GetInt32();
            var filtered = EnvelopeExtractor.Filter(input, rate);
            var envelope = EnvelopeExtractor.Extract(
                Pcm16Codec.Encode([input], rate, out _),
                new EnvelopeOptions(EnableFilter: true)
            );
            foreach (
                var (field, actual) in new[] { ("filtered", filtered), ("envelope", envelope) }
            )
            {
                var expected = item.GetProperty(field)
                    .EnumerateArray()
                    .Select(x => x.GetDouble())
                    .ToArray();
                Assert.Equal(expected.Length, actual.Length);
                Assert.InRange(
                    expected.Zip(actual).Max(p => Math.Abs(p.First - p.Second)),
                    0,
                    1e-5
                );
            }
        }
    }
}
