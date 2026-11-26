using System.Collections.Immutable;
using EGGtCSPlatform.Audio.Core.Pipeline;
using EGGtCSPlatform.Audio.Core.Timing;
using EGGtCSPlatform.Audio.Dsp.Noise;

namespace EGGtCSPlatform.Audio.Dsp.Reverb;

public sealed record ReverbNoiseOptions(int OffsetFrames, double SnrDb)
{
    public void Validate() => NoiseInput.ValidateOptions(OffsetFrames, SnrDb);
}

public sealed record ReverbOptions(
    RirSettings Settings,
    double CalibrationRms,
    ReverbNoiseOptions? Noise = null,
    RirResponse? PreparedResponse = null
);

public sealed record ReverbMetadata(
    RirResponse Response,
    double CalibrationGain,
    double PeakGain,
    double EarlySpeechRms,
    double NoiseGain,
    int OutputFrames,
    double OutputRms,
    NoiseInputMetadata? NoiseInput = null
);

public sealed class ReverbProcessor : IAudioProcessor<ReverbOptions>
{
    private readonly RirGenerator _generator = new();
    public string Identity => "image-source-reverb-2";

    public IReadOnlyDictionary<string, AudioSignalFormat> Describe(
        IReadOnlyDictionary<string, AudioSignalFormat> inputs,
        ReverbOptions options,
        string nodeId
    )
    {
        var input = NoiseInput.ValidatePorts(inputs, options.Noise is not null);
        options.Settings.Validate();
        if (
            options.PreparedResponse is { } prepared
            && (
                prepared.Settings != options.Settings
                || prepared.Full.IsDefaultOrEmpty
                || prepared.Effective.IsDefaultOrEmpty
                || prepared.EarlyReference.IsDefaultOrEmpty
                || prepared.Full.Length != options.Settings.Frames
                || prepared.DirectFrame < 0
                || prepared.DivisionFrame <= prepared.DirectFrame
                || prepared.DivisionFrame >= prepared.Full.Length
                || !double.IsFinite(prepared.EarlyCoefficientRms)
                || prepared.EarlyCoefficientRms <= 0
                || string.IsNullOrWhiteSpace(prepared.FullHash)
                || string.IsNullOrWhiteSpace(prepared.EffectiveHash)
            )
        )
            throw new ArgumentException("预生成 RIR 与设置不一致。");
        if (
            input.SampleRate != RirSettings.SampleRate
            || !double.IsFinite(options.CalibrationRms)
            || options.CalibrationRms <= 0
        )
            throw new ArgumentException("RIR 需要 16 kHz 音频及有效校准参数。");
        options.Noise?.Validate();
        return new Dictionary<string, AudioSignalFormat>
        {
            ["audio"] = input,
            ["dryReference"] = input,
        };
    }

    public ValueTask<AudioProcessResult> ProcessAsync(
        IReadOnlyDictionary<string, AudioSignal> inputs,
        ReverbOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        Describe(
            inputs.ToDictionary(
                p => p.Key,
                p => new AudioSignalFormat(
                    p.Value.Buffer.SampleRate,
                    p.Value.Buffer.Channels,
                    p.Value.Timeline.TimeDomain,
                    p.Value.Buffer.SampleType
                )
            ),
            options,
            context.NodeId
        );
        var input = inputs["audio"];
        var response = options.PreparedResponse ?? _generator.Generate(options.Settings, token);
        var frames = checked(input.Audio.Frames + response.Effective.Length - 1);
        var noise = options.Noise is { } n
            ? NoiseInput.Match(
                inputs["noise"],
                frames,
                input.Audio.SampleRate,
                input.Audio.Channels,
                n.OffsetFrames,
                token
            )
            : null;
        var result = Reverb.Apply(
            input.Audio,
            response,
            options.CalibrationRms,
            noise,
            options.Noise?.SnrDb ?? 0,
            token
        );
        var shift = response.DirectFrame / (double)input.Audio.SampleRate;
        var start = input.Timeline.StartSeconds + shift;
        var end = start + input.Audio.Duration.TotalSeconds;
        var zeros = input
            .Timeline.Regions.Select(r => new AudioZeroRegion(
                r.StartSeconds + shift,
                r.EndSeconds + shift
            ))
            .ToList();
        if (start > 0)
            zeros.Add(new(0, start));
        if (end < result.Audio.Duration.TotalSeconds)
            zeros.Add(new(end, result.Audio.Duration.TotalSeconds));
        return ValueTask.FromResult(
            new AudioProcessResult(
                ImmutableDictionary<string, AudioSignal>
                    .Empty.Add("audio", input with { Audio = result.Audio })
                    .Add(
                        "dryReference",
                        input with
                        {
                            Timeline = input.Timeline with
                            {
                                StartSeconds = start,
                                ZeroRegions = [.. zeros],
                            },
                        }
                    ),
                new ReverbMetadata(
                    response,
                    result.CalibrationGain,
                    result.PeakGain,
                    result.EarlySpeechRms,
                    result.NoiseGain,
                    result.Audio.Frames,
                    NoiseMixer.Rms(Internal.VocoderDsp.Decode(result.Audio)),
                    options.Noise is null
                        ? null
                        : NoiseInputMetadata.FromBuffer(inputs["noise"].Buffer, token)
                )
            )
        );
    }
}
