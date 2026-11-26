using System.Numerics;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using Xunit;

namespace EGGtCSPlatform.Audio.Core.Tests;

public sealed class SignalTypeTests
{
    [Fact]
    public async Task ExecutorRejectsUndeclaredSampleType()
    {
        var graph = new AudioPipelineBuilder()
            .Then("wrong-type", new WrongType(), 0)
            .Output("out")
            .Build();
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AudioPipelineExecutor()
                .ExecuteAsync(graph, new AudioBuffer(16000, 1, [0, 0]))
                .AsTask()
        );
        Assert.Equal("wrong-type", error.Data["Audio.NodeId"]);
        Assert.Equal("wrong-type-1", error.Data["Audio.Processor"]);
    }

    [Fact]
    public async Task TypedSourcesAndOutletsRetainFormatWithoutConversion()
    {
        var graph = new AudioPipelineBuilder().Output("source").Build();
        var executor = new AudioPipelineExecutor();
        var real = new FloatBuffer(11025, 2, [1, 2, 3, 4]);
        var complex = new ComplexBuffer(16000, 1, [new(3, 4)]);
        Assert.Same(real, (await executor.ExecuteAsync(graph, real)).GetFloat("source"));
        Assert.Contains(
            "Float64",
            (await executor.ExecuteAsync(graph, real)).Outputs["source"].ToString()
        );
        Assert.Same(complex, (await executor.ExecuteAsync(graph, complex)).GetComplex("source"));
        Assert.Equal(
            SignalSampleType.Float64,
            executor.Validate(graph, real).Single().Value.SampleType
        );
        Assert.Equal(
            SignalSampleType.Complex128,
            executor.Validate(graph, complex).Single().Value.SampleType
        );
        Assert.Throws<ArgumentException>(() => new FloatBuffer(16000, 2, [1]));
        Assert.Throws<ArgumentException>(() => new FloatBuffer(16000, 1, [double.NaN]));
        Assert.Throws<ArgumentException>(() =>
            new ComplexBuffer(16000, 1, [new(1, double.PositiveInfinity)])
        );
    }

    private sealed class WrongType : SingleInputAudioProcessor<int>
    {
        public override string Identity => "wrong-type-1";

        protected override AudioSignalFormat Describe(
            AudioSignalFormat input,
            int options,
            string nodeId
        ) => input;

        protected override AudioProcessResult Process(
            AudioSignal input,
            int options,
            AudioProcessingContext context,
            CancellationToken token
        ) => AudioProcessResult.Single(new FloatSignal(new(16000, 1, [1d]), input.Timeline));
    }
}
