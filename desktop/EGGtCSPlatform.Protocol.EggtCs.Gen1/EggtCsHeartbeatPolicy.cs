using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsHeartbeatPolicy(
    TimeSpan? interval = null,
    TimeSpan? failureTimeout = null,
    Func<bool>? shouldRun = null,
    Action<DeviceStatusResponse>? statusReceived = null
) : IHeartbeatPolicy
{
    private readonly Func<bool> _shouldRun = shouldRun ?? (() => true);

    public TimeSpan Interval { get; } = interval ?? TimeSpan.FromSeconds(2);

    public TimeSpan FailureTimeout { get; } = failureTimeout ?? TimeSpan.FromSeconds(8);

    public bool ShouldRun => _shouldRun();

    public async ValueTask ExecuteAsync(IDeviceSession session, CancellationToken cancellationToken)
    {
        var response = await session
            .SendAsync(new ReadDeviceStatusRequest(), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            statusReceived?.Invoke(response);
        }
        catch
        {
            // A UI/state observer must never turn a successful heartbeat into a
            // communication failure or disconnect an otherwise healthy device.
        }
    }
}
