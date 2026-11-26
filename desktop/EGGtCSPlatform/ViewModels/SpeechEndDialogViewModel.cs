using CommunityToolkit.Mvvm.Input;

namespace EGGtCSPlatform.ViewModels;

public enum SpeechEndChoice
{
    Cancel,
    Training,
    Experiment,
}

public sealed class SpeechEndDialogViewModel : DialogViewModel
{
    public SpeechEndDialogViewModel(int savedSentences, bool canEndTraining)
    {
        CanEndTraining = canEndTraining;
        Message = canEndTraining
            ? $"已保存 {savedSentences} 句结果。中断训练将保留已保存记录，但本轮不计入有效完成统计和对比。未提交评分不保存。"
            : "当前没有进行中的训练。结束实验后可查看全部训练结果。";
        EndTrainingCommand = new RelayCommand(
            () => Finish(SpeechEndChoice.Training),
            () => CanEndTraining
        );
        EndExperimentCommand = new RelayCommand(() => Finish(SpeechEndChoice.Experiment));
    }

    public string Message { get; }
    public bool CanEndTraining { get; }
    public SpeechEndChoice Choice { get; private set; }
    public RelayCommand EndTrainingCommand { get; }
    public RelayCommand EndExperimentCommand { get; }

    private void Finish(SpeechEndChoice choice)
    {
        Choice = choice;
        Close();
    }
}
