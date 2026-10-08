using Avalonia.Controls;
using Avalonia.Interactivity;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels.Pages;
namespace EGGtCSPlatform.Views.Pages;
public partial class ElectrodeConfigurationPageView : UserControl
{
    private ElectrodeHead3D? _head3D;
    public ElectrodeConfigurationPageView() { InitializeComponent(); }
    private void Show2D(object? sender, RoutedEventArgs e)
    {
        Head2D.IsVisible = true; Head3DHost.IsVisible = false; Reset3D.IsVisible = false;
    }
    private void Show3D(object? sender, RoutedEventArgs e)
    {
        _head3D ??= new ElectrodeHead3D();
        _head3D.Page = DataContext as ElectrodeConfigurationPageViewModel;
        Head3DHost.Content = _head3D;
        Head2D.IsVisible = false; Head3DHost.IsVisible = true; Reset3D.IsVisible = true;
    }
    private void ResetHead3D(object? sender, RoutedEventArgs e) => _head3D?.ResetView();
}
