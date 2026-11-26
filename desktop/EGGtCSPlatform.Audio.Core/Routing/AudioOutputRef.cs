namespace EGGtCSPlatform.Audio.Core.Routing;

public sealed record AudioOutputRef(string NodeId, string Port = "audio")
{
    public static AudioOutputRef Source { get; } = new("$source");
}

public sealed record AudioRoute(
    AudioOutputRef Source,
    string? AlignWith = null,
    bool IntentionalMismatch = false
);
