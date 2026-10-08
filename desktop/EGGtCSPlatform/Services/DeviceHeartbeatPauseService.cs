using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Services;

public sealed class DeviceHeartbeatPauseService
    : DeviceRuntime.HeartbeatPauseService,
        IDeviceHeartbeatPauseService;
