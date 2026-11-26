using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.ViewModels;

public sealed partial class SpeechCopyDialogViewModel : DialogViewModel
{
    public SpeechCopyDialogViewModel(IReadOnlyList<SpeechRoundRow> rounds, SpeechRoundRow? selected)
    {
        Rounds = rounds;
        _selected = selected;
        ConfirmCommand = new RelayCommand(() =>
        {
            Confirmed = Selected is not null;
            if (Confirmed)
                Close();
        });
    }

    public IReadOnlyList<SpeechRoundRow> Rounds { get; }

    [ObservableProperty]
    private SpeechRoundRow? _selected;
    public bool Confirmed { get; private set; }
    public RelayCommand ConfirmCommand { get; }
}
