using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using EGGtCSPlatform.Dialog;
using EGGtCSPlatform.Interfaces;

namespace EGGtCSPlatform.Views.Pages;

public partial class MainView : UserControl
{
    private readonly KeyBinding _dismissDialogBinding;
    private TopLevel? _dialogTopLevel;

    public MainView()
    {
        InitializeComponent();
        var dialogs = new DialogAdornerHost();
        // The adorner has a different visual parent; keep its context tied to this view.
        dialogs.Bind(DataContextProperty, new Binding(nameof(DataContext)) { Source = this });
        // SetAdorner owns attachment/detachment and tracks the adorned root's bounds.
        AdornerLayer.SetAdorner(DialogAdornedRoot, dialogs);
        _dismissDialogBinding = new KeyBinding
        {
            Gesture = new KeyGesture(Key.Escape),
            Command = new RelayCommand(
                () =>
                {
                    if (DataContext is IDialogProvider provider && provider.DialogStack.Count > 0)
                        provider.DialogStack[^1].OverlayDismissCommand.Execute(null);
                },
                () => DataContext is IDialogProvider provider && provider.DialogStack.Count > 0
            ),
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Focus can remain on the window when opening a dialog. A binding inside
        // MainView or its adorner is then absent from the keyboard's ancestor route.
        _dialogTopLevel = TopLevel.GetTopLevel(this);
        _dialogTopLevel?.KeyBindings.Add(_dismissDialogBinding);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _dialogTopLevel?.KeyBindings.Remove(_dismissDialogBinding);
        _dialogTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }
}
