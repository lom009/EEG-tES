namespace EGGtCSPlatform.Audio.Core.Pipeline;

/// <summary>A construction-time macro. Only its expanded nodes are executed.</summary>
public interface IAudioCompositeProcessor<TOptions>
{
    void Expand(AudioPipelineBuilder branch, string id, TOptions options);
}
