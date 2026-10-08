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

    public int PageContentRow => CurrentPage is ElectrodeConfigurationPageViewModel or EnvelopeStimulationPageViewModel ? 1 : 2;
    public Avalonia.Thickness PageContentMargin => CurrentPage is ElectrodeConfigurationPageViewModel
        ? new Avalonia.Thickness(0, 24, 0, 0) : CurrentPage is EnvelopeStimulationPageViewModel
        ? new Avalonia.Thickness(0, 16, 0, 0) : default;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageContentRow))]
    [NotifyPropertyChangedFor(nameof(PageContentMargin))]
    private PageViewModel _currentPage = currentPage;

    [RelayCommand]
    private void GoBack() => router.GoBack();

    [RelayCommand]
    private void GoHome() => router.GoHome();
}
