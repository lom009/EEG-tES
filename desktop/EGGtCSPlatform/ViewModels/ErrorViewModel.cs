using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EGGtCSPlatform.ViewModels;

public partial class ErrorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Unknown Error";

    [ObservableProperty]
    private string _description = "Unknown Error Description";

    [ObservableProperty]
    private string _confirmText = "确认";
}
