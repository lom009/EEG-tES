using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels;

public partial class ApplicationUpdateViewModel(
    IApplicationUpdateClient updateClient,
    IApplicationShutdownCoordinator shutdownCoordinator
) : ViewModelBase
{
    private enum UpdateDialogState
    {
        Available,
        CheckFailed,
        DownloadFailed,
        ApplyFailed,
        NoUpdate,
    }

    private UpdateDialogState _state;
    private AvailableApplicationUpdate? _update;
    private CancellationTokenSource? _downloadCancellation;

    [ObservableProperty]
    private string _title = "软件更新";

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    private bool _busy;

    [ObservableProperty]
    private string _primaryButtonText = "立即更新";

    [ObservableProperty]
    private bool _showSecondaryButton = true;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    private bool _canCancelDownload;

    public string ProgressText => $"{Progress}%";

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanClose => !Busy;

    public event Action? CloseRequested;

    internal void ShowAvailableUpdate(AvailableApplicationUpdate update)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _state = UpdateDialogState.Available;
        Title = "发现新版本";
        Message = $"当前版本 v{update.CurrentVersion}，可更新至 v{update.TargetVersion}。";
        ErrorMessage = string.Empty;
        StatusText = "更新将在下载完成后自动安装并重启程序。";
        Progress = 0;
        ShowProgress = false;
        PrimaryButtonText = "立即更新";
        ShowSecondaryButton = true;
    }

    internal void ShowCheckFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _state = UpdateDialogState.CheckFailed;
        _update = null;
        Title = "检查更新失败";
        Message = "暂时无法连接到配置的更新源。";
        ErrorMessage = exception.Message;
        StatusText = string.Empty;
        Progress = 0;
        ShowProgress = false;
        PrimaryButtonText = "重试";
        ShowSecondaryButton = true;
    }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (Busy)
            return;

        switch (_state)
        {
            case UpdateDialogState.Available:
            case UpdateDialogState.DownloadFailed:
                await DownloadAndApplyAsync();
                break;
            case UpdateDialogState.CheckFailed:
                await RetryCheckAsync();
                break;
            case UpdateDialogState.ApplyFailed:
                await ApplyAsync();
                break;
            case UpdateDialogState.NoUpdate:
                CloseRequested?.Invoke();
                break;
        }
    }

    [RelayCommand]
    private void Later()
    {
        if (!Busy)
            CloseRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload()
    {
        var cancellation = _downloadCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
            return;

        CanCancelDownload = false;
        StatusText = "正在取消下载…";
        cancellation.Cancel();
    }

    private async Task RetryCheckAsync()
    {
        Busy = true;
        ShowSecondaryButton = false;
        ErrorMessage = string.Empty;
        StatusText = "正在重新检查更新…";
        try
        {
            var update = await updateClient.CheckForUpdatesAsync();
            if (update is null)
            {
                _state = UpdateDialogState.NoUpdate;
                _update = null;
                Title = "已是最新版本";
                Message = "当前没有可用更新。";
                StatusText = string.Empty;
                PrimaryButtonText = "关闭";
                ShowSecondaryButton = false;
                return;
            }

            ShowAvailableUpdate(update);
        }
        catch (Exception exception)
        {
            ShowCheckFailure(exception);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task DownloadAndApplyAsync()
    {
        var update = _update;
        if (update is null)
        {
            ShowCheckFailure(new InvalidOperationException("更新信息已失效，请重新检查。"));
            return;
        }

        using var downloadCancellation = new CancellationTokenSource();
        _downloadCancellation = downloadCancellation;
        Busy = true;
        IsDownloading = true;
        CanCancelDownload = true;
        ShowSecondaryButton = false;
        ErrorMessage = string.Empty;
        ShowProgress = true;
        StatusText = "正在下载更新…";
        Progress = 0;

        try
        {
            await WaitForDownloadUiAsync();
            downloadCancellation.Token.ThrowIfCancellationRequested();
            await updateClient.DownloadAsync(
                update,
                value => ReportProgress(value, downloadCancellation.Token),
                downloadCancellation.Token
            );
            downloadCancellation.Token.ThrowIfCancellationRequested();
            IsDownloading = false;
            CanCancelDownload = false;
            ReportProgress(100);
            await ApplyAsyncCore();
        }
        catch (OperationCanceledException) when (downloadCancellation.IsCancellationRequested)
        {
            ShowAvailableUpdate(update);
            StatusText = "下载已取消，可重新下载。";
        }
        catch (Exception exception)
        {
            _state = UpdateDialogState.DownloadFailed;
            Title = "更新下载失败";
            Message = $"无法下载 v{update.TargetVersion}，请检查更新源后重试。";
            ErrorMessage = exception.Message;
            StatusText = string.Empty;
            ShowProgress = false;
            PrimaryButtonText = "重试";
            ShowSecondaryButton = true;
        }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, downloadCancellation))
                _downloadCancellation = null;
            CanCancelDownload = false;
            IsDownloading = false;
            Busy = false;
        }
    }

    private async Task ApplyAsync()
    {
        Busy = true;
        ShowSecondaryButton = false;
        ErrorMessage = string.Empty;
        try
        {
            await ApplyAsyncCore();
        }
        catch (Exception exception)
        {
            ShowApplyFailure(exception);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ApplyAsyncCore()
    {
        if (_update is null)
            throw new InvalidOperationException("更新信息已失效，请重新检查。");

        Title = "准备安装更新";
        Message = $"v{_update.TargetVersion} 已下载完成，程序即将重启。";
        StatusText = "正在安全结束当前会话…";
        ShowProgress = true;
        Progress = 100;

        try
        {
            await shutdownCoordinator.ShutdownAsync("application-update");
            StatusText = "正在启动更新程序…";
            updateClient.ApplyAndRestart(_update);
        }
        catch (Exception exception)
        {
            ShowApplyFailure(exception);
        }
    }

    private void ShowApplyFailure(Exception exception)
    {
        _state = UpdateDialogState.ApplyFailed;
        Title = "无法应用更新";
        Message = "更新已经下载，但启动安装程序失败。";
        ErrorMessage = exception.Message;
        StatusText = string.Empty;
        ShowProgress = false;
        PrimaryButtonText = "重试";
        ShowSecondaryButton = true;
    }

    private static async Task WaitForDownloadUiAsync()
    {
        if (Application.Current is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private void ReportProgress(int value, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var progress = Math.Clamp(value, 0, 100);
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            Progress = Math.Max(Progress, progress);
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    Progress = Math.Max(Progress, progress);
            },
            DispatcherPriority.Normal
        );
    }
}
