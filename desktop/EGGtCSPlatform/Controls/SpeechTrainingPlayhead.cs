using System;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using EGGtCSPlatform.SpeechTraining;

namespace EGGtCSPlatform.Controls;

/// <summary>A static marker moved by one compositor animation for the whole timeline.</summary>
public sealed class SpeechTrainingPlayhead : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SpeechTrainingPlayhead, double>(nameof(Progress));
    public static readonly StyledProperty<SpeechTimelineAnimation?> TimelineAnimationProperty =
        AvaloniaProperty.Register<SpeechTrainingPlayhead, SpeechTimelineAnimation?>(nameof(TimelineAnimation));

    private CompositionSolidColorVisual? _visual;
    private SpeechTimelineAnimation? _submitted;
    private Size _submittedSize;
    private static readonly ImmutablePen Pen = new(new ImmutableSolidColorBrush(Color.Parse("#2563EB")), 1);

    internal int AnimationSubmissionCount { get; private set; }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public SpeechTimelineAnimation? TimelineAnimation
    {
        get => GetValue(TimelineAnimationProperty);
        set => SetValue(TimelineAnimationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TimelineAnimationProperty || change.Property == ProgressProperty
            || change.Property == BoundsProperty)
            UpdateAnimation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            _visual = compositor.CreateSolidColorVisual();
            _visual.Color = Color.Parse("#2563EB");
            ElementComposition.SetElementChildVisual(this, _visual);
            UpdateAnimation();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _visual?.StopAnimation("Offset");
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
        _submitted = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateAnimation()
    {
        if (_visual is null)
        {
            InvalidateVisual();
            return;
        }
        var timeline = TimelineAnimation;
        // Completion notifications must not restart or snap an existing animation.
        if (timeline is not null && ReferenceEquals(timeline, _submitted) && Bounds.Size == _submittedSize)
            return;
        _submitted = timeline;
        _submittedSize = Bounds.Size;
        _visual.StopAnimation("Offset");
        _visual.Size = new(1, Math.Max(0, Bounds.Height));
        var distance = (float)Math.Max(0, Bounds.Width - 1);
        if (timeline is null || timeline.Duration <= TimeSpan.Zero)
        {
            _visual.Offset = new(distance * Normalize(Progress), 0, 0);
            return;
        }

        // Compute once. Resize/late attachment resumes the same timeline.
        // No audio, device, timer or frame callback drives the marker's movement.
        var motion = SpeechTimelineMotion.From(timeline, Progress);
        var remaining = motion.Remaining;
        _visual.Offset = new(distance, 0, 0);
        if (remaining <= TimeSpan.Zero)
            return;
        var animation = _visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = motion.AnimationDuration;
        animation.IterationCount = 1;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.InsertKeyFrame(0, new Vector3(distance * (float)motion.Progress, 0, 0));
        animation.InsertKeyFrame(1, new Vector3(distance, 0, 0), new LinearEasing());
        _visual.StartAnimation("Offset", animation);
        AnimationSubmissionCount++;
    }

    private static double Normalize(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_visual is null)
        {
            var x = Math.Max(0, Bounds.Width - 1) * Normalize(Progress) + .5;
            context.DrawLine(Pen, new Point(x, 0), new Point(x, Bounds.Height));
        }
    }
}
