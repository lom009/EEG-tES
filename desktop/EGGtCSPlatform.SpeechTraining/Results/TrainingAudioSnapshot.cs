using System.Collections.Immutable;
using System.Text.Json;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Routing;
using EGGtCSPlatform.Audio.Core.Timing;

namespace EGGtCSPlatform.SpeechTraining;

public sealed record TrainingNodeSnapshot(
    string Id,
    string ProcessorIdentity,
    JsonElement Parameters,
    ImmutableDictionary<string, AudioOutputRef> Inputs,
    ImmutableDictionary<string, AudioSignalFormat>? OutputFormats = null
);

public sealed record TrainingResourceSnapshot(
    string Role,
    string? FileName,
    string? FileSha256,
    string PcmSha256
);

public sealed record TrainingExecutionSnapshot(
    string NodeId,
    string ProcessorIdentity,
    JsonElement Metadata
);

public sealed record TrainingAudioSnapshot(
    TrainingAudioSettings Settings,
    string SentenceId,
    int Seed,
    double SpeedMultiplier,
    ImmutableArray<TrainingNodeSnapshot> Nodes,
    ImmutableDictionary<string, AudioRoute> Outputs,
    ImmutableArray<TrainingResourceSnapshot> Resources,
    ImmutableDictionary<string, AudioTimeline> Timelines,
    SpeechTimeStretchMetadata? TimeStretch,
    SpeechNoiseMetadata? Noise,
    SpeechRirMetadata? Rir,
    ImmutableArray<TrainingExecutionSnapshot> Execution = default,
    ImmutableDictionary<string, AudioSignalFormat>? OutputFormats = null
)
{
    public ImmutableArray<TrainingExecutionSnapshot> Execution { get; init; } =
        Execution.IsDefault ? [] : Execution;
}
