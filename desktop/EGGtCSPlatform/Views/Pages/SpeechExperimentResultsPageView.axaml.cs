using System;
using Avalonia.Controls;

namespace EGGtCSPlatform.Views.Pages;

public partial class SpeechExperimentResultsPageView : UserControl
{
    public SpeechExperimentResultsPageView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ContentGrid.MaxHeight = Math.Max(700, Bounds.Height - 24);
    }
}
