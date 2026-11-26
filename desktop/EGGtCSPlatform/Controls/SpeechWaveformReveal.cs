using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

/// <summary>Clips cached curve content on the compositor without changing child layout.</summary>
public sealed class SpeechWaveformReveal : Decorator
{
    public static readonly StyledProperty<bool> RealtimeEnabledProperty =
        AvaloniaProperty.Register<SpeechWaveformReveal, bool>(nameof(RealtimeEnabled));
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SpeechWaveformReveal, double>(nameof(Progress));
    public static readonly StyledProperty<SpeechTimelineAnimation?> TimelineAnimationProperty =
        AvaloniaProperty.Register<SpeechWaveformReveal, SpeechTimelineAnimation?>(nameof(TimelineAnimation));

    public bool RealtimeEnabled { get => GetValue(RealtimeEnabledProperty); set => SetValue(RealtimeEnabledProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public SpeechTimelineAnimation? TimelineAnimation { get => GetValue(TimelineAnimationProperty); set => SetValue(TimelineAnimationProperty, value); }
    private CompositionVisual? _visual;
    private SpeechTimelineAnimation? _submitted;
    private Size _size;
    private bool _enabled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visual = ElementComposition.GetElementVisual(this);
        UpdateClip();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.StopAnimation("Size");
        _visual = null;
        _submitted = null;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == ProgressProperty
            || change.Property == TimelineAnimationProperty || change.Property == RealtimeEnabledProperty)
            UpdateClip();
    }
    private void UpdateClip()
    {
        ClipToBounds = RealtimeEnabled;
        if (_visual is null) return;
        var timeline = TimelineAnimation;
        if (timeline is not null && ReferenceEquals(timeline, _submitted)
            && _size == Bounds.Size && _enabled == RealtimeEnabled) return;
        _submitted = timeline;
        _size = Bounds.Size;
        _enabled = RealtimeEnabled;
        _visual.StopAnimation("Size");
        _visual.ClipToBounds = RealtimeEnabled;
        var motion = SpeechTimelineMotion.From(timeline, Progress);
        var width = Math.Max(0, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);
        // The marker travels width - 1 so its last pixel stays inside the plot.
        var revealed = motion.Progress >= 1 ? width : Math.Max(0, width - 1) * motion.Progress;
        Clip = RealtimeEnabled && motion.Remaining <= TimeSpan.Zero
            ? new RectangleGeometry(new Rect(0, 0, revealed, height)) : null;
        _visual.Size = new(RealtimeEnabled ? revealed : width, height);
        if (!RealtimeEnabled || motion.Remaining <= TimeSpan.Zero) return;
        var animation = _visual.Compositor.CreateVectorKeyFrameAnimation();
        animation.Duration = motion.AnimationDuration;
        animation.IterationCount = 1;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.InsertKeyFrame(0, new Vector(revealed, height));
        animation.InsertKeyFrame(1, new Vector(width, height), new LinearEasing());
        _visual.Size = new(width, height);
        _visual.StartAnimation("Size", animation);
    }
}
