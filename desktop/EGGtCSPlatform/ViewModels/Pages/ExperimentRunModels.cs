using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EGGtCSPlatform.Services;

namespace EGGtCSPlatform.ViewModels.Pages;

public enum ExperimentRunMode
{
    Manual,
    Automatic,
}

public enum ExperimentRunPageState
{
    TimingSetup,
    Running,
    EmergencyStopped,
    Completed,
    Results,
}

public enum ExperimentStageStatus
{
    Pending,
    Running,
    Completed,
    Stopped,
}

public enum ExperimentDurationUnit
{
    Milliseconds,
    Seconds,
    Minutes,
}

public enum WaveformLeadLayout
{
    SingleColumn,
    DoubleColumn,
    Comb32,
}

public sealed class WaveformLayoutOption(WaveformLeadLayout value, string label)
{
    public WaveformLeadLayout Value { get; } = value;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

public sealed class DurationUnitOption(ExperimentDurationUnit value, string label)
{
    public ExperimentDurationUnit Value { get; } = value;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

public sealed record ExperimentTimelineSegment(
    double StartSeconds,
    double EndSeconds,
    ExperimentRunStage Stage,
    string Label,
    string Fill,
    string Foreground,
    int Cycle
);

public readonly record struct TimelineViewportSpan(double StartFraction, double EndFraction);

public static class TimelineViewportMath
{
    public static bool TryGetVisibleSpan(
        double intervalStart,
        double intervalEnd,
        double viewStart,
        double viewEnd,
        out TimelineViewportSpan visibleSpan,
        double? revealThrough = null
    )
    {
        visibleSpan = default;
        if (
            !double.IsFinite(intervalStart)
            || !double.IsFinite(intervalEnd)
            || !double.IsFinite(viewStart)
            || !double.IsFinite(viewEnd)
            || intervalEnd <= intervalStart
            || viewEnd <= viewStart
        )
            return false;

        var revealedEnd =
            revealThrough is { } liveValue && double.IsFinite(liveValue)
                ? Math.Min(intervalEnd, liveValue)
                : intervalEnd;
        var visibleStart = Math.Max(intervalStart, viewStart);
        var visibleEnd = Math.Min(revealedEnd, viewEnd);
        if (visibleEnd <= visibleStart)
            return false;

        var viewSpan = viewEnd - viewStart;
        visibleSpan = new TimelineViewportSpan(
            Math.Clamp((visibleStart - viewStart) / viewSpan, 0d, 1d),
            Math.Clamp((visibleEnd - viewStart) / viewSpan, 0d, 1d)
        );
        return visibleSpan.EndFraction > visibleSpan.StartFraction;
    }
}

public static class ExperimentTimelineFormatter
{
    public static string Format(double seconds)
    {
        var value = Math.Max(0d, seconds);
        if (value < 60d)
            return $"{value:0.###}s";

        var hours = (int)Math.Floor(value / 3600d);
        var remainingAfterHours = value - hours * 3600d;
        var minutes = (int)Math.Floor(remainingAfterHours / 60d);
        var remainingSeconds = remainingAfterHours - minutes * 60d;
        var parts = new List<string>(3);
        if (hours > 0)
            parts.Add($"{hours}h");
        if (minutes > 0)
            parts.Add($"{minutes}mins");
        if (remainingSeconds > 0.0005d)
            parts.Add($"{remainingSeconds:0.###}s");
        return parts.Count == 0 ? "0s" : string.Join(" ", parts);
    }
}

public readonly record struct TimedWaveformPoint(
    double TimeSeconds,
    double Value,
    bool StartsNewSegment
);

public readonly record struct WaveformCoordinatePoint(double TimeSeconds, double Value);

public readonly record struct WaveformHoverSampleResult(
    double RequestedTimeSeconds,
    double SampleTimeSeconds,
    double Value,
    bool UsesNearestPoint
);

public interface IWaveformSampleLookup
{
    bool TryResolveHoverSample(
        double requestedTimeSeconds,
        double segmentStartSeconds,
        double segmentEndSeconds,
        out WaveformHoverSampleResult result
    );
}

public static class WaveformSampleInterpolator
{
    private const double TimeTolerance = 0.0000001d;

    public static bool TryInterpolate(
        IReadOnlyList<TimedWaveformPoint>? samples,
        double timeSeconds,
        out WaveformCoordinatePoint coordinate
    )
    {
        coordinate = default;
        if (samples is not { Count: > 0 })
            return false;

        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (samples[middle].TimeSeconds < timeSeconds)
                low = middle + 1;
            else
                high = middle;
        }

