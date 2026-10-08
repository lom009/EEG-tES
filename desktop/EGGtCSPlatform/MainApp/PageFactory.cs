using System;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.MainApp;

public class PageFactory(Func<ApplicationPageNames, object?, PageViewModel> factory)
{
    public PageViewModel GetPageViewModel(ApplicationPageNames route, object? routeData = null) =>
        factory(route, routeData);
}
