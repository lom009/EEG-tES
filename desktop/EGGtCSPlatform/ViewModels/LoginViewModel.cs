using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels;

public partial class LoginViewModel(
    IAuthenticationService authenticationService,
    IDeviceBackendModeToggleService backendModeToggleService,
    TimeProvider timeProvider
) : ViewModelBase
{
    private static readonly TimeSpan BackendToggleClickWindow = TimeSpan.FromSeconds(3);
    private const int BackendToggleClickCount = 5;
    private readonly Queue<DateTimeOffset> _companyNameClicks = new();
    private bool _isChangingBackendMode;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public event Action? LoginSucceeded;

    public event Action? ExitRequested;

    [RelayCommand]
    private async Task CompanyNameClickedAsync()
    {
        if (_isChangingBackendMode)
            return;

        var now = timeProvider.GetUtcNow();
        while (_companyNameClicks.TryPeek(out var click) && now - click > BackendToggleClickWindow)
        {
            _companyNameClicks.Dequeue();
        }
        _companyNameClicks.Enqueue(now);
        if (_companyNameClicks.Count < BackendToggleClickCount)
            return;

        _companyNameClicks.Clear();
        _isChangingBackendMode = true;
        ErrorMessage = string.Empty;
        try
        {
            await backendModeToggleService.ToggleAsync();
            ExitRequested?.Invoke();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"切换设备模式失败：{exception.Message}";
        }
        finally
        {
            _isChangingBackendMode = false;
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        ErrorMessage = string.Empty;

        if (await authenticationService.SignInAsync(Username, Password))
        {
            LoginSucceeded?.Invoke();
            return;
        }

        ErrorMessage = "用户名或密码错误";
    }
}