        if (
            low < samples.Count
            && Math.Abs(samples[low].TimeSeconds - timeSeconds) <= TimeTolerance
        )
        {
            coordinate = new WaveformCoordinatePoint(timeSeconds, samples[low].Value);
            return true;
        }
        if (low == 0 || low >= samples.Count)
            return false;

        var left = samples[low - 1];
        var right = samples[low];
        if (right.StartsNewSegment || right.TimeSeconds <= left.TimeSeconds)
            return false;

        var fraction = (timeSeconds - left.TimeSeconds) / (right.TimeSeconds - left.TimeSeconds);
        if (fraction is < 0d or > 1d)
            return false;
        coordinate = new WaveformCoordinatePoint(
            timeSeconds,
            left.Value + (right.Value - left.Value) * fraction
        );
        return true;
    }
}

public partial class WaveformHoverState : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private double _timeSeconds;

    [ObservableProperty]
    private string? _activeChannelId;

    public void Update(string channelId, double timeSeconds)
    {
        ActiveChannelId = channelId;
        TimeSeconds = timeSeconds;
        IsActive = true;
    }

    public void Clear(string? channelId = null)
    {
        if (
            channelId is not null
            && !string.Equals(ActiveChannelId, channelId, StringComparison.Ordinal)
        )
            return;
        IsActive = false;
        ActiveChannelId = null;
    }
}

public sealed record ExperimentResultDetailItem(string Label, string Value);

internal static class ExperimentDurationMath
{
    public const int MaximumDisplayDecimalPlaces = 6;
    private const decimal MaximumTimeSpanMilliseconds = 922337203685477.5807m;

    public static decimal UnitMilliseconds(ExperimentDurationUnit unit) =>
        unit switch
        {
            ExperimentDurationUnit.Milliseconds => 1m,
            ExperimentDurationUnit.Seconds => 1000m,
            ExperimentDurationUnit.Minutes => 60000m,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };

    public static int GetDisplayDecimalPlaces(int stepMilliseconds, ExperimentDurationUnit unit)
    {
        if (stepMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepMilliseconds));

        var increment = stepMilliseconds / UnitMilliseconds(unit);
        for (var places = 0; places <= MaximumDisplayDecimalPlaces; places++)
        {
            if (decimal.Round(increment, places, MidpointRounding.AwayFromZero) == increment)
                return places;
        }

