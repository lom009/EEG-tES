using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsDeviceConnector(
    ITransportFactory transportFactory,
    IChecksum checksum,
    EggtCsProtocolOptions protocolOptions,
    bool allowUnverifiedChecksum = false,
    DeviceSessionOptions? sessionOptions = null,
    Func<DeviceCandidate, DeviceSessionOptions>? sessionOptionsFactory = null,
    Action<DeviceIdentity, Exception>? sessionFaulted = null,
    Func<DeviceCandidate, DeviceCapabilities>? capabilitiesFactory = null,
    Action<SessionEggtCsDevice>? deviceCreated = null
) : IDeviceConnector
{
    public bool CanConnect(DeviceCandidate candidate) =>
        transportFactory.CanCreate(candidate.ConnectionEndpoint ?? candidate.Endpoint)
        && (candidate.ProtocolId is null || candidate.ProtocolId == EggtCsProtocolModule.Id)
        && EggtCsRevisions.Supported.Contains(candidate.ProtocolVersion)
        && candidate.ProtocolVersion == protocolOptions.Revision;

    public async Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    )
    {
        if (!CanConnect(candidate))
            throw new NotSupportedException(
                $"Unsupported {candidate.ConnectionEndpoint?.Scheme ?? candidate.Endpoint.Scheme} endpoint or EggtCs revision {candidate.ProtocolVersion}; connector uses {protocolOptions.Revision}."
            );
        if (!checksum.IsVerifiedForPhysicalDevices && !allowUnverifiedChecksum)
            throw new InvalidOperationException(
                $"Physical EggtCs {protocolOptions.Revision} connections are disabled until the checksum algorithm is verified with golden frames."
            );

        var options =
            sessionOptionsFactory?.Invoke(candidate)
            ?? sessionOptions
            ?? new DeviceSessionOptions
            {
                RetryPolicy = NoRetryPolicy.Instance,
                ReconnectPolicy = ReconnectPolicy.Disabled,
                HeartbeatPolicy = new EggtCsHeartbeatPolicy(),
            };
        options = options with
        {
            ProtocolId = EggtCsProtocolModule.Id,
            ProtocolRevision = protocolOptions.Revision,
        };
        var session = new DeviceSession(
            transportFactory.Create(candidate.ConnectionEndpoint ?? candidate.Endpoint),
            new EggtCsFrameCodec(checksum),
            new EggtCsProtocolProfile(protocolOptions),
            new EggtCsResponseMatcher(),
            new CorrelatedRequestScheduler(),
            options
        );
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            var device = new SessionEggtCsDevice(
                candidate.Identity,
                capabilitiesFactory?.Invoke(candidate) ?? EggtCsCapabilities.Default,
                session,
                faulted: sessionFaulted
            );
            deviceCreated?.Invoke(device);
            return device;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
