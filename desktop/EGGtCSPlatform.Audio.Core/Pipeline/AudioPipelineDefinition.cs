using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Core.Routing;

namespace EGGtCSPlatform.Audio.Core.Pipeline;

public sealed record AudioPipelineDefinition(
    ImmutableArray<AudioNodeDefinition> Nodes,
    ImmutableDictionary<string, AudioRoute> Outputs
)
{
    public bool EquivalentTo(AudioPipelineDefinition other) =>
        Nodes.Length == other.Nodes.Length
        && Nodes.Zip(other.Nodes).All(p => p.First.EquivalentTo(p.Second))
        && Outputs.Count == other.Outputs.Count
        && Outputs.All(p => other.Outputs.TryGetValue(p.Key, out var route) && route == p.Value);
}

/// <summary>Branches share nodes, not mutable audio. Branch creates a cursor on the same plan.</summary>
public sealed class AudioPipelineBuilder
{
    private readonly List<AudioNodeDefinition> _nodes;
    private readonly Dictionary<string, AudioRoute> _outputs;
    private AudioOutputRef _current;

    public AudioPipelineBuilder()
        : this([], new(StringComparer.Ordinal), AudioOutputRef.Source) { }

    private AudioPipelineBuilder(
        List<AudioNodeDefinition> nodes,
        Dictionary<string, AudioRoute> outputs,
        AudioOutputRef current
    ) => (_nodes, _outputs, _current) = (nodes, outputs, current);

    public AudioOutputRef Tap(string port = "audio") => _current with { Port = port };

    public AudioPipelineBuilder Branch(AudioOutputRef source) => new(_nodes, _outputs, source);

    public AudioPipelineBuilder Source(string id, AudioBuffer buffer) =>
        Add(
            new AudioNodeDefinition<AudioBufferSourceOptions>(
                id,
                new AudioBufferSourceProcessor(),
                new(buffer),
                ImmutableDictionary<string, AudioOutputRef>.Empty
            )
        );

    public AudioPipelineBuilder Then<TOptions>(
        string id,
        IAudioCompositeProcessor<TOptions> processor,
        TOptions options
    )
    {
        if (
            string.IsNullOrWhiteSpace(id)
            || id == "$source"
            || _nodes.Any(n => n.Id == id || n.Id.StartsWith(id + "/", StringComparison.Ordinal))
        )
            throw new ArgumentException($"组合节点 ID 无效或重复：{id}");
        // Build in isolation so a rejected expansion does not partially mutate the caller's graph.
        var expansion = new AudioPipelineBuilder([], new(StringComparer.Ordinal), _current);
        processor.Expand(expansion, id, options);
        if (
            expansion._nodes.Count == 0
            || expansion._outputs.Count != 0
            || expansion._nodes.Any(n => !n.Id.StartsWith(id + "/", StringComparison.Ordinal))
        )
            throw new ArgumentException("组合处理器必须生成其命名空间内的节点，不能声明出口。");
        if (expansion._nodes.Any(n => _nodes.Any(existing => existing.Id == n.Id)))
            throw new ArgumentException($"重复组合节点：{id}");
        _nodes.AddRange(expansion._nodes);
        _current = expansion._current;
        return this;
    }

    public AudioPipelineBuilder Then<TOptions>(
        string id,
        IAudioProcessor<TOptions> processor,
        TOptions options
    ) =>
        Add(
            new AudioNodeDefinition<TOptions>(
                id,
                processor,
                options,
                new Dictionary<string, AudioOutputRef> { ["audio"] = _current }
            )
        );

    public AudioPipelineBuilder Add(AudioNodeDefinition node)
    {
        if (_nodes.Any(n => n.Id == node.Id))
            throw new ArgumentException($"重复节点：{node.Id}");
        _nodes.Add(node);
        _current = new(node.Id);
        return this;
    }

    public AudioPipelineBuilder Output(
        string name,
        AudioOutputRef? source = null,
        string? alignWith = null,
        bool intentionalMismatch = false
    )
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || !_outputs.TryAdd(name, new(source ?? _current, alignWith, intentionalMismatch))
        )
            throw new ArgumentException($"出口名称无效或重复：{name}");
        return this;
    }

    public AudioPipelineDefinition Build() => new([.. _nodes], _outputs.ToImmutableDictionary());
}