        return MaximumDisplayDecimalPlaces;
    }

    public static decimal ToDisplayValue(decimal milliseconds, ExperimentDurationUnit unit) =>
        decimal.Round(
            milliseconds / UnitMilliseconds(unit),
            MaximumDisplayDecimalPlaces,
            MidpointRounding.AwayFromZero
        );

    public static bool IsAboveMaximum(
        decimal displayValue,
        ExperimentDurationUnit unit,
        int maximumMilliseconds
    )
    {
        try
        {
            return displayValue * UnitMilliseconds(unit) > maximumMilliseconds;
        }
        catch (OverflowException)
        {
            return true;
        }
    }

    public static bool IsBelowMinimum(
        decimal displayValue,
        ExperimentDurationUnit unit,
        int minimumMilliseconds
    )
    {
        try
        {
            return displayValue * UnitMilliseconds(unit) < minimumMilliseconds;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TryToStepMilliseconds(
        decimal displayValue,
        ExperimentDurationUnit unit,
        int stepMilliseconds,
        int minimumMilliseconds,
        int maximumMilliseconds,
        out decimal milliseconds
    )
    {
        milliseconds = 0;
        if (
            displayValue <= 0
            || stepMilliseconds <= 0
            || minimumMilliseconds <= 0
            || minimumMilliseconds > maximumMilliseconds
            || maximumMilliseconds < stepMilliseconds
        )
            return false;

        decimal rawMilliseconds;
        try
        {
            rawMilliseconds = displayValue * UnitMilliseconds(unit);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (rawMilliseconds > MaximumTimeSpanMilliseconds)
            return false;

        var stepCount = decimal.Round(
            rawMilliseconds / stepMilliseconds,
            0,
            MidpointRounding.AwayFromZero
        );
        if (stepCount <= 0)
            return false;

        var candidate = stepCount * stepMilliseconds;
        if (candidate < minimumMilliseconds || candidate > maximumMilliseconds)
            return false;
        var decimalPlaces = GetDisplayDecimalPlaces(stepMilliseconds, unit);
        var increment = stepMilliseconds / UnitMilliseconds(unit);
        var hasExactDisplayIncrement =
            decimal.Round(increment, decimalPlaces, MidpointRounding.AwayFromZero) == increment;
        var tolerance = hasExactDisplayIncrement
            ? 0m
            : UnitMilliseconds(unit) * 0.5m / Pow10(decimalPlaces);
        if (decimal.Abs(rawMilliseconds - candidate) > tolerance)
            return false;

        milliseconds = candidate;
        return true;
    }

    private static decimal Pow10(int exponent)
    {
        var result = 1m;
        for (var index = 0; index < exponent; index++)
            result *= 10m;
        return result;
    }
}

public partial class ExperimentStageViewModel : ObservableObject
{
    private decimal? _durationValue;

    private DurationUnitOption? _selectedUnit;

    private decimal? _durationMilliseconds;

    private bool _isSynchronizingDuration;

    [ObservableProperty]
    private ExperimentStageStatus _status;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isEditable;

    [ObservableProperty]
    private string _validationText = string.Empty;

    public ExperimentStageViewModel(
        int index,
        ExperimentRunStage stage,
        string title,
        DurationUnitOption selectedUnit,
        bool isEditable,
        int durationStepMilliseconds = 1,
        int durationMaximumMilliseconds = 65535000,
        int durationMinimumMilliseconds = 1
    )
    {
        if (durationStepMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationStepMilliseconds));
        if (durationMaximumMilliseconds < durationStepMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(durationMaximumMilliseconds));
        if (
            durationMinimumMilliseconds <= 0
            || durationMinimumMilliseconds > durationMaximumMilliseconds
        )
            throw new ArgumentOutOfRangeException(nameof(durationMinimumMilliseconds));

        Index = index;
        Stage = stage;
        Title = title;
        _selectedUnit = selectedUnit;
        _isEditable = isEditable;
        DurationStepMilliseconds = durationStepMilliseconds;
        DurationMaximumMilliseconds = durationMaximumMilliseconds;
        DurationMinimumMilliseconds = durationMinimumMilliseconds;
    }

    public int Index { get; }

    public ExperimentRunStage Stage { get; }

    public string Title { get; }

    public int DurationStepMilliseconds { get; }

    public int DurationMaximumMilliseconds { get; }

    public int DurationMinimumMilliseconds { get; }

    public decimal? DurationMilliseconds => _durationMilliseconds;

    public decimal? DurationValue
    {
        get => _durationValue;
        set
        {
            if (!SetProperty(ref _durationValue, value) || _isSynchronizingDuration)
                return;

            UpdateMillisecondsFromDisplay(value);
        }
    }

    public DurationUnitOption? SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (!SetProperty(ref _selectedUnit, value) || _isSynchronizingDuration)
                return;

            SyncDisplayFromMilliseconds();
        }
    }

    public IReadOnlyList<DurationUnitOption> Units { get; init; } = [];

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationText);

    public string StatusText =>
        Status switch
        {
            ExperimentStageStatus.Running => "● 进行中",
            ExperimentStageStatus.Completed => "✓ 已完成",
            ExperimentStageStatus.Stopped => "■ 已停止",
            _ => "待执行",
        };

    public TimeSpan? GetDuration()
    {
        if (_durationMilliseconds is null or <= 0 || SelectedUnit is null)
            return null;
        return TimeSpan.FromMilliseconds((double)_durationMilliseconds.Value);
    }

    public void SetDurationMilliseconds(decimal? milliseconds)
    {
        // Persisted runs created before configurable input steps may contain
        // otherwise valid durations that are not aligned to the current UI step.
        _durationMilliseconds = milliseconds is > 0
            ? decimal.Clamp(
                milliseconds.Value,
                DurationMinimumMilliseconds,
                DurationMaximumMilliseconds
            )
            : null;
        OnPropertyChanged(nameof(DurationMilliseconds));
        SyncDisplayFromMilliseconds();
    }

    private void UpdateMillisecondsFromDisplay(decimal? value)
    {
        var milliseconds =
            value is { } displayValue
            && SelectedUnit is { } selectedUnit
            && ExperimentDurationMath.TryToStepMilliseconds(
                displayValue,
                selectedUnit.Value,
                DurationStepMilliseconds,
                DurationMinimumMilliseconds,
                DurationMaximumMilliseconds,
                out var converted
            )
                ? converted
                : (decimal?)null;
        if (_durationMilliseconds == milliseconds)
            return;
        _durationMilliseconds = milliseconds;
        OnPropertyChanged(nameof(DurationMilliseconds));
    }

    private void SyncDisplayFromMilliseconds()
    {
        var value =
            _durationMilliseconds is { } milliseconds && SelectedUnit is { } selectedUnit
                ? ExperimentDurationMath.ToDisplayValue(milliseconds, selectedUnit.Value)
                : (decimal?)null;
        _isSynchronizingDuration = true;
        try
        {
            DurationValue = value;
        }
        finally
        {
            _isSynchronizingDuration = false;
        }
    }

    partial void OnValidationTextChanged(string value) =>
        OnPropertyChanged(nameof(HasValidationError));

    partial void OnStatusChanged(ExperimentStageStatus value) =>
        OnPropertyChanged(nameof(StatusText));
}

