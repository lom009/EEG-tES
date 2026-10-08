using EGGtCSPlatform.ViewModels;

namespace EGGtCSPlatform.Interfaces;

public interface IDialogProvider
{
    System.Collections.ObjectModel.ObservableCollection<DialogViewModel> DialogStack { get; }
}
