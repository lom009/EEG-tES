using System;
using System.Threading.Tasks;

namespace EGGtCSPlatform.Services;

// Called on the UI thread. Keep its synchronization context alive until all
// asynchronous cleanup has completed, including UI-affine disposables.
internal sealed class ApplicationExitWorkflow(
    Func<Task> shutdown,
    Func<Task> disposeServices,
    Action releaseLease,
    Action completeExit,
    Action<Exception> reportFailure,
    Func<Task>? finishLogging = null
)
{
    private Task? _exitTask;
    public bool IsStarted => _exitTask is not null;
    public bool IsComplete { get; private set; }

    public Task RunAsync() => _exitTask ??= RunCoreAsync();

    private async Task RunCoreAsync()
    {
        // Publish the task before invoking callbacks or closing any windows.
        await Task.Yield();
        try
        {
            await shutdown();
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        try
        {
            await disposeServices();
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        try
        {
            releaseLease();
        }
        catch (Exception exception)
        {
            reportFailure(exception);
        }
        if (finishLogging is not null)
        {
            try
            {
                await finishLogging();
            }
            catch (Exception exception)
            {
                reportFailure(exception);
            }
        }
        IsComplete = true;
        completeExit();
    }
}