public sealed class WaveformChannelViewModel : ObservableObject, IWaveformSampleLookup
{
    private const int RetainedSeconds = 600;
    private const int OverviewPointsPerSecond = 20;
    private readonly TimedWaveformPoint[] _recentSamples;
    private readonly List<TimedWaveformPoint> _overview = [];
    private readonly int _overviewStride;
    private TimedWaveformPoint[] _samples = [];
    private long _recentWriteCount;
    private long _overviewCounter;
    private int _recentCount;
    private int _activeAcquisitionSegmentId = int.MinValue;
    private double _historyDurationSeconds;
    private bool _hasVisibleWindowOverride;

    public WaveformChannelViewModel(
        string channelId,
        string stroke,
        string background,
        int sampleRateHz,
        bool isEven
    )
    {
        ChannelId = channelId;
        Stroke = stroke;
        Background = background;
        SampleRateHz = sampleRateHz;
        IsEven = isEven;
        _recentSamples = new TimedWaveformPoint[Math.Max(1, sampleRateHz * RetainedSeconds)];
        _overviewStride = Math.Max(1, sampleRateHz / OverviewPointsPerSecond);
    }

    public string ChannelId { get; }

    public string Stroke { get; }

    public string Background { get; }

    public bool IsEven { get; }

    public bool ShowAmplitudeUnit { get; init; }

    public int SampleRateHz { get; }

    public string SignalQualityText { get; set; } = "良好";

    public TimedWaveformPoint[] Samples
    {
        get => _samples;
        private set => SetProperty(ref _samples, value);
    }

    public IReadOnlyList<TimedWaveformPoint> OverviewSamples => _overview;

    public double HistoryDurationSeconds => _historyDurationSeconds;

    public void Append(WaveformChannelBatch batch, int acquisitionSegmentId = 1)
    {
        if (batch.Samples.Count == 0)
            return;

        var interval =
            batch.SampleIntervalSeconds > 0d
                ? batch.SampleIntervalSeconds
                : 1d / Math.Max(1, SampleRateHz);
        var batchStartsNewSegment =
            _recentCount == 0 || acquisitionSegmentId != _activeAcquisitionSegmentId;

        for (var index = 0; index < batch.Samples.Count; index++)
        {
            var point = new TimedWaveformPoint(
                batch.StartTimeSeconds + index * interval,
                batch.Samples[index],
                index == 0 && batchStartsNewSegment
            );
            _recentSamples[_recentWriteCount % _recentSamples.Length] = point;
            _recentWriteCount++;
            _recentCount = Math.Min(_recentSamples.Length, _recentCount + 1);
            if (point.StartsNewSegment || _overviewCounter++ % _overviewStride == 0)
                _overview.Add(point);
            _historyDurationSeconds = Math.Max(
                _historyDurationSeconds,
                point.TimeSeconds + interval
            );
        }
        _activeAcquisitionSegmentId = acquisitionSegmentId;
    }

