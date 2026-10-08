using System.Collections.Frozen;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

public sealed record ProtocolCommandDefinition(
    Type CommandType,
    TimeSpan DefaultTimeout,
    IReadOnlySet<string> Revisions
)
{
    public string? IntroducedIn { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>Explicit command support. A newer revision never implicitly inherits commands.</summary>
public sealed class ProtocolCommandCatalog
{
    private readonly IReadOnlyDictionary<Type, ProtocolCommandDefinition> _commands;

    public ProtocolCommandCatalog(IEnumerable<ProtocolCommandDefinition> commands)
    {
        _commands = commands
            .Select(command =>
                command with
                {
                    Revisions = command.Revisions.ToFrozenSet(StringComparer.Ordinal),
                }
            )
            .ToFrozenDictionary(command => command.CommandType);
        if (
            _commands.Values.Any(command =>
                command.DefaultTimeout <= TimeSpan.Zero
                || !typeof(IDeviceCommand).IsAssignableFrom(command.CommandType)
            )
        )
            throw new ArgumentException(
                "Commands must implement IDeviceCommand and have positive default timeouts.",
                nameof(commands)
            );
    }

    public IReadOnlySet<Type> GetSupportedCommands(string revision) =>
        _commands
            .Values.Where(command => command.Revisions.Contains(revision))
            .Select(command => command.CommandType)
            .ToFrozenSet();

    public TimeSpan GetTimeout(
        Type commandType,
        string revision,
        IReadOnlyDictionary<Type, TimeSpan> overrides
    )
    {
        if (
            !_commands.TryGetValue(commandType, out var command)
            || !command.Revisions.Contains(revision)
        )
            throw new NotSupportedException(
                $"{commandType.Name} is not supported by revision {revision}."
            );
        var timeout = overrides.GetValueOrDefault(commandType, command.DefaultTimeout);
        return timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(overrides));
    }
}
