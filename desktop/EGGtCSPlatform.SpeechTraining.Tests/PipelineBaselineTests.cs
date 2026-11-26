using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EGGtCSPlatform.SpeechTraining.Tests;

public sealed class PipelineBaselineTests
{
    [Fact]
    public void AllCombinationsMatchPreMigrationPcmAnalysisAlignmentAndMetadata()
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "GenericApiBaseline.json")
            )
        )!;
        var actual = new Dictionary<string, JsonElement>();
        PipelineBaselineCases.Capture(
            (name, json) => actual[name] = JsonSerializer.Deserialize<JsonElement>(json)
        );
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (name, fields) in expected)
        foreach (var (path, hash) in fields)
        {
            var value = actual[name];
            foreach (var part in path.Split('.'))
                value = value.GetProperty(part);
            var current = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()))
            );
            Assert.True(hash == current, $"{name}: {path} changed");
        }
    }
}