    public bool TryResolveHoverSample(
        double requestedTimeSeconds,
        double segmentStartSeconds,
        double segmentEndSeconds,
        out WaveformHoverSampleResult result
    )
    {
        result = default;
        if (_hasVisibleWindowOverride)
        {
            if (
                requestedTimeSeconds < segmentStartSeconds
                || requestedTimeSeconds > segmentEndSeconds
                || !WaveformSampleInterpolator.TryInterpolate(
                    Samples,
                    requestedTimeSeconds,
                    out var coordinate
                )
            )
                return false;
            result = new WaveformHoverSampleResult(
                requestedTimeSeconds,
                coordinate.TimeSeconds,
                coordinate.Value,
                false
            );
            return true;
        }
        if (
            _recentCount == 0
            || !double.IsFinite(requestedTimeSeconds)
            || !double.IsFinite(segmentStartSeconds)
            || !double.IsFinite(segmentEndSeconds)
            || segmentEndSeconds < segmentStartSeconds
            || requestedTimeSeconds < segmentStartSeconds
            || requestedTimeSeconds > segmentEndSeconds
        )
            return false;

        (TimedWaveformPoint Point, bool IsRecent)? left = null;
        (TimedWaveformPoint Point, bool IsRecent)? right = null;

        void Consider(TimedWaveformPoint point, bool isRecent)
        {
            const double tolerance = 0.0000001d;
            if (
                point.TimeSeconds < segmentStartSeconds - tolerance
                || point.TimeSeconds > segmentEndSeconds + tolerance
            )
                return;

            if (
                point.TimeSeconds <= requestedTimeSeconds + tolerance
                && (
                    left is null
                    || point.TimeSeconds > left.Value.Point.TimeSeconds + tolerance
                    || (
                        Math.Abs(point.TimeSeconds - left.Value.Point.TimeSeconds) <= tolerance
                        && isRecent
                    )
                )
            )
                left = (point, isRecent);
            if (
                point.TimeSeconds >= requestedTimeSeconds - tolerance
                && (
                    right is null
                    || point.TimeSeconds < right.Value.Point.TimeSeconds - tolerance
                    || (
                        Math.Abs(point.TimeSeconds - right.Value.Point.TimeSeconds) <= tolerance
                        && isRecent
                    )
                )
            )
                right = (point, isRecent);
        }

        var overviewIndex = FindFirstOverviewIndex(requestedTimeSeconds);
        if (overviewIndex > 0)
            Consider(_overview[overviewIndex - 1], false);
        if (overviewIndex < _overview.Count)
            Consider(_overview[overviewIndex], false);

        var recentStartIndex = Math.Max(0L, _recentWriteCount - _recentCount);
        var recentIndex = FindFirstRecentIndex(requestedTimeSeconds, recentStartIndex);
        if (recentIndex > recentStartIndex)
            Consider(_recentSamples[(recentIndex - 1) % _recentSamples.Length], true);
        if (recentIndex < _recentWriteCount)
            Consider(_recentSamples[recentIndex % _recentSamples.Length], true);

        const double exactTolerance = 0.0000001d;
        if (
            left is { } exactLeft
            && Math.Abs(exactLeft.Point.TimeSeconds - requestedTimeSeconds) <= exactTolerance
        )
        {
            result = new WaveformHoverSampleResult(
                requestedTimeSeconds,
                exactLeft.Point.TimeSeconds,
                exactLeft.Point.Value,
                false
            );
            return true;
        }
        if (
            right is { } exactRight
            && Math.Abs(exactRight.Point.TimeSeconds - requestedTimeSeconds) <= exactTolerance
        )
        {
            result = new WaveformHoverSampleResult(
                requestedTimeSeconds,
                exactRight.Point.TimeSeconds,
                exactRight.Point.Value,
                false
            );
            return true;
        }

        if (left is { } leftValue && right is { } rightValue)
        {
            var expectedInterval =
                leftValue.IsRecent || rightValue.IsRecent
                    ? 1d / Math.Max(1, SampleRateHz)
                    : _overviewStride / (double)Math.Max(1, SampleRateHz);
            var gap = rightValue.Point.TimeSeconds - leftValue.Point.TimeSeconds;
            if (gap > 0d && gap <= expectedInterval * 1.5d + exactTolerance)
            {
                var fraction = (requestedTimeSeconds - leftValue.Point.TimeSeconds) / gap;
                result = new WaveformHoverSampleResult(
                    requestedTimeSeconds,
                    requestedTimeSeconds,
                    leftValue.Point.Value
                        + (rightValue.Point.Value - leftValue.Point.Value) * fraction,
                    false
                );
                return true;
            }

            var nearest =
                requestedTimeSeconds - leftValue.Point.TimeSeconds
                <= rightValue.Point.TimeSeconds - requestedTimeSeconds + exactTolerance
                    ? leftValue.Point
                    : rightValue.Point;
            result = new WaveformHoverSampleResult(
                requestedTimeSeconds,
                nearest.TimeSeconds,
                nearest.Value,
                true
            );
            return true;
        }

        var single = left?.Point ?? right?.Point;
        if (single is not { } nearestPoint)
            return false;
        result = new WaveformHoverSampleResult(
            requestedTimeSeconds,
            nearestPoint.TimeSeconds,
            nearestPoint.Value,
            true
        );
        return true;
    }

