using Avalonia.Controls;
using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Views;

public partial class ApplicationUpdateWindow : Window
{
    public ApplicationUpdateWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Avalonia.Application.Current is App { IsExiting: true })
            return;
        if (DataContext is ApplicationUpdateViewModel { CanClose: false })
            e.Cancel = true;
    }
}
