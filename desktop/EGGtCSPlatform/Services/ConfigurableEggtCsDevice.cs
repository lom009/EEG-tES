using System;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;

namespace EGGtCSPlatform.Services;

// Source-compatible application adapter. Capability selection lives in the reusable runtime.
public sealed class ConfigurableEggtCsDevice : DeviceRuntime.MixedSourceDevice
{
    public ConfigurableEggtCsDevice(
        DeviceIdentity identity,
        IDeviceSession session,
        Action<DeviceIdentity, Exception>? faulted = null
    )
        : base(identity, session, Protocol.EggtCs.Gen1.EggtCsCapabilities.Default, faulted) { }

    public ConfigurableEggtCsDevice(
        DeviceIdentity identity,
        DeviceBackendOptions options,
        IEggtCsDevice? physical = null,
        SimulatedEggtCsDevice? simulation = null
    )
        : base(identity, options.ToRuntimeProfile(), physical, simulation) { }
}