    public void RefreshVisible(double startSeconds, double endSeconds, int maximumPoints = 1200)
    {
        if (_hasVisibleWindowOverride)
            return;
        if (_recentCount == 0 || endSeconds <= startSeconds)
        {
            Samples = [];
            return;
        }

        var recentStartIndex = Math.Max(0L, _recentWriteCount - _recentCount);
        var earliestRecentTime = _recentSamples[
            recentStartIndex % _recentSamples.Length
        ].TimeSeconds;
        var targetPointCount = Math.Max(2, maximumPoints);
        var sourcePointsPerSecond =
            endSeconds <= earliestRecentTime ? OverviewPointsPerSecond : SampleRateHz;
        var estimatedFullWindowPoints = Math.Max(
            1d,
            (endSeconds - startSeconds) * sourcePointsPerSecond
        );
        var candidates = EnumerateVisibleCandidates(
            startSeconds,
            endSeconds,
            earliestRecentTime,
            recentStartIndex
        );
        if (estimatedFullWindowPoints <= targetPointCount)
        {
            var visible = candidates.ToArray();
            Samples = visible.Length < 2 ? [] : visible;
            return;
        }

        Samples = DownsampleStableEnvelope(candidates, endSeconds - startSeconds, targetPointCount);
    }

    private IEnumerable<TimedWaveformPoint> EnumerateVisibleCandidates(
        double startSeconds,
        double endSeconds,
        double earliestRecentTime,
        long recentStartIndex
    )
    {
        TimedWaveformPoint? last = null;
        if (
            TryFindStoredBoundaryPoint(startSeconds, findPrevious: true, out var previous)
            && previous.TimeSeconds < startSeconds
        )
        {
            last = previous;
            yield return previous;
        }

        var overviewIndex = FindFirstOverviewIndex(startSeconds);
        for (; overviewIndex < _overview.Count; overviewIndex++)
        {
            var point = _overview[overviewIndex];
            if (point.TimeSeconds > endSeconds)
                break;
            if (
                point.TimeSeconds < earliestRecentTime
                && (last is null || point.TimeSeconds > last.Value.TimeSeconds)
            )
            {
                last = point;
                yield return point;
            }
        }

        var firstRecentIndex = FindFirstRecentIndex(startSeconds, recentStartIndex);
        for (
            var absoluteIndex = firstRecentIndex;
            absoluteIndex < _recentWriteCount;
            absoluteIndex++
        )
        {
            var point = _recentSamples[absoluteIndex % _recentSamples.Length];
            if (point.TimeSeconds > endSeconds)
                break;
            if (last is null || point.TimeSeconds > last.Value.TimeSeconds)
            {
                last = point;
                yield return point;
            }
        }

        if (
            TryFindStoredBoundaryPoint(endSeconds, findPrevious: false, out var next)
            && next.TimeSeconds > endSeconds
            && (last is null || next.TimeSeconds > last.Value.TimeSeconds)
        )
            yield return next;
    }

