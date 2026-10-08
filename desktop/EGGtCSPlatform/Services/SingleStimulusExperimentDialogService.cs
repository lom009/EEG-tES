using System.Threading.Tasks;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public interface ISingleStimulusExperimentDialogService
{
    Task ShowAsync(ExperimentRunRouteData route);
}

public sealed class SingleStimulusExperimentDialogService(
    DialogService dialogs,
    IDialogProvider host,
    IExperimentRunService runs,
    IExperimentRunPersistenceCoordinator persistence,
    IExperimentRunClock clock,
    IOptions<ExperimentRunTimingOptions> timing,
    IExperimentRunErrorDialogService errors
) : ISingleStimulusExperimentDialogService
{
    public async Task ShowAsync(ExperimentRunRouteData route)
    {
        using var model = new SingleStimulusExperimentDialogViewModel(
            route,
            runs,
            persistence,
            clock,
            timing.Value,
            errors
        );
        await dialogs.ShowDialog(host, model);
    }
}
