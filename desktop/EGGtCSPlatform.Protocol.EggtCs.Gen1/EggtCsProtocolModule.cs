using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed record EggtCsModuleOptions
{
    public IChecksum Checksum { get; init; } = new EggtCsUnverifiedFixedChecksum();
    public bool AllowUnverifiedChecksum { get; init; }
    public EggtCsProtocolOptions Protocol { get; init; } = new(new Dictionary<Type, TimeSpan>());
}

public sealed class EggtCsProtocolModule(EggtCsModuleOptions? options = null)
    : IDeviceProtocolModule
{
    public const string Id = "eggtcs.gen1";
    private readonly EggtCsModuleOptions _options = options ?? new();
    public string ProtocolId => Id;
    public IReadOnlySet<string> SupportedRevisions => EggtCsRevisions.Supported;

    public ProtocolRevisionSelection ResolveRevision(DeviceCandidate candidate) =>
        candidate.RevisionSource
            is ProtocolRevisionSource.SoftwareDefault
                or ProtocolRevisionSource.CompatibilityAssumption
        || candidate.RevisionSource == ProtocolRevisionSource.Unspecified
            && string.IsNullOrWhiteSpace(candidate.ProtocolVersion)
            ? new(_options.Protocol.Revision, ProtocolRevisionSource.SoftwareDefault)
            : new(candidate.ProtocolVersion, candidate.RevisionSource);

    public bool CanConnect(DeviceCandidate candidate) =>
        (candidate.ProtocolId is null || candidate.ProtocolId == Id)
        && SupportedRevisions.Contains(candidate.ProtocolVersion);

    public IReadOnlySet<Type> GetSupportedCommands(string revision) =>
        EggtCsCommandCatalog.GetSupportedCommands(revision);

    public DeviceCapabilities GetCapabilities(
        DeviceCandidate candidate,
        IReadOnlySet<DeviceCapabilityKind> realCapabilities
    ) =>
        new(
            realCapabilities.Contains(DeviceCapabilityKind.Status),
            realCapabilities.Contains(DeviceCapabilityKind.EegAcquisition),
            realCapabilities.Contains(DeviceCapabilityKind.Stimulation),
            realCapabilities.Contains(DeviceCapabilityKind.EegImpedance),
            realCapabilities.Contains(DeviceCapabilityKind.StimulationImpedance),
            realCapabilities.Contains(DeviceCapabilityKind.Tolerance),
            EggtCsCapabilities.EegChannelCount,
            EggtCsCapabilities.Default.SupportedSampleRatesHz,
            EggtCsCapabilities.StimulationChannelCount
        );

    public async Task<IEggtCsDevice> ConnectAsync(
        ProtocolConnectionContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!CanConnect(context.Candidate))
            throw new NotSupportedException("Unsupported EggtCs protocol identity or revision.");
        SessionEggtCsDevice? physical = null;
        var heartbeat = context.Heartbeat;
        var connector = new EggtCsDeviceConnector(
            context.TransportFactory,
            _options.Checksum,
            _options.Protocol.ForRevision(context.Candidate.ProtocolVersion),
            _options.AllowUnverifiedChecksum,
            sessionOptions: context.SessionOptions with
            {
                HeartbeatPolicy = heartbeat.Enabled
                    ? new EggtCsHeartbeatPolicy(
                        heartbeat.Interval,
                        heartbeat.FailureTimeout,
                        heartbeat.ShouldRun,
                        status =>
                        {
                            physical?.ApplyStatus(status);
                            heartbeat.StatusReceived(status);
                        }
                    )
                    : null,
            },
            sessionFaulted: context.SessionFaulted,
            capabilitiesFactory: candidate => GetCapabilities(candidate, context.RealCapabilities),
            deviceCreated: device => physical = device
        );
        return await connector
            .ConnectAsync(context.Candidate, cancellationToken)
            .ConfigureAwait(false);
    }

    public IDeviceDiscovery CreateDiscovery(IDiscoveryTransport transport, int callbackPort) =>
        new EggtCsDeviceDiscovery(
            transport,
            _options.Checksum,
            callbackPort,
            _options.Protocol.Revision
        );

    public Task<DeviceStatusResponse> ValidateConnectionAsync(
        IEggtCsDevice device,
        CancellationToken cancellationToken = default
    ) => device.ReadStatusAsync(cancellationToken);
}
