using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public partial class ToleranceTestPageViewModel : PageViewModel, IDisposable
{
    private readonly ToleranceTestRouteData _route;
    private IToleranceSession? _session;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private bool _isOperating;
    private readonly IToleranceSessionFactory _sessions;
    private readonly ITolerancePersistenceService _persistence;
    private bool _disposed;
    private bool _suspended;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _statusText = "";

    public ToleranceTestPageViewModel(
        ToleranceTestRouteData route,
        IToleranceSessionFactory sessions,
        ITolerancePersistenceService persistence
    )
        : base(ApplicationPageNames.ToleranceTest, "耐受度测试")
    {
        _route = route;
        _sessions = sessions;
        CreateSession();
        _persistence = persistence;
    }

    public override IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges =>
        [new("被试ID", SubjectId)];
    public override bool CanGoBack => !IsSaving;
    public override bool RequiresAsyncNavigation => true;

    public override async Task<bool> PrepareToLeaveAsync()
    {
        if (IsSaving)
            return false;
        try
        {
            if (_session is not null)
                await _session.StopAsync();
            return true;
        }
        catch (Exception e)
        {
            StatusText = $"停止失败：{e.Message}";
            return false;
        }
        finally
        {
            Changed();
        }
    }

    public string SubjectId => _route.Context.SubjectId;
    public string Summary => ToleranceDisplay.Configuration(_route.Configuration).Replace(" · ", "，");
    public string Sites => ToleranceDisplay.Sites(_route, includeChannel: false);
    public string ParameterSummary =>
        StimulusParameterPolicy
            .For(_route.Configuration.Kind, _route.Configuration.ShamMode)
            .FormatSummary(
                _route.Configuration.Frequency,
                _route.Configuration.RampSeconds,
                _route.Configuration.DutyPercent
            );
    public double Current => _session?.Current ?? 0;
    public string ModeText =>
        _session is null ? "测试不可用"
        : _session.IsSimulated ? "模拟调流"
        : "设备调流";
    public string CurrentText => $"{Current:0.00}";
    public string LimitsText =>
        $"测试上限 {(_session?.Maximum ?? 0):0.##} mA，当前步长 {(_session?.Step ?? .04) * 1000:0} μA";
    public string CurrentMeaning =>
        _route.Configuration.Targets.Count > 1 ? "每个靶点峰值 · 所有靶点同步调节" : "峰值电流";
    public ObservableCollection<ToleranceResultSnapshot> Records { get; } = [];
    public IReadOnlyList<ToleranceResultRow> RecordRows =>
        Records.Select((r, i) => new ToleranceResultRow(i + 1, r)).ToArray();
    public bool HasRecords => Records.Count > 0;
    public bool CanAdjust =>
        !IsSaving && !_isOperating && !_disposed && !_suspended && _session?.IsRunning == true;
    public bool CanSave =>
        !IsSaving
        && !_isOperating
        && !_disposed
        && !_suspended
        && _session?.HasConfirmedCurrent == true;
    public bool CanIncrease => CanAdjust && Current < (_session?.Maximum ?? 0);
    public bool CanDecrease => CanAdjust && Current > (_session?.Minimum ?? 0);

    [RelayCommand(CanExecute = nameof(CanIncrease))]
    private Task IncreaseAsync() =>
        CanIncrease ? OperateAsync(s => s.AdjustAsync(1)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanDecrease))]
    private Task DecreaseAsync() =>
        CanDecrease ? OperateAsync(s => s.AdjustAsync(-1)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAdjust))]
    private Task ResetAsync() => CanAdjust ? OperateAsync(s => s.ResetAsync()) : Task.CompletedTask;

    private async Task OperateAsync(Func<IToleranceSession, Task> action)
    {
        if (_session is null || _isOperating || _disposed || _suspended)
            return;
        var session = _session;
        _isOperating = true;
        Changed();
        try
        {
            await action(session);
            StatusText = session.Status;
        }
        catch (OperationCanceledException)
        {
            StatusText = session.Status;
        }
        catch (Exception e)
        {
            StatusText = $"操作失败：{e.Message}";
        }
        finally
        {
            _isOperating = false;
            Changed();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave || _session is null)
            return;
        IsSaving = true;
        StatusText = "正在保存…";
        try
        {
            var saved = await _persistence.SaveAsync(
                _route,
                new ToleranceSaveData(Current, _session.IsSimulated, _session.Configuration)
            );
            Records.Add(saved);
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(RecordRows));
            StatusText = $"已保存第 {Records.Count} 次耐受度结果";
        }
        catch (Exception e)
        {
            StatusText = $"保存失败：{e.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(CanAdjust));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(CanIncrease));
        OnPropertyChanged(nameof(CanDecrease));
        IncreaseCommand.NotifyCanExecuteChanged();
        DecreaseCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    public override void OnNavigatedFrom()
    {
        if (_disposed || _suspended)
            return;
        _suspended = true;
        if (_session is not null)
        {
            _session.Changed -= SessionChanged;
            _session.Dispose();
        }
        Changed();
    }

    public void Resume() => _ = ResumeAsync();

    public async Task ResumeAsync()
    {
        if (_disposed)
            return;
        if (_suspended)
        {
            _suspended = false;
            Records.Clear();
            StatusText = "";
            CreateSession();
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(RecordRows));
        }
        await OperateAsync(s => s.StartAsync());
    }

    private void CreateSession()
    {
        try
        {
            _session = _sessions.Create(_route);
            _session.Changed += SessionChanged;
            StatusText = _session.Status;
        }
        catch (Exception e)
        {
            _session = null;
            StatusText = e.Message;
        }
    }

    private void SessionChanged()
    {
        var session = _session;
        void Update()
        {
            if (_disposed || _suspended || !ReferenceEquals(session, _session))
                return;
            StatusText = session?.Status ?? StatusText;
            Changed();
        }
        if (_ui is not null && SynchronizationContext.Current != _ui)
            _ui.Post(_ => Update(), null);
        else
            Update();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_session is not null)
        {
            _session.Changed -= SessionChanged;
            _session.Dispose();
        }
        Changed();
    }
}
