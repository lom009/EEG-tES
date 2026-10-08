using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime;

public interface IHeartbeatPauseService
{
    IDisposable Pause(DeviceId deviceId);
    bool IsPaused(DeviceId deviceId);
}

public class HeartbeatPauseService : IHeartbeatPauseService
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, int> _counts = [];

    public IDisposable Pause(DeviceId deviceId)
    {
        lock (_gate)
        {
            _counts.TryGetValue(deviceId, out var count);
            _counts[deviceId] = checked(count + 1);
        }
        return new Scope(this, deviceId);
    }

    public bool IsPaused(DeviceId deviceId)
    {
        lock (_gate)
            return _counts.TryGetValue(deviceId, out var count) && count > 0;
    }

    private void Resume(DeviceId id)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(id, out var count))
                return;
            if (count <= 1)
                _counts.Remove(id);
            else
                _counts[id] = count - 1;
        }
    }

    private sealed class Scope(HeartbeatPauseService owner, DeviceId id) : IDisposable
    {
        private HeartbeatPauseService? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Resume(id);
    }
}
