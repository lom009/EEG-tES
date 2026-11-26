using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EGGtCSPlatform.Controls;
using EGGtCSPlatform.ViewModels;
using EGGtCSPlatform.ViewModels.Pages;
namespace EGGtCSPlatform.Views.Pages;
public partial class ElectrodeConfigurationPageView : UserControl
{
    private ElectrodeHead3D? _head3D;
    private BasicViewModel? _navigationHost;
    private MainViewModel? _mainHost;
    private bool _isLeaving;
    public ElectrodeConfigurationPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Exit3D();
            UnsubscribeNavigation();
            SubscribeNavigation();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _isLeaving = false;
            SubscribeNavigation();
            if (View3D.IsChecked == true) Show3D(null, new RoutedEventArgs());
        };
        DetachedFromVisualTree += (_, _) => { Exit3D(); UnsubscribeNavigation(); };
    }
    private void SubscribeNavigation()
    {
        UnsubscribeNavigation();
        _navigationHost = this.GetVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<BasicViewModel>()
            .FirstOrDefault(model => ReferenceEquals(model.CurrentPage, DataContext));
        _mainHost = this.GetVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<MainViewModel>().FirstOrDefault();
        if (_navigationHost is not null) _navigationHost.PropertyChanging += NavigationChanging;
        if (_mainHost is not null) _mainHost.PropertyChanging += NavigationChanging;
    }
    private void UnsubscribeNavigation()
    {
        if (_navigationHost is not null) _navigationHost.PropertyChanging -= NavigationChanging;
        if (_mainHost is not null) _mainHost.PropertyChanging -= NavigationChanging;
        _navigationHost = null;
        _mainHost = null;
    }
    private void NavigationChanging(object? sender, PropertyChangingEventArgs e)
    {
        if ((ReferenceEquals(sender, _navigationHost) && e.PropertyName == nameof(BasicViewModel.CurrentPage))
            || (ReferenceEquals(sender, _mainHost) && e.PropertyName == nameof(MainViewModel.CurrentView)))
        {
            _isLeaving = true;
            Exit3D();
            UnsubscribeNavigation();
        }
    }
    private void Exit3D()
    {
        // Hide the native window synchronously, before the outgoing page starts sliding.
        (Head3DHost.Content as ElectrodeHead3D)?.Exit();
        Head3DHost.Content = null;
        _head3D = null;
    }
    private void Show2D(object? sender, RoutedEventArgs e)
    {
        Head2D.IsVisible = true; Head3DHost.IsVisible = false; Reset3D.IsVisible = false;
        Exit3D();
    }
    private void Show3D(object? sender, RoutedEventArgs e)
    {
        if (_isLeaving) return;
        _head3D ??= new ElectrodeHead3D();
        _head3D.Page = DataContext as ElectrodeConfigurationPageViewModel;
        Head3DHost.Content = _head3D;
        Head2D.IsVisible = false; Head3DHost.IsVisible = true; Reset3D.IsVisible = true;
    }
    private void ResetHead3D(object? sender, RoutedEventArgs e) => _head3D?.ResetView();
}
