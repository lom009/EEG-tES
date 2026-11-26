using Avalonia.Controls;

namespace EGGtCSPlatform.Views.Pages;

public partial class ToleranceHistoryView : UserControl
{
    // Return fresh definitions for each grid; header and group rows share the same layout specification.
    public static ColumnDefinitions GroupColumns => new("1.1*,1.2*,1.7*,70,100,180,112");

    public ToleranceHistoryView() => InitializeComponent();
}
