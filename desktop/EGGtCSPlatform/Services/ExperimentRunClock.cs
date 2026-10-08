using System;
using System.Threading;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

public interface IExperimentRunClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemExperimentRunClock : IExperimentRunClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
