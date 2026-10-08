using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Views.Pages;

public partial class DeviceConnectionPageView : UserControl
{
    private DeviceConnectionPageViewModel? _observed;
    private bool _attached;

    public DeviceConnectionPageView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Observe();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Observe();
        };
        DataContextChanged += (_, _) => Observe();
    }

    private void Observe()
    {
        if (_observed is not null)
            _observed.Connection.PropertyChanged -= ConnectionChanged;
        _observed = _attached ? DataContext as DeviceConnectionPageViewModel : null;
        if (_observed is null)
            return;
        _observed.Connection.PropertyChanged += ConnectionChanged;
        _observed.RefreshDevices();
    }

    private void ConnectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            args.PropertyName
            is nameof(IDeviceSelectionContext.IsConnected)
                or nameof(IDeviceSelectionContext.StatusMessage)
        )
            // Read the current manager on the UI thread; never apply a captured old connection's snapshot.
            Dispatcher.UIThread.Post(() => _observed?.RefreshDevices());
    }
}