    private static TimedWaveformPoint[] DownsampleStableEnvelope(
        IEnumerable<TimedWaveformPoint> candidates,
        double viewSpanSeconds,
        int targetPointCount
    )
    {
        var bucketCount = Math.Max(1, targetPointCount / 2);
        var normalizedViewSpan = Math.Round(viewSpanSeconds, 9, MidpointRounding.AwayFromZero);
        var bucketWidth = Math.Max(0.0000001d, normalizedViewSpan / bucketCount);
        var output = new List<TimedWaveformPoint>(targetPointCount + 32);
        TimedWaveformPoint? minimum = null;
        TimedWaveformPoint? maximum = null;
        long bucketIndex = long.MinValue;

        void AppendDistinct(TimedWaveformPoint point)
        {
            if (
                output.Count == 0
                || Math.Abs(output[^1].TimeSeconds - point.TimeSeconds) > 0.0000001d
            )
                output.Add(point);
        }

        void FlushBucket()
        {
            if (minimum is not { } min || maximum is not { } max)
                return;
            if (min.TimeSeconds <= max.TimeSeconds)
            {
                AppendDistinct(min);
                AppendDistinct(max);
            }
            else
            {
                AppendDistinct(max);
                AppendDistinct(min);
            }
            minimum = null;
            maximum = null;
        }

        var hasFirst = false;
        var last = default(TimedWaveformPoint);
        foreach (var point in candidates)
        {
            if (!hasFirst)
            {
                AppendDistinct(point);
                last = point;
                hasFirst = true;
                continue;
            }
            last = point;
            var pointBucket = (long)Math.Floor(point.TimeSeconds / bucketWidth);
            if (point.StartsNewSegment)
            {
                FlushBucket();
                AppendDistinct(point);
                bucketIndex = pointBucket;
                continue;
            }
            if (bucketIndex != pointBucket)
            {
                FlushBucket();
                bucketIndex = pointBucket;
            }
            if (minimum is null || point.Value < minimum.Value.Value)
                minimum = point;
            if (maximum is null || point.Value > maximum.Value.Value)
                maximum = point;
        }
        if (!hasFirst)
            return [];
        FlushBucket();
        AppendDistinct(last);
        return output.Count < 2 ? [] : [.. output];
    }

    private bool TryFindStoredBoundaryPoint(
        double timeSeconds,
        bool findPrevious,
        out TimedWaveformPoint result
    )
    {
        var candidate = default(TimedWaveformPoint);
        var found = false;

        void Consider(TimedWaveformPoint point)
        {
            if (findPrevious)
            {
                if (
                    point.TimeSeconds > timeSeconds
                    || (found && point.TimeSeconds <= candidate.TimeSeconds)
                )
                    return;
            }
            else if (
                point.TimeSeconds < timeSeconds
                || (found && point.TimeSeconds >= candidate.TimeSeconds)
            )
                return;
            candidate = point;
            found = true;
        }

        var overviewIndex = FindFirstOverviewIndex(timeSeconds);
        if (overviewIndex > 0)
            Consider(_overview[overviewIndex - 1]);
        if (overviewIndex < _overview.Count)
            Consider(_overview[overviewIndex]);

        var recentStartIndex = Math.Max(0L, _recentWriteCount - _recentCount);
        var recentIndex = FindFirstRecentIndex(timeSeconds, recentStartIndex);
        if (recentIndex > recentStartIndex)
            Consider(_recentSamples[(recentIndex - 1) % _recentSamples.Length]);
        if (recentIndex < _recentWriteCount)
            Consider(_recentSamples[recentIndex % _recentSamples.Length]);
        result = candidate;
        return found;
    }

    private int FindFirstOverviewIndex(double timeSeconds)
    {
        var low = 0;
        var high = _overview.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_overview[middle].TimeSeconds < timeSeconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private long FindFirstRecentIndex(double timeSeconds, long earliestIndex)
    {
        var low = earliestIndex;
        var high = _recentWriteCount;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_recentSamples[middle % _recentSamples.Length].TimeSeconds < timeSeconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    public void Clear()
    {
        _hasVisibleWindowOverride = false;
        ClearLiveHistory();
        Samples = [];
    }

    public void ClearLiveHistory()
    {
        Array.Clear(_recentSamples);
        _overview.Clear();
        _recentWriteCount = 0;
        _overviewCounter = 0;
        _recentCount = 0;
        _activeAcquisitionSegmentId = int.MinValue;
        _historyDurationSeconds = 0d;
        if (!_hasVisibleWindowOverride)
            Samples = [];
    }

    public void ReplaceVisibleWindow(IReadOnlyList<TimedWaveformPoint> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _hasVisibleWindowOverride = true;
        Samples = samples as TimedWaveformPoint[] ?? samples.ToArray();
    }

    public void ClearVisibleWindowOverride()
    {
        if (!_hasVisibleWindowOverride)
            return;
        _hasVisibleWindowOverride = false;
        Samples = [];
    }
}

public sealed class ExperimentExceptionViewModel(DateTime occurredAt, string message)
{
    public DateTime OccurredAt { get; } = occurredAt;

    public string Message { get; } = message;

    public string DisplayText => $"{OccurredAt:HH:mm:ss}  {Message}";
}
