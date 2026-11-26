using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ViewModels;

public partial class BasicViewModel(
    INavigationRouter router,
    PageViewModel currentPage,
    IDeviceSelectionContext deviceConnection,
    bool isNavigationAnimationEnabled = true
) : ViewModelBase
{
    public IDeviceSelectionContext DeviceConnection { get; } = deviceConnection;

    public bool IsNavigationAnimationEnabled { get; } = isNavigationAnimationEnabled;

    [ObservableProperty]
    private bool _isTransitionReversed;

    [ObservableProperty]
    private PageViewModel _currentPage = currentPage;

    [RelayCommand]
    private void GoBack() => router.GoBack();

    [RelayCommand]
    private void GoHome()
    {
        if (CurrentPage.CanGoBack)
            router.GoHome();
    }
}
