using System;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

internal static class TaskExtensions
{
    public static void ObserveFault(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = task.ContinueWith(
            static faultedTask => _ = faultedTask.Exception,
            default,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );
    }
}
