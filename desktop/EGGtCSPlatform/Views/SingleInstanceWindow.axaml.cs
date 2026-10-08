using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EGGtCSPlatform.Views;

public partial class SingleInstanceWindow : Window
{
    public SingleInstanceWindow()
    {
        InitializeComponent();
    }

    public SingleInstanceWindow(string message)
        : this()
    {
        MessageText.Text = message;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close();
}
