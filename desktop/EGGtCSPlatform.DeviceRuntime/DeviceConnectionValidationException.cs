using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime;

public sealed class DeviceConnectionValidationException(DeviceCommandStatus status)
    : CommunicationException($"Device status verification failed: {status}.")
{
    public DeviceCommandStatus Status { get; } = status;
}
