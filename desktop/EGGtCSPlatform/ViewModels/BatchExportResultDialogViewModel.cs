using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public sealed record BatchExportFailureViewModel(
    string ExperimentId,
    string StartedAt,
    string Message
);

public partial class BatchExportResultDialogViewModel(
    int successCount,
    int fileCount,
    IReadOnlyList<BatchExportFailureViewModel> failures,
    string directoryPath
) : DialogViewModel
{
    public int SuccessCount { get; } = successCount;
    public int FileCount { get; } = fileCount;
    public IReadOnlyList<BatchExportFailureViewModel> Failures { get; } = failures;
    public int FailureCount => Failures.Count;
    public bool HasFailures => FailureCount > 0;
    public string DirectoryPath { get; } = directoryPath;
    public string Summary =>
        $"成功 {SuccessCount} 条，生成 {FileCount} 个文件，失败 {FailureCount} 条";

    [RelayCommand]
    private void Confirm() => Close();
}
