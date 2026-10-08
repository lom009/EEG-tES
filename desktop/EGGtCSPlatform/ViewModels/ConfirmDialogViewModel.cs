using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public partial class ConfirmDialogViewModel : DialogViewModel
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _confirmText = "确认";

    [ObservableProperty]
    private string _cancelText = "取消";

    [ObservableProperty]
    private bool _showCancelButton;

    [ObservableProperty]
    private bool _showCloseButton = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _busy;

    public bool NotBusy() => !Busy;

    [ObservableProperty]
    private bool _confirmed;

    [JsonIgnore]
    public Func<ConfirmDialogViewModel, Task<bool>> OnConfirm { get; set; } =
        (_) => Task.FromResult(true);

    public ConfirmDialogViewModel(DialogKind kind = DialogKind.SecondaryConfirmation)
    {
        Kind = kind;
        _title = kind switch
        {
            DialogKind.SecondaryConfirmation => "二级确认",
            DialogKind.Success => "成功提示",
            DialogKind.Error => "错误提示",
            DialogKind.Risk => "风险提示",
            _ => "提示",
        };
        _showCancelButton = kind == DialogKind.SecondaryConfirmation;
    }

    public DialogKind Kind { get; }

    public bool IsSecondaryConfirmation => Kind == DialogKind.SecondaryConfirmation;

    public bool IsSuccess => Kind == DialogKind.Success;

    public bool IsError => Kind == DialogKind.Error;

    public bool IsRisk => Kind == DialogKind.Risk;

    public override bool AllowOverlayDismiss =>
        ShowCloseButton && !Busy && Kind is DialogKind.SecondaryConfirmation or DialogKind.Success;

    protected override void OnOverlayDismiss()
    {
        Confirmed = false;
        Close();
    }

    [RelayCommand]
    public async Task ConfirmAsync()
    {
        if (Busy)
            return;

        Busy = true;
        StatusText = string.Empty;
        ProgressText = "处理中...";

        bool result;
        try
        {
            result = await OnConfirm(this);
        }
        finally
        {
            Busy = false;
        }

        if (!result)
            return;

        Confirmed = true;
        Close();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task Cancel()
    {
        Confirmed = false;
        Close();

        return Task.CompletedTask;
    }
}
