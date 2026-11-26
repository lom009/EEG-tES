using System;
using System.Linq;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

/// <summary>In-memory only: deliberately has no device/protocol dependencies.</summary>
public class ToleranceCurrentModel : IDisposable
{
    private readonly StimulationCurrentPolicy _policy;
    private readonly StimulusConfigurationSnapshot _source;
    private readonly int _minimumRaw,
        _maximumRaw;
    private readonly int _channelMinimumRaw;
    private int _currentRaw;
    private bool _disposed;

    public ToleranceCurrentModel(
        ToleranceTestRouteData route,
        StimulusCapabilityProfile capability,
        bool protocolStep = false
    )
    {
        var zeroStart = route.Configuration.ArrayMode != StimulusArrayMode.MultiTarget;
        _policy =
            protocolStep || zeroStart
                ? new StimulationCurrentPolicy(
                    new StimulationCurrentOptions
                    {
                        Minimum = capability.CurrentPolicy.MinimumRaw,
                        Maximum = capability.CurrentPolicy.MaximumRaw,
                        Step = 4,
                    }
                )
                : capability.CurrentPolicy;
        _source = route.Configuration;
        if (_source.Kind == StimulusKind.EnvelopeTAcs || _source.Targets.Count == 0)
            throw new ArgumentException("耐受度配置必须包含非包络刺激靶点。");
        var returnCount =
            _source.ArrayMode == StimulusArrayMode.DualChannel
                ? capability.DualChannelSelectableCount
                : capability.HdSelectableChannelCount;
        if (
            !Enum.IsDefined(_source.Kind)
            || !Enum.IsDefined(_source.ArrayMode)
            || _source.Targets.Select(t => t.TargetId).Distinct().Count() != _source.Targets.Count
            || route.Electrodes.Select(e => e.PhysicalChannelId).Distinct().Count()
                != route.Electrodes.Count
            || route
                .Electrodes.Select(e => e.SiteId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != route.Electrodes.Count
            || route.Electrodes.Any(e =>
                !_source.Targets.Any(t => t.TargetId == e.TargetId)
                || !capability.StimulusSiteIds.Contains(e.SiteId)
                || e.PhysicalChannelId < 1
                || e.PhysicalChannelId > capability.StimulationPhysicalChannelCount
            )
            || _source.Targets.Any(t =>
                route.Electrodes.Count(e =>
                    e.TargetId == t.TargetId && e.Role == StimulationChannelRole.FixedActive
                ) != 1
                || route.Electrodes.Count(e =>
                    e.TargetId == t.TargetId && e.Role == StimulationChannelRole.Selectable
                ) != returnCount
            )
        )
            throw new ArgumentException("刺激点位或物理通道分配无效。");
        // Dual-channel snapshots acquire their physical channels on the electrode page.
        _source = _source with
        {
            Targets = _source
                .Targets.Select(t =>
                    t with
                    {
                        FixedActivePhysicalChannelId = route
                            .Electrodes.Single(e =>
                                e.TargetId == t.TargetId
                                && e.Role == StimulationChannelRole.FixedActive
                            )
                            .PhysicalChannelId,
                        Channels = route
                            .Electrodes.Where(e => e.TargetId == t.TargetId)
                            .OrderBy(e => e.PhysicalChannelId)
                            .Select(e => new StimulationPhysicalChannelSnapshot(
                                e.PhysicalChannelId,
                                e.Role,
                                0
                            ))
                            .ToArray(),
                    }
                )
                .ToArray(),
        };
        if (
            _source.Targets.Any(t =>
                t.Channels.Count(c => c.Role == StimulationChannelRole.FixedActive) != 1
                || !t.Channels.Any(c => c.Role == StimulationChannelRole.Selectable)
            )
        )
            throw new ArgumentException("刺激通道分配不完整。");
        _channelMinimumRaw = zeroStart ? 0 : _policy.MinimumRaw;
        _minimumRaw =
            _source.Targets.Max(t =>
                t.Channels.Count(c => c.Role == StimulationChannelRole.Selectable)
            ) * _channelMinimumRaw;
        _maximumRaw = Math.Min(
            _policy.MaximumRaw,
            _policy.ToRaw(capability.MaximumTotalPeakCurrent)
                / _source.Targets.Count
                / _policy.StepRaw
                * _policy.StepRaw
        );
        if (_minimumRaw > _maximumRaw)
            throw new ArgumentException("当前靶点数量无法满足电流上下限。");
        Reset();
    }

    public double Current => _policy.ToMilliAmps(_currentRaw);
    public double Minimum => _policy.ToMilliAmps(_minimumRaw);
    public double Maximum => _policy.ToMilliAmps(_maximumRaw);
    public double Step => _policy.StepMilliAmps;
    public StimulusConfigurationSnapshot Configuration => CreateConfiguration(_currentRaw);
    public StimulusConfigurationSnapshot MaximumConfiguration => CreateConfiguration(_maximumRaw);

    private StimulusConfigurationSnapshot CreateConfiguration(int currentRaw) =>
        _source with
        {
            Targets = _source
                .Targets.Select(t =>
                {
                    var selectable = t
                        .Channels.Where(c => c.Role == StimulationChannelRole.Selectable)
                        .OrderBy(c => c.PhysicalChannelId)
                        .ToArray();
                    var steps =
                        (currentRaw - selectable.Length * _channelMinimumRaw) / _policy.StepRaw;
                    return t with
                    {
                        PeakCurrent = _policy.ToMilliAmps(currentRaw),
                        Channels = t
                            .Channels.Select(c =>
                                c with
                                {
                                    Current =
                                        c.Role == StimulationChannelRole.FixedActive
                                            ? _policy.ToMilliAmps(currentRaw)
                                            : _policy.ToMilliAmps(
                                                _channelMinimumRaw
                                                    + (
                                                        steps / selectable.Length
                                                        + (
                                                            Array.IndexOf(selectable, c)
                                                            < steps % selectable.Length
                                                                ? 1
                                                                : 0
                                                        )
                                                    ) * _policy.StepRaw
                                            ),
                                }
                            )
                            .ToArray(),
                    };
                })
                .ToArray(),
        };

    public void Adjust(int steps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _currentRaw = (int)
            Math.Clamp((long)_currentRaw + (long)steps * _policy.StepRaw, _minimumRaw, _maximumRaw);
    }

    public void SetCurrent(double current)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (
            !double.IsFinite(current)
            || current < Minimum
            || current > Maximum
            || Math.Abs(current * 100 - Math.Round(current * 100)) > 0.000001
            || (_policy.ToRaw(current) - _minimumRaw) % _policy.StepRaw != 0
        )
            throw new ArgumentException("设备电流不满足当前测试范围或步长。");
        _currentRaw = _policy.ToRaw(current);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _currentRaw = _minimumRaw;
    }

    public void Dispose() => _disposed = true;
}
