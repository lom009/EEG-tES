using System.Collections.Immutable;
using System.Diagnostics;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Core.Timing;

namespace EGGtCSPlatform.Audio.Core.Execution;

public delegate ValueTask<AudioProcessResult> AudioExecutionDelegate();

public interface IAudioExecutionMiddleware
{
    ValueTask<AudioProcessResult> InvokeAsync(
        AudioNodeDefinition node,
        AudioExecutionDelegate next,
        CancellationToken token
    );
}

public sealed record AudioExecutionRecord(
    string NodeId,
    string ProcessorIdentity,
    TimeSpan Elapsed,
    object? Metadata
);

public sealed record AudioPipelineResult(
    ImmutableDictionary<string, AudioSignal> Outputs,
    ImmutableArray<AudioExecutionRecord> Records
)
{
    public AudioBuffer GetAudio(string name) => Outputs[name].GetAudio();

    public FloatBuffer GetFloat(string name) => Outputs[name].GetFloat();

    public ComplexBuffer GetComplex(string name) => Outputs[name].GetComplex();
}

public sealed class AudioPipelineExecutor(IEnumerable<IAudioExecutionMiddleware>? middleware = null)
{
    private readonly IAudioExecutionMiddleware[] _middleware = middleware?.ToArray() ?? [];

    public IReadOnlyDictionary<AudioOutputRef, AudioSignalFormat> Validate(
        AudioPipelineDefinition definition,
        AudioBuffer source
    ) => Validate(definition, new AudioSignal(source, new AudioTimeline()));

    public IReadOnlyDictionary<AudioOutputRef, AudioSignalFormat> Validate(
        AudioPipelineDefinition definition,
        FloatBuffer source
    ) => Validate(definition, new FloatSignal(source, new AudioTimeline()));

    public IReadOnlyDictionary<AudioOutputRef, AudioSignalFormat> Validate(
        AudioPipelineDefinition definition,
        ComplexBuffer source
    ) => Validate(definition, new ComplexSignal(source, new AudioTimeline()));

    public IReadOnlyDictionary<AudioOutputRef, AudioSignalFormat> Validate(
        AudioPipelineDefinition definition,
        AudioSignal source
    )
    {
        source.Timeline.Validate();
        var formats = new Dictionary<AudioOutputRef, AudioSignalFormat>
        {
            [AudioOutputRef.Source] = new(
                source.Buffer.SampleRate,
                source.Buffer.Channels,
                source.Timeline.TimeDomain,
                source.Buffer.SampleType
            ),
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            if (!ids.Add(node.Id))
                throw new ArgumentException($"重复节点：{node.Id}");
            try
            {
                var inputs = node.Inputs.ToDictionary(
                    p => p.Key,
                    p =>
                        formats.TryGetValue(p.Value, out var value)
                            ? value
                            : throw new ArgumentException($"节点输入不存在或是前向引用：{p.Value}")
                );
                var outputs = node.Describe(inputs);
                foreach (var (port, format) in outputs)
                {
                    if (
                        string.IsNullOrWhiteSpace(port)
                        || format.SampleRate is < 8000 or > 192000
                        || format.Channels is < 1 or > 2
                        || !Enum.IsDefined(format.SampleType)
                        || string.IsNullOrWhiteSpace(format.TimeDomain)
                    )
                        throw new ArgumentException("处理器声明了无效的输出格式。");
                    formats.Add(new(node.Id, port), format);
                }
            }
            catch (Exception e)
            {
                Annotate(e, node);
                throw;
            }
        }
        foreach (var (name, route) in definition.Outputs)
        {
            if (string.IsNullOrWhiteSpace(name) || !formats.ContainsKey(route.Source))
                throw new ArgumentException($"出口引用不存在：{name}");
            if (route.AlignWith is { } target)
            {
                if (
                    !definition.Outputs.TryGetValue(target, out var other)
                    || !formats.TryGetValue(other.Source, out var otherFormat)
                )
                    throw new ArgumentException($"对齐目标不存在：{target}");
                if (
                    !route.IntentionalMismatch
                    && formats[route.Source].TimeDomain != otherFormat.TimeDomain
                )
                    throw new ArgumentException(
                        $"出口 {name} 与 {target} 的时间域不同，必须显式允许错配。"
                    );
            }
        }
        return formats;
    }

