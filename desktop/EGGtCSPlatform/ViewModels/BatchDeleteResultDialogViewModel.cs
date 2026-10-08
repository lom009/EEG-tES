using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public sealed record BatchDeleteFailureViewModel(
    string ExperimentId,
    string StartedAt,
    string Message
);

public partial class BatchDeleteResultDialogViewModel(
    int successCount,
    IReadOnlyList<BatchDeleteFailureViewModel> failures
) : DialogViewModel
{
    public int SuccessCount { get; } = successCount;
    public IReadOnlyList<BatchDeleteFailureViewModel> Failures { get; } = failures;
    public int FailureCount => Failures.Count;
    public bool HasFailures => FailureCount > 0;
    public string Summary => $"成功 {SuccessCount} 条，失败 {FailureCount} 条";

    [RelayCommand]
    private void Confirm() => Close();
}
