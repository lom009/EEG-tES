using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EGGtCSPlatform.ViewModels;

public partial class DialogViewModel : ViewModelBase
{
    private TaskCompletionSource<bool> _closeTask = CreateCloseTask();

    [ObservableProperty]
    private bool _isDialogOpen;

    public Task WaitAsync() => _closeTask.Task;

    public void Show()
    {
        if (_closeTask.Task.IsCompleted)
            _closeTask = CreateCloseTask();

        IsDialogOpen = true;
    }

    public void Close()
    {
        IsDialogOpen = false;
        _closeTask.TrySetResult(true);
    }

    public virtual bool AllowOverlayDismiss => true;

    protected virtual void OnOverlayDismiss() => Close();

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OverlayDismiss()
    {
        if (AllowOverlayDismiss)
            OnOverlayDismiss();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Dismiss() => Close();

    private static TaskCompletionSource<bool> CreateCloseTask() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
