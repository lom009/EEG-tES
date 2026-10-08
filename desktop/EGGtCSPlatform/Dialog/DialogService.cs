using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Dialog;

public class DialogService(Func<TopLevel?> topLevel)
{
    private static readonly TimeSpan DialogExitDuration = TimeSpan.FromMilliseconds(200);

    public Task ShowDialog<THost, TDialogViewModel>(THost host, TDialogViewModel dialogViewModel)
        where TDialogViewModel : DialogViewModel
        where THost : IDialogProvider => ShowDialogAsync(host, dialogViewModel);

    public async Task ShowDialogAsync(IDialogProvider host, DialogViewModel dialogViewModel)
    {
        if (!Avalonia.Controls.Design.IsDesignMode)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => host.DialogStack.Add(dialogViewModel),
                DispatcherPriority.Normal
            );
            await Dispatcher.UIThread.InvokeAsync(
                dialogViewModel.Show,
                DispatcherPriority.Background
            );
            try
            {
                await dialogViewModel.WaitAsync();
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (dialogViewModel.IsDialogOpen)
                            dialogViewModel.Close();
                    },
                    DispatcherPriority.Normal
                );
                await Task.Delay(DialogExitDuration);
                await Dispatcher.UIThread.InvokeAsync(
                    () => host.DialogStack.Remove(dialogViewModel),
                    DispatcherPriority.Normal
                );
            }
            return;
        }

        // Fallback to design time
        var dialogView =
            new ViewLocator().Build(dialogViewModel)
            ?? new ContentControl { Content = dialogViewModel };

        // Show UI
        if (DialogInjector.TryShow(dialogView))
        {
            dialogViewModel.Show();
            try
            {
                await dialogViewModel.WaitAsync();
            }
            finally
            {
                dialogViewModel.Close();
                DialogInjector.Close();
            }
        }
    }

    public async Task<string?> FolderPicker(string title = "选择文件夹")
    {
        var topLevelVisual = topLevel();
        if (topLevelVisual == null)
            return null;

        var folders = await topLevelVisual.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions() { AllowMultiple = false, Title = title }
        );

        var path = folders.FirstOrDefault()?.Path;
        if (path == null)
            return null;
        return path.IsAbsoluteUri ? path.LocalPath : path.OriginalString;
    }

    public async Task<string[]> FilePicker(
        string title = "Select a file",
        bool allowMultiple = false,
        FilePickerFileType[]? fileTypes = null
    )
    {
        fileTypes ??= [FilePickerFileTypes.All];

        var topLevelVisual = topLevel();
        if (topLevelVisual == null)
            return [];

        var files = await topLevelVisual.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions()
            {
                AllowMultiple = allowMultiple,
                Title = title,
                FileTypeFilter = fileTypes,
            }
        );

        return files
            .Select(file =>
                file.Path.IsAbsoluteUri ? file.Path.LocalPath : file.Path.OriginalString
            )
            .ToArray();
    }
}
