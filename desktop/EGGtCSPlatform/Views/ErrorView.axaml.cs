using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace EGGtCSPlatform.Views;

public partial class ErrorView : UserControl
{
    public ErrorView()
    {
        InitializeComponent();
    }

    private void ConfirmButton_OnClick(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as Window)?.Close();
}
