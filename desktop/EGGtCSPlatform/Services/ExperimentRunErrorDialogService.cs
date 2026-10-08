using System.Threading.Tasks;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Services;

public interface IExperimentRunErrorDialogService
{
    Task ShowAsync(string title, string message);
}

public sealed class ExperimentRunErrorDialogService(
    DialogService dialogService,
    IDialogProvider dialogHost
) : IExperimentRunErrorDialogService
{
    public Task ShowAsync(string title, string message)
    {
        var dialog = new ConfirmDialogViewModel(DialogKind.Error)
        {
            Title = title,
            Message = message,
            ConfirmText = "确认",
            ShowCancelButton = false,
        };
        return dialogService.ShowDialog(dialogHost, dialog);
    }
}
