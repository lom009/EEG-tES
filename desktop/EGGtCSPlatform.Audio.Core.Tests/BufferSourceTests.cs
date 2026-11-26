using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using Xunit;

namespace EGGtCSPlatform.Audio.Core.Tests;

public sealed class BufferSourceTests
{
    [Fact]
    public async Task SourcePublishesLoadedBufferAndContentParticipatesInEquivalence()
    {
        var buffer = new AudioBuffer(16000, 1, [1, 0, 2, 0]);
        AudioPipelineDefinition Build(AudioBuffer audio) =>
            new AudioPipelineBuilder()
                .Source("noise", audio)
                .Output("first")
                .Output("second")
                .Build();
        var graph = Build(buffer);
        var result = await new AudioPipelineExecutor().ExecuteAsync(
            graph,
            new AudioBuffer(8000, 1, [])
        );
        Assert.Same(buffer, result.GetAudio("first"));
        Assert.Same(result.Outputs["first"], result.Outputs["second"]);
        Assert.Single(result.Records);
        Assert.Equal("resource:noise", result.Outputs["first"].Timeline.TimeDomain);
        var metadata = Assert.IsType<AudioBufferSourceMetadata>(result.Records[0].Metadata);
        Assert.Equal(buffer.ContentHash, metadata.ContentHash);
        Assert.True(graph.EquivalentTo(Build(new(16000, 1, buffer.Data))));
        Assert.False(graph.EquivalentTo(Build(new(8000, 1, buffer.Data))));
        Assert.False(graph.EquivalentTo(Build(new(16000, 1, [3, 0, 2, 0]))));
        var invalid = new AudioPipelineBuilder()
            .Then("source", new AudioBufferSourceProcessor(), new AudioBufferSourceOptions(buffer))
            .Output("out")
            .Build();
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(invalid, buffer)
        );
    }
}
