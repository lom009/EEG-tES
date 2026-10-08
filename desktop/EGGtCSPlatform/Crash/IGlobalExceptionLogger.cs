using System;
using System.Threading;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Crash;

public sealed record GlobalExceptionContext(
    string Source,
    DateTimeOffset OccurredAt,
    bool IsTerminating
);

public interface IGlobalExceptionLogger
{
    ValueTask LogAsync(
        Exception exception,
        GlobalExceptionContext context,
        CancellationToken cancellationToken = default
    );
}

public sealed class CrashServiceExceptionLogger : IGlobalExceptionLogger
{
    public ValueTask LogAsync(
        Exception exception,
        GlobalExceptionContext context,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        CrashService.SetCrashData(exception);
        return ValueTask.CompletedTask;
    }
}
