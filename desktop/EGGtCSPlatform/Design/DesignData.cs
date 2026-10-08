using System;
using EGGtCSPlatform.Bootstrap;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace EGGtCSPlatform.Design;

public static class DesignData
{
    private static readonly IServiceProvider Services;

    static DesignData()
    {
        var collection = new ServiceCollection();
        Bootstrapper.RegisterCommonServices(collection);
        Services = collection.BuildServiceProvider();
    }

    public static MainViewModel MainViewModel => Services.GetRequiredService<MainViewModel>();

    public static ConfirmDialogViewModel ConfirmDialogViewModel =>
        Services.GetRequiredService<ConfirmDialogViewModel>();

    public static ErrorViewModel ErrorViewModel => Services.GetRequiredService<ErrorViewModel>();
}
