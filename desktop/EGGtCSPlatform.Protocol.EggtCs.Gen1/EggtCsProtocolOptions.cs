using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsProtocolOptions
{
    private readonly IReadOnlyDictionary<Type, TimeSpan> _timeouts;
    private readonly IReadOnlySet<Type> _supportedCommands;

    public EggtCsProtocolOptions(
        IReadOnlyDictionary<Type, TimeSpan> timeouts,
        bool requireMatchingResponseIndex = false,
        string? revision = null
    )
    {
        ArgumentNullException.ThrowIfNull(timeouts);
        revision ??= EggtCsRevisions.DefaultRevision;
        _timeouts = new Dictionary<Type, TimeSpan>(timeouts);
        if (!EggtCsRevisions.Supported.Contains(revision))
            throw new NotSupportedException($"Unsupported EggtCs revision {revision}.");
        Revision = revision;
        _supportedCommands = EggtCsCommandCatalog.GetSupportedCommands(revision);
        RequireMatchingResponseIndex = requireMatchingResponseIndex;
        if (_timeouts.Any(item => item.Value <= TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(
                nameof(timeouts),
                "Every request timeout must be positive."
            );
    }

    public static IReadOnlyList<Type> CommandTypes { get; } =
        Array.AsReadOnly(
            EggtCsCommandCatalog.Commands.Select(command => command.CommandType).ToArray()
        );

    public string Revision { get; }

    public EggtCsProtocolOptions ForRevision(string revision) =>
        new(_timeouts, RequireMatchingResponseIndex, revision);

    public bool SupportsCommand(Type commandType) => _supportedCommands.Contains(commandType);

    public bool RequireMatchingResponseIndex { get; }

    public TimeSpan GetTimeout(Type commandType) =>
        EggtCsCommandCatalog.GetTimeout(commandType, Revision, _timeouts);

    public static EggtCsProtocolOptions CreateTestDefaults(
        TimeSpan? timeout = null,
        bool requireMatchingResponseIndex = false
    )
    {
        var value = timeout ?? TimeSpan.FromSeconds(1);
        return CreateUniform(value, requireMatchingResponseIndex);
    }

    public static EggtCsProtocolOptions CreateUniform(
        TimeSpan timeout,
        bool requireMatchingResponseIndex = false
    ) => new(CommandTypes.ToDictionary(type => type, _ => timeout), requireMatchingResponseIndex);
}
