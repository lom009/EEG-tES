using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

namespace EGGtCSPlatform.Services;

public sealed class ConfigurableDeviceBackend : IDeviceDiscovery, IDeviceConnector, IAsyncDisposable
{
    private readonly IDeviceRuntime _runtime;

    public ConfigurableDeviceBackend(
        DeviceBackendOptions options,
        IDeviceConnectionNetworkContext networkContext
    )
        : this(
            options,
            networkContext,
            new DeviceHeartbeatPauseService(),
            new DeviceHeartbeatOptions { Enabled = false },
            NullByteTrafficLogger.Instance
        ) { }

    public ConfigurableDeviceBackend(
        DeviceBackendOptions options,
        IDeviceConnectionNetworkContext networkContext,
        IDeviceHeartbeatPauseService heartbeatPause,
        DeviceHeartbeatOptions heartbeatOptions,
        IByteTrafficLogger byteTrafficLogger,
        Action<DeviceIdentity, Exception>? faulted = null,
        EggtCsProtocolOptions? protocolOptions = null,
        Action<IEggtCsDevice, DeviceStatusResponse>? heartbeatReceived = null
    )
    {
        ProtocolModule = new EggtCsProtocolModule(
            new()
            {
                AllowUnverifiedChecksum = true,
                Protocol =
                    protocolOptions
                    ?? EggtCsProtocolOptions.CreateUniform(heartbeatOptions.ResponseTimeout),
            }
        );
        _runtime = new DeviceRuntimeBuilder()
            .AddProtocol(ProtocolModule)
            .WithOptions(
                new DeviceRuntimeOptions
                {
                    Backend = options.ToRuntimeProfile(),
                    NetworkSettings = () =>
                    {
                        var network = networkContext.Snapshot;
                        return new(
                            network.BroadcastAddress,
                            network.DevicePort,
                            network.LocalAddress,
                            network.LocalPort
                        );
                    },
                    ValidateConnection = false, // The existing coordinator performs exactly one status verification.
                    HeartbeatEnabled = heartbeatOptions.Enabled,
                    HeartbeatInterval = heartbeatOptions.Interval,
                    HeartbeatFailureTimeout = heartbeatOptions.DisconnectTimeout,
                    HeartbeatPause = heartbeatPause,
                    PauseHeartbeatDuringOperations = false, // Preserve the application's existing pause scopes.
                    ByteTrafficLogger = byteTrafficLogger,
                    SessionFaulted = faulted,
                    HeartbeatReceived = heartbeatReceived,
                    FailureHandler = (_, _) =>
                        ValueTask.FromResult(CommunicationFailureDecision.UseDefault),
                }
            )
            .Build();
    }

    public IDeviceProtocolModule ProtocolModule { get; }
    public IDeviceManager DeviceManager => _runtime.Devices;
    public IDeviceRuntime Runtime => _runtime;

    public bool CanConnect(DeviceCandidate candidate) => _runtime.Connector.CanConnect(candidate);

    public Task<IEggtCsDevice> ConnectAsync(
        DeviceCandidate candidate,
        CancellationToken cancellationToken = default
    ) => _runtime.Connector.ConnectAsync(candidate, cancellationToken);

    public IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) => _runtime.Discovery.DiscoverAsync(window, cancellationToken);

    public ValueTask DisposeAsync() => _runtime.DisposeAsync();
}
