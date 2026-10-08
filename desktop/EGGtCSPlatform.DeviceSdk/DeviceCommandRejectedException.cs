namespace EGGtCSPlatform.DeviceSdk;

public sealed class DeviceCommandRejectedException : InvalidOperationException
{
    public DeviceCommandRejectedException(string operation, DeviceCommandResult result)
        : base($"Device rejected {operation}: {result.Status}. {result.Message}".TrimEnd())
    {
        Operation = operation;
        Status = result.Status;
        DeviceMessage = result.Message;
    }

    public string Operation { get; }

    public DeviceCommandStatus Status { get; }

    public string? DeviceMessage { get; }
}
