using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Persistence;

namespace EGGtCSPlatform.ViewModels;

public partial class ToleranceSubjectDialogViewModel(
    IExperimentPersistenceService persistence,
    ISubjectLookupService subjects
) : DialogViewModel
{
    [ObservableProperty]
    private string _subjectId = "";

    [ObservableProperty]
    private string _remarks = "";

    [ObservableProperty]
    private string _errorText = "";
    public bool Confirmed { get; private set; }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var candidate = persistence.CreateRandomSubjectCode();
                if (await subjects.ExistsAsync(candidate))
                    continue;
                SubjectId = candidate;
                ErrorText = "";
                return;
            }
            ErrorText = "暂时无法生成唯一被试 ID，请重试。";
        }
        catch (Exception)
        {
            ErrorText = "无法查询被试信息，请重试。";
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (GenerateCommand.IsRunning)
            return;
        if (
            string.IsNullOrWhiteSpace(SubjectId)
            || SubjectId.Trim().Length > 64
            || SubjectId.Trim().Any(char.IsControl)
        )
        {
            ErrorText = "请输入 1～64 个字符的被试 ID。";
            return;
        }
        if (Remarks.Trim().Length > 1000)
        {
            ErrorText = "备注不能超过 1000 个字符。";
            return;
        }
        SubjectId = SubjectId.Trim();
        Remarks = Remarks.Trim();
        Confirmed = true;
        Close();
    }
}
