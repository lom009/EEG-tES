using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public partial class BatchExportDialogViewModel(int selectedCount) : DialogViewModel
{
    public int SelectedCount { get; } = selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFormat))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _exportEdf = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFormat))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _exportCsv = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFormat))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _exportExpp;

    [ObservableProperty]
    private bool _confirmed;

    public bool HasSelectedFormat => ExportEdf || ExportCsv || ExportExpp;

    public override bool AllowOverlayDismiss => true;

    [RelayCommand(CanExecute = nameof(HasSelectedFormat))]
    private void Confirm()
    {
        Confirmed = true;
        Close();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        Close();
    }

    protected override void OnOverlayDismiss() => Cancel();
}
