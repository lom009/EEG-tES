using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.DeviceSdk;

/// <summary>A protocol implementation; it does not own runtime-wide transports or scheduling.</summary>
public interface IDeviceProtocolModule
{
    string ProtocolId { get; }
    IReadOnlySet<string> SupportedRevisions { get; }

    /// <summary>Resolves software rules without connecting or changing device identity/endpoints.</summary>
    ProtocolRevisionSelection ResolveRevision(DeviceCandidate candidate) =>
        new(candidate.ProtocolVersion, candidate.RevisionSource);
    bool CanConnect(DeviceCandidate candidate);
    DeviceCapabilities GetCapabilities(
        DeviceCandidate candidate,
        IReadOnlySet<DeviceCapabilityKind> realCapabilities
    );
    IReadOnlySet<Type> GetSupportedCommands(string revision);
    Task<IEggtCsDevice> ConnectAsync(
        ProtocolConnectionContext context,
        CancellationToken cancellationToken = default
    );
    IDeviceDiscovery CreateDiscovery(IDiscoveryTransport transport, int callbackPort);
    Task<DeviceStatusResponse> ValidateConnectionAsync(
        IEggtCsDevice device,
        CancellationToken cancellationToken = default
    ) => device.ReadStatusAsync(cancellationToken);
}

public sealed record ProtocolHeartbeatSettings(
    bool Enabled,
    TimeSpan Interval,
    TimeSpan FailureTimeout,
    Func<bool> ShouldRun,
    Action<DeviceStatusResponse> StatusReceived
);

/// <summary>Resources supplied by Runtime. Created transports/sessions belong to the returned device.</summary>
public sealed record ProtocolConnectionContext(
    DeviceCandidate Candidate,
    ITransportFactory TransportFactory,
    DeviceSessionOptions SessionOptions,
    IReadOnlySet<DeviceCapabilityKind> RealCapabilities,
    ProtocolHeartbeatSettings Heartbeat,
    Action<DeviceIdentity, Exception> SessionFaulted
);

public interface IDeviceProtocolBinding
{
    string ProtocolId { get; }
    string ProtocolRevision { get; }
    DeviceProtocolInfo ProtocolInfo =>
        new(ProtocolId, ProtocolRevision, ProtocolRevisionSource.Unspecified);

    /// <summary>Software definition support; this does not establish firmware support.</summary>
    bool SupportsCommand(Type commandType);
}

/// <summary>The immutable software protocol selection, not a detected firmware version.</summary>
public sealed record DeviceProtocolInfo(
    string ProtocolId,
    string ActiveRevision,
    ProtocolRevisionSource RevisionSource
);

public sealed record ProtocolRevisionSelection(string Revision, ProtocolRevisionSource Source);

public enum ProtocolRevisionSource
{
    Unspecified,
    CompatibilityAssumption,
    Explicit,
    DeviceReported,
    SoftwareDefault,
}

public sealed class AmbiguousDeviceProtocolException(string message)
    : InvalidOperationException(message);

public sealed class DeviceProtocolConflictException(string message)
    : InvalidOperationException(message);
