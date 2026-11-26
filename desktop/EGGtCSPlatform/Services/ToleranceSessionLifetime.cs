using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

/// <summary>Stops page-owned stimulation before application shutdown disconnects devices.</summary>
public sealed class ToleranceSessionLifetime
{
    private readonly object _gate = new();
    private readonly HashSet<DeviceToleranceSession> _sessions = [];

    internal void Register(DeviceToleranceSession session)
    {
        lock (_gate)
            _sessions.Add(session);
    }

    internal void Unregister(DeviceToleranceSession session)
    {
        lock (_gate)
            _sessions.Remove(session);
    }

    public async Task StopAllAsync()
    {
        DeviceToleranceSession[] sessions;
        lock (_gate)
            sessions = _sessions.ToArray();
        List<Exception> failures = [];
        foreach (var session in sessions)
        {
            try
            {
                await session.StopAsync();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }
        if (failures.Count > 0)
            throw new AggregateException("耐受度刺激停止失败", failures);
    }
}
