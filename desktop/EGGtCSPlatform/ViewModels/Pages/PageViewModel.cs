using System;
using System.Collections.Generic;
using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.ViewModels.Pages;

public abstract class PageViewModel(ApplicationPageNames pageName, string pageTitle) : ViewModelBase
{
    private string _pageTitle = pageTitle;

    public ApplicationPageNames PageName { get; } = pageName;

    public virtual IReadOnlyList<PageHeaderBadgeViewModel> HeaderBadges { get; } = [];

    public virtual object? HeaderAction => null;

    public virtual bool CanGoBack => true;

    public string PageTitle
    {
        get => _pageTitle;
        protected set => SetProperty(ref _pageTitle, value);
    }
}

public sealed class PageHeaderBadgeViewModel
{
    private readonly string? _displayTextOverride;

    public PageHeaderBadgeViewModel(string label, string value)
        : this(label, value, null) { }

    private PageHeaderBadgeViewModel(string label, string value, string? displayTextOverride)
    {
        Label = label;
        Value = value;
        _displayTextOverride = displayTextOverride;
    }

    public string Label { get; }

    public string Value { get; }

    public string DisplayText => _displayTextOverride ?? $"{Label}：{Value}";

    public static PageHeaderBadgeViewModel CreateExperimentMode(ExperimentCreationMode mode)
    {
        var displayText = mode switch
        {
            ExperimentCreationMode.StimulusOnly => "单刺激模式",
            ExperimentCreationMode.AcquisitionOnly => "单采集模式",
            ExperimentCreationMode.AcquisitionAndStimulation => "采集-刺激模式",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的实验模式。"),
        };
        return new PageHeaderBadgeViewModel("模式", displayText, displayText);
    }
}