    public ValueTask<AudioPipelineResult> ExecuteAsync(
        AudioPipelineDefinition definition,
        AudioBuffer source,
        CancellationToken token = default
    ) => ExecuteAsync(definition, new AudioSignal(source, new AudioTimeline()), token);

    public ValueTask<AudioPipelineResult> ExecuteAsync(
        AudioPipelineDefinition definition,
        FloatBuffer source,
        CancellationToken token = default
    ) => ExecuteAsync(definition, new FloatSignal(source, new AudioTimeline()), token);

    public ValueTask<AudioPipelineResult> ExecuteAsync(
        AudioPipelineDefinition definition,
        ComplexBuffer source,
        CancellationToken token = default
    ) => ExecuteAsync(definition, new ComplexSignal(source, new AudioTimeline()), token);

    public async ValueTask<AudioPipelineResult> ExecuteAsync(
        AudioPipelineDefinition definition,
        AudioSignal source,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        var formats = Validate(definition, source);
        var values = new Dictionary<AudioOutputRef, AudioSignal>
        {
            [AudioOutputRef.Source] = source,
        };
        var uses = definition
            .Nodes.SelectMany(n => n.Inputs.Values)
            .Concat(definition.Outputs.Values.Select(r => r.Source))
            .GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count());
        var records = ImmutableArray.CreateBuilder<AudioExecutionRecord>();
        foreach (var node in definition.Nodes)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var inputs = node.Inputs.ToImmutableDictionary(p => p.Key, p => values[p.Value]);
                AudioExecutionDelegate next = () => node.ExecuteAsync(inputs, token);
                foreach (var layer in _middleware.Reverse())
                {
                    var inner = next;
                    next = () => layer.InvokeAsync(node, inner, token);
                }
                var start = Stopwatch.GetTimestamp();
                var result = await next().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                foreach (var expected in formats.Keys.Where(r => r.NodeId == node.Id))
                    if (!result.Outputs.ContainsKey(expected.Port))
                        throw new ArgumentException($"处理器未提供声明的输出：{expected.Port}");
                foreach (var (port, signal) in result.Outputs)
                {
                    signal.Timeline.Validate();
                    var key = new AudioOutputRef(node.Id, port);
                    if (
                        !formats.TryGetValue(key, out var expected)
                        || (
                            expected.SampleRate != signal.Buffer.SampleRate
                            || expected.Channels != signal.Buffer.Channels
                        )
                        || expected.SampleType != signal.Buffer.SampleType
                        || expected.TimeDomain != signal.Timeline.TimeDomain
                    )
                        throw new ArgumentException($"输出 {port} 与声明的格式或时间域不符。");
                    if (uses.ContainsKey(key))
                        values.Add(key, signal);
                }
                records.Add(
                    new(
                        node.Id,
                        node.ProcessorIdentity,
                        Stopwatch.GetElapsedTime(start),
                        result.Metadata
                    )
                );
                foreach (var input in node.Inputs.Values)
                    if (--uses[input] == 0)
                        values.Remove(input);
            }
            catch (Exception e)
            {
                Annotate(e, node);
                throw;
            }
        }
        var outputs = definition.Outputs.ToImmutableDictionary(
            p => p.Key,
            p => values[p.Value.Source]
        );
        return new(outputs, records.ToImmutable());
    }

    private static void Annotate(Exception error, AudioNodeDefinition node)
    {
        error.Data["Audio.NodeId"] = node.Id;
        error.Data["Audio.Processor"] = node.ProcessorIdentity;
    }
}
