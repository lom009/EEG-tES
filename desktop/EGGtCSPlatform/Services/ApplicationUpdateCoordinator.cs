using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.Views;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public sealed class ApplicationUpdateCoordinator(
    IOptions<ApplicationUpdateOptions> options,
    IApplicationUpdateClient updateClient,
    IApplicationShutdownCoordinator shutdownCoordinator
)
{
    private int _startupCheckStarted;

    public async Task RunStartupCheckAsync(Func<Window?> ownerProvider)
    {
        ArgumentNullException.ThrowIfNull(ownerProvider);
        if (
            Interlocked.Exchange(ref _startupCheckStarted, 1) != 0
            || !options.Value.AutoCheckOnStartup
            || !updateClient.IsInstalled
        )
        {
            return;
        }

        var viewModel = new ApplicationUpdateViewModel(updateClient, shutdownCoordinator);
        try
        {
            var update = await updateClient.CheckForUpdatesAsync();
            if (update is null)
                return;

            viewModel.ShowAvailableUpdate(update);
        }
        catch (Exception exception)
        {
            viewModel.ShowCheckFailure(exception);
        }

        var owner = ownerProvider();
        if (
            owner is null
            || !owner.IsVisible
            || Avalonia.Application.Current is App { IsExiting: true }
        )
            return;

        var window = new ApplicationUpdateWindow { DataContext = viewModel };
        void CloseWindow() => window.Close();
        viewModel.CloseRequested += CloseWindow;
        try
        {
            try
            {
                await window.ShowDialog(owner);
            }
            catch (InvalidOperationException) when (!owner.IsVisible)
            {
                // The owner can close between resolving it and showing the dialog.
            }
        }
        finally
        {
            viewModel.CloseRequested -= CloseWindow;
        }
    }
}
