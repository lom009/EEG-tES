using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels.Pages;

public sealed partial class EnvelopeTrainingCharacterViewModel(string text, Func<bool> canScore, Action changed) : ViewModelBase
{
    public string Text { get; } = text;
    public bool IsScoringEnabled => canScore();
    [ObservableProperty] private bool? _isCorrect;
    public bool IsCorrectScore => IsCorrect == true;
    public bool IsIncorrectScore => IsCorrect == false;
    public string ScoreLabel => IsCorrect switch { true => "正确", false => "错误", _ => "待评分" };
    public void SetScore(bool? score)
    {
        IsCorrect = score; OnPropertyChanged(nameof(IsCorrectScore)); OnPropertyChanged(nameof(IsIncorrectScore));
        OnPropertyChanged(nameof(ScoreLabel));
    }
    [RelayCommand(CanExecute = nameof(CanScore))] private void Correct() { if (!CanScore()) return; SetScore(true); changed(); }
    [RelayCommand(CanExecute = nameof(CanScore))] private void Incorrect() { if (!CanScore()) return; SetScore(false); changed(); }
    private bool CanScore() => canScore();
    public void RefreshCommands() { OnPropertyChanged(nameof(IsScoringEnabled)); CorrectCommand.NotifyCanExecuteChanged(); IncorrectCommand.NotifyCanExecuteChanged(); }
}
