using System.Threading.Tasks;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed class ToleranceEntryService(
    DialogService dialogs,
    IExperimentPersistenceService persistence,
    ISubjectLookupService subjects
)
{
    public async Task StartAsync(IDialogProvider host, INavigationRouter router)
    {
        var dialog = new ToleranceSubjectDialogViewModel(persistence, subjects);
        await dialogs.ShowDialog(host, dialog);
        if (dialog.Confirmed)
            router.Navigate(
                new StimulusConfigurationRouteData(
                    "耐受度测试",
                    dialog.SubjectId,
                    Remarks: dialog.Remarks,
                    CreationMode: ExperimentCreationMode.StimulusOnly,
                    Tolerance: new(dialog.SubjectId, dialog.Remarks)
                )
            );
    }
}
