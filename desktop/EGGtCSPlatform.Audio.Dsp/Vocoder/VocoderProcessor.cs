using EGGtCSPlatform.Audio.Core.Pipeline;

namespace EGGtCSPlatform.Audio.Dsp.Vocoder;

public sealed record VocoderMetadata(bool IntroducedClipping);

public sealed class VocoderProcessor : SingleInputAudioProcessor<VocoderOptions>
{
    public override string Identity => "acoustic-vocoder-1";

    protected override AudioSignalFormat Describe(
        AudioSignalFormat input,
        VocoderOptions options,
        string nodeId
    )
    {
        options.Validate();
        if (input.SampleRate != 16000)
            throw new ArgumentException("声码器前必须将音频重采样到 16 kHz。");
        return input;
    }

    protected override AudioProcessResult Process(
        AudioSignal input,
        VocoderOptions options,
        AudioProcessingContext context,
        CancellationToken token
    )
    {
        var audio = Vocoder.Apply(input.Audio, options, token, out var clipped);
        return AudioProcessResult.Single(
            input with
            {
                Audio = audio,
            },
            new VocoderMetadata(clipped)
        );
    }
}
