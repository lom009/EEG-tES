using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.Controls;

/// <summary>Progressive preview of the exact playback PCM and quantized SDK envelope, not measured EEG.</summary>
public sealed class EnvelopeTrainingWaveform : Control
{
    public static readonly StyledProperty<EnvelopeTrainingAudio?> AudioProperty =
        AvaloniaProperty.Register<EnvelopeTrainingWaveform, EnvelopeTrainingAudio?>(nameof(Audio));
    public static readonly StyledProperty<double> AudioSecondsProperty = AvaloniaProperty.Register<EnvelopeTrainingWaveform, double>(nameof(AudioSeconds));
    public static readonly StyledProperty<double> StimulationSecondsProperty = AvaloniaProperty.Register<EnvelopeTrainingWaveform, double>(nameof(StimulationSeconds));
    public static readonly StyledProperty<double> DelaySecondsProperty = AvaloniaProperty.Register<EnvelopeTrainingWaveform, double>(nameof(DelaySeconds));
    public static readonly StyledProperty<double> MaximumCurrentProperty = AvaloniaProperty.Register<EnvelopeTrainingWaveform, double>(nameof(MaximumCurrent), 2);
    public EnvelopeTrainingAudio? Audio { get => GetValue(AudioProperty); set => SetValue(AudioProperty, value); }
    public double AudioSeconds { get => GetValue(AudioSecondsProperty); set => SetValue(AudioSecondsProperty, value); }
    public double StimulationSeconds { get => GetValue(StimulationSecondsProperty); set => SetValue(StimulationSecondsProperty, value); }
    public double DelaySeconds { get => GetValue(DelaySecondsProperty); set => SetValue(DelaySecondsProperty, value); }
    public double MaximumCurrent { get => GetValue(MaximumCurrentProperty); set => SetValue(MaximumCurrentProperty, value); }
    static EnvelopeTrainingWaveform() => AffectsRender<EnvelopeTrainingWaveform>(AudioProperty, AudioSecondsProperty,
        StimulationSecondsProperty, DelaySecondsProperty, MaximumCurrentProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 120 || Bounds.Height < 100) return;
        var height = (Bounds.Height - 48) / 2;
        var audioPlot = new Rect(68, 30, Bounds.Width - 86, height - 30);
        var stimulusPlot = new Rect(68, height + 30, Bounds.Width - 86, height - 30);
        context.FillRectangle(Brush.Parse("#FAFBFC"), new Rect(0, height + 16, Bounds.Width, height + 16));
        var duration = Audio is null ? 4 : Math.Max(.1, Math.Max(Audio.Duration.TotalSeconds,
            (double)Audio.EnvelopeSamples.Length / Audio.EnvelopeSampleRateHz) + DelaySeconds);
        var amplitude = Audio is null ? .4 : Math.Max(.4, Math.Ceiling(Audio.WaveSamples.Max() * 10) / 10);
        Axes(context, audioPlot, amplitude, duration);
        Axes(context, stimulusPlot, Math.Max(.1, MaximumCurrent), duration);
        Text(context, "Amplitude", 14, 8, "#ACB7C8", 10);
        Text(context, "mA", 16, height + 9, "#ACB7C8", 10);
        Text(context, "语\n音\n包\n络", 16, 51, "#232B38", 12);
        Text(context, "计\n划\n刺\n激", 16, height + 47, "#232B38", 12);
        Text(context, "时间(s)", 15, Bounds.Height - 26, "#ACB7C8", 10);
        for (var i = 0; i <= 8; i++)
            Text(context, (duration * i / 8).ToString("0.#", CultureInfo.InvariantCulture),
                audioPlot.Left + audioPlot.Width * i / 8 - 4, Bounds.Height - 26, "#6B7D99", 11);
        if (Audio is null) return;
        DrawSignal(context, audioPlot, Audio.WaveSamples, Audio.Duration.TotalSeconds, duration,
            Math.Min(AudioSeconds, Audio.Duration.TotalSeconds), 0, amplitude, "#83CEFF");
        DrawEnvelope(context, audioPlot, Audio.AudioEnvelope, Audio.Duration.TotalSeconds, duration,
            Math.Min(AudioSeconds, Audio.Duration.TotalSeconds), 0, amplitude, "#FD5B38", false);
        var current = Audio.EnvelopeSamples.Select(sample => sample * .033).ToArray();
        DrawEnvelope(context, stimulusPlot, current, (double)current.Length / Audio.EnvelopeSampleRateHz,
            duration, Math.Max(0, StimulationSeconds - DelaySeconds), DelaySeconds,
            Math.Max(.1, MaximumCurrent), "#9536F3", true);
    }
    private static void Axes(DrawingContext context, Rect plot, double maximum, double duration)
    {
        var pen = new Pen(Brush.Parse("#E2E8F2"), 1);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + plot.Height * i / 4;
            context.DrawLine(pen, new(plot.Left, y), new(plot.Right, y));
            Text(context, (maximum * (1 - i / 2d)).ToString("0.##", CultureInfo.InvariantCulture), plot.Left - 28, y - 7, "#ACB7C8", 11);
        }
        for (var i = 0; i <= 8; i++)
        {
            var x = plot.Left + plot.Width * i / 8;
            context.DrawLine(pen, new(x, plot.Top), new(x, plot.Bottom));
        }
    }
    private static void DrawSignal(DrawingContext context, Rect plot, double[] samples, double sampleDuration,
        double duration, double visibleSeconds, double offset, double maximum, string color)
    {
        if (visibleSeconds <= 0) return;
        using var clip = context.PushClip(plot);
        var pen = new Pen(Brush.Parse(color), 1);
        var count = Math.Min(samples.Length, (int)Math.Ceiling(visibleSeconds / sampleDuration * samples.Length));
        for (var i = 0; i < count; i++)
        {
            var x = plot.Left + (offset + sampleDuration * i / samples.Length) / duration * plot.Width;
            var magnitude = samples[i] / maximum * plot.Height / 2;
            context.DrawLine(pen, new(x, plot.Center.Y - magnitude), new(x, plot.Center.Y + magnitude));
        }
    }
    // Draw the data-derived envelope, with Figma's dashed lower stimulation boundary.
    // No carrier waveform or measured current is fabricated from the magnitude-only SDK samples.
    private static void DrawEnvelope(DrawingContext context, Rect plot, double[] samples, double sampleDuration,
        double duration, double visibleSeconds, double offset, double maximum, string color, bool dashedLower)
    {
        if (visibleSeconds <= 0 || samples.Length == 0) return;
        using var clip = context.PushClip(plot);
        var upper = new Pen(Brush.Parse(color), 1.5);
        var lower = new Pen(Brush.Parse(color), 1, dashedLower ? new DashStyle([4, 2], 0) : null);
        var visibleCount = Math.Min(samples.Length, (int)Math.Ceiling(visibleSeconds / sampleDuration * samples.Length));
        Point? previousTop = null, previousBottom = null;
        for (var i = 0; i < visibleCount; i++)
        {
            var x = plot.Left + (offset + sampleDuration * i / samples.Length) / duration * plot.Width;
            var magnitude = samples[i] / maximum * plot.Height / 2;
            var top = new Point(x, plot.Center.Y - magnitude);
            var bottom = new Point(x, plot.Center.Y + magnitude);
            if (previousTop.HasValue) context.DrawLine(upper, previousTop.Value, top);
            if (previousBottom.HasValue) context.DrawLine(lower, previousBottom.Value, bottom);
            previousTop = top; previousBottom = bottom;
        }
    }
    private static void Text(DrawingContext context, string value, double x, double y, string color, double size) =>
        context.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), size, Brush.Parse(color)), new(x, y));
}
