using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class IndexViewModel(
    INavigationRouter router,
    IDeviceSelectionContext deviceConnection,
    IDeviceCapabilityAvailability? capabilityAvailability = null
) : ViewModelBase
{
    private readonly IDeviceCapabilityAvailability _capabilityAvailability =
        capabilityAvailability ?? AllDeviceCapabilitiesAvailable.Instance;

    public IDeviceSelectionContext DeviceConnection { get; } = deviceConnection;

    public string StartExperimentUnavailableReason =>
        _capabilityAvailability.GetExperimentEntryUnavailableReason();

    public bool CanStartExperiment => string.IsNullOrEmpty(StartExperimentUnavailableReason);

    public string StartExperimentSubtitle =>
        CanStartExperiment ? "创建并配置新的脑机实验" : StartExperimentUnavailableReason;

    public string ToleranceUnavailableReason =>
        _capabilityAvailability.GetUnavailableReason(DeviceCapabilityKind.Tolerance);

    public bool CanStartToleranceTest => string.IsNullOrEmpty(ToleranceUnavailableReason);

    public string ToleranceSubtitle =>
        CanStartToleranceTest ? "耐受度" : ToleranceUnavailableReason;

    [RelayCommand(CanExecute = nameof(CanStartExperiment))]
    private void StartExperiment() => router.Navigate(StartExperimentRouteData.NewExperiment);

    [RelayCommand]
    private void ShowExperimentRecords() => router.Navigate(StartExperimentRouteData.History);

    [RelayCommand]
    private void OpenDeviceConnection() => router.Navigate(ApplicationPageNames.DeviceConnection);

    [RelayCommand(CanExecute = nameof(CanStartToleranceTest))]
    private void StartToleranceTest() => router.Navigate(ApplicationPageNames.ToleranceTest);
}
