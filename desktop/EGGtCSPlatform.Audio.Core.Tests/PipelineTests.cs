using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Execution;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using Xunit;

namespace EGGtCSPlatform.Audio.Core.Tests;

public sealed class PipelineTests
{
    private static AudioBuffer Source() => new(16000, 1, new byte[32]);

    [Fact]
    public void CoreAssemblyHasOnlyFrameworkDependencies()
    {
        Assert.All(
            typeof(AudioPipelineExecutor).Assembly.GetReferencedAssemblies(),
            reference =>
                Assert.True(
                    reference.Name!.StartsWith("System.", StringComparison.Ordinal)
                        || reference.Name == "System"
                        || reference.Name == "netstandard"
                )
        );
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("format")]
    [InlineData("alignment")]
    public async Task InvalidProcessorOutputsCannotReachDownstream(string failure)
    {
        var downstream = new Probe();
        var plan = new AudioPipelineBuilder()
            .Then("invalid", new InvalidProcessor(), failure)
            .Then("downstream", downstream, 1)
            .Output("out")
            .Build();
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AudioPipelineExecutor().ExecuteAsync(plan, Source()).AsTask()
        );
        Assert.Equal("invalid", error.Data["Audio.NodeId"]);
        Assert.Equal(0, downstream.Calls);
    }

    private sealed class InvalidProcessor : IAudioProcessor<string>
    {
        public string Identity => "invalid-1";

        public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
            IReadOnlyDictionary<string, AudioSignalFormat> inputs,
            string options,
            string nodeId
        ) => inputs;

        public ValueTask<AudioProcessResult> ProcessAsync(
            IReadOnlyDictionary<string, AudioSignal> inputs,
            string options,
            AudioProcessingContext context,
            CancellationToken token
        )
        {
            if (options == "missing")
                return ValueTask.FromResult(
                    new AudioProcessResult(ImmutableDictionary<string, AudioSignal>.Empty)
                );
            var value = inputs["audio"];
            if (options == "format")
                value = value with { Audio = new AudioBuffer(8000, 1, value.Audio.Data) };
            else
                value = value with { Timeline = value.Timeline with { StartSeconds = double.NaN } };
            return ValueTask.FromResult(AudioProcessResult.Single(value));
        }
    }

    [Fact]
    public async Task SharedNodesExecuteOnceAndBranchesAreImmutable()
    {
        var processor = new Probe();
        var builder = new AudioPipelineBuilder().Then("shared", processor, 7);
        var tap = builder.Tap();
        builder.Branch(tap).Then("branch", processor, 9).Output("playback");
        builder.Output("envelopeSource", tap, "playback");
        var result = await new AudioPipelineExecutor().ExecuteAsync(builder.Build(), Source());
        Assert.Equal(2, processor.Calls);
        Assert.Equal(7, result.Outputs["envelopeSource"].Audio.Data[0]);
        Assert.Equal(9, result.Outputs["playback"].Audio.Data[0]);
        var copy = result.Outputs["envelopeSource"].Audio.ToArray();
        copy[0] = 99;
        Assert.Equal(7, result.Outputs["envelopeSource"].Audio.Data[0]);
        Assert.Equal(2, result.Records.Length);
    }

    [Fact]
    public void BufferSnapshotsItsOwnerAndUsesContentEquality()
    {
        var bytes = new byte[4];
        var a = new AudioBuffer(16000, 1, bytes);
        bytes[0] = 10;
        Assert.Equal(0, a.Data[0]);
        Assert.Equal(new AudioBuffer(16000, 1, new byte[4]), a);
        Assert.NotEqual(new AudioBuffer(8000, 1, new byte[4]), a);
    }

    [Fact]
    public async Task InvalidReferencesAndMissingPortsFailBeforeProcessing()
    {
        var processor = new Probe();
        var builder = new AudioPipelineBuilder();
        builder.Then("one", processor, 1);
        builder.Branch(new("missing")).Then("two", processor, 2).Output("out");
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new AudioPipelineExecutor().ExecuteAsync(builder.Build(), Source()).AsTask()
        );
        Assert.Equal("two", error.Data["Audio.NodeId"]);
        Assert.Equal(0, processor.Calls);
        Assert.Throws<ArgumentException>(() => builder.Then("one", processor, 4));
        var missingPort = new AudioPipelineBuilder()
            .Then("node", processor, 1)
            .Output("out", new("node", "missing"))
            .Build();
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(missingPort, Source())
        );
        var duplicate = new AudioPipelineDefinition(
            [missingPort.Nodes[0], missingPort.Nodes[0]],
            missingPort.Outputs
        );
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(duplicate, Source())
        );
    }

    [Fact]
    public async Task SeparateTimeDomainsRequireExplicitMismatch()
    {
        var processor = new Probe(changesTime: true);
        AudioPipelineDefinition Plan(bool mismatch)
        {
            var b = new AudioPipelineBuilder();
            b.Branch(AudioOutputRef.Source).Then("a", processor, 1).Output("playback");
            b.Branch(AudioOutputRef.Source)
                .Then("b", processor, 1)
                .Output("reference", alignWith: "playback", intentionalMismatch: mismatch);
            return b.Build();
        }
        Assert.Throws<ArgumentException>(() =>
            new AudioPipelineExecutor().Validate(Plan(false), Source())
        );
        var result = await new AudioPipelineExecutor().ExecuteAsync(Plan(true), Source());
        Assert.Equal("b", result.Outputs["reference"].Timeline.TimeDomain);
    }

    [Fact]
    public async Task MiddlewareOrderCancellationAndFailureDiagnosticsArePreserved()
    {
        var log = new List<string>();
        var executor = new AudioPipelineExecutor([new Layer("a", log), new Layer("b", log)]);
        var p = new Probe();
        var plan = new AudioPipelineBuilder().Then("node", p, 1).Output("out").Build();
        await executor.ExecuteAsync(plan, Source());
        Assert.Equal(new[] { "a.enter", "b.enter", "b.exit", "a.exit" }, log);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(plan, Source(), cancellation.Token).AsTask()
        );
        Assert.Equal(1, p.Calls);
        var broken = new AudioPipelineBuilder()
            .Then("broken", new Probe(throws: true), 1)
            .Output("out")
            .Build();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(broken, Source()).AsTask()
        );
        Assert.Equal("broken", error.Data["Audio.NodeId"]);
        Assert.Equal("probe-1", error.Data["Audio.Processor"]);
    }

    [Fact]
    public void EquivalenceIncludesOrderInputsOptionsRoutesAndMismatch()
    {
        AudioPipelineDefinition Plan(int option, bool source = false, bool mismatch = false) =>
            new AudioPipelineBuilder()
                .Then("node", new Probe(), option)
                .Output("playback")
                .Output(
                    "reference",
                    source ? AudioOutputRef.Source : new("node"),
                    "playback",
                    mismatch
                )
                .Build();
        Assert.True(Plan(1).EquivalentTo(Plan(1)));
        Assert.False(Plan(1).EquivalentTo(Plan(2)));
        Assert.False(Plan(1).EquivalentTo(Plan(1, true)));
        Assert.False(Plan(1).EquivalentTo(Plan(1, mismatch: true)));
    }

    private sealed class Probe(bool changesTime = false, bool throws = false) : IAudioProcessor<int>
    {
        public int Calls;
        public string Identity => "probe-1";

        public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
            IReadOnlyDictionary<string, AudioSignalFormat> inputs,
            int options,
            string nodeId
        ) =>
            new Dictionary<string, AudioSignalFormat>
            {
                ["audio"] = inputs["audio"] with
                {
                    TimeDomain = changesTime ? nodeId : inputs["audio"].TimeDomain,
                },
            };

        public ValueTask<AudioProcessResult> ProcessAsync(
            IReadOnlyDictionary<string, AudioSignal> inputs,
            int options,
            AudioProcessingContext context,
            CancellationToken token
        )
        {
            Calls++;
            if (throws)
                throw new InvalidDataException("deliberate");
            var input = inputs["audio"];
            var bytes = input.Audio.ToArray();
            bytes[0] = (byte)options;
            return ValueTask.FromResult(
                AudioProcessResult.Single(
                    new(
                        new(input.Audio.SampleRate, input.Audio.Channels, bytes),
                        input.Timeline with
                        {
                            TimeDomain = changesTime ? context.NodeId : input.Timeline.TimeDomain,
                        }
                    )
                )
            );
        }
    }

    private sealed class Layer(string name, List<string> log) : IAudioExecutionMiddleware
    {
        public async ValueTask<AudioProcessResult> InvokeAsync(
            AudioNodeDefinition node,
            AudioExecutionDelegate next,
            CancellationToken token
        )
        {
            log.Add(name + ".enter");
            var result = await next();
            log.Add(name + ".exit");
            return result;
        }
    }
}
