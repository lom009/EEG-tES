using Avalonia;
using Avalonia.Controls;

namespace EGGtCSPlatform.Dialog;

/// <summary>Hides native child windows while the owning window has an Avalonia modal overlay.</summary>
public sealed class NativeContentModalState : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsBlockedProperty =
        AvaloniaProperty.RegisterAttached<NativeContentModalState, Control, bool>(
            "IsBlocked", inherits: true);

    public static bool GetIsBlocked(Control control) => control.GetValue(IsBlockedProperty);
    public static void SetIsBlocked(Control control, bool value) => control.SetValue(IsBlockedProperty, value);
}
