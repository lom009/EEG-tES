using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public partial class BatchExportDialogViewModel(int selectedCount, int speechCount = 0)
    : DialogViewModel
{
    public int SelectedCount { get; } = selectedCount;
    public bool HasSpeech => speechCount > 0;
    public bool HasDeviceRecords => SelectedCount > speechCount;
    public string SpeechDescription =>
        $"{speechCount} 个言语实验将各导出一个 ZIP，包含完整参数及所有训练结果。";

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

    public bool HasSelectedFormat => !HasDeviceRecords || ExportEdf || ExportCsv || ExportExpp;

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
