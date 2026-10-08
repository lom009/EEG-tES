using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;
using EGGtCSPlatform.ViewModels.Pages;
using SdkDirection = EGGtCSPlatform.DeviceSdk.StimulationDirection;
using UiDirection = EGGtCSPlatform.ViewModels.Pages.StimulusDirection;

namespace EGGtCSPlatform.Services;

public static class DeviceStimulationConfigurationMapper
{
    /// <summary>Legacy impedance probe only. Never use this mapping to start envelope stimulation.</summary>
    public static StimulationConfiguration CreateForImpedance(
        IEggtCsDevice device,
        ExperimentStimulusConfigurationSnapshot source,
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    )
    {
        if (source.Kind == StimulusKind.EnvelopeTAcs)
            source = source with
            {
                Kind = StimulusKind.TAcs,
                Envelope = null,
                Frequency = 40,
                RampSeconds = 7,
                DutyPercent = 79,
                Direction = StimulusDirection.Bidirectional,
            };
        return Create(device, source, assignments);
    }

    public static StimulationConfiguration Create(
        IEggtCsDevice device,
        ExperimentStimulusConfigurationSnapshot source,
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == StimulusKind.EnvelopeTAcs || source.Envelope is not null)
            throw new InvalidOperationException(
                "包络-tACS 仅支持数据生成与历史回放，不能下发设备。"
            );
        ArgumentNullException.ThrowIfNull(assignments);
        if (source.Targets.Count == 0)
            throw new InvalidOperationException(
                "Stimulation configuration requires at least one target group."
            );
        if (!Enum.IsDefined(source.ArrayMode))
            throw new InvalidOperationException("Stimulation array mode is invalid.");
        if (
            source.Targets.Select(target => target.TargetId).Distinct().Count()
                != source.Targets.Count
            || source.Targets.Select(target => target.DisplayOrder).Distinct().Count()
                != source.Targets.Count
        )
            throw new InvalidOperationException(
                "Stimulation target identifiers and display orders must be unique."
            );
        if (
            assignments.Count != source.Targets.Sum(target => target.Channels.Count)
            || assignments.Any(assignment =>
                !source.Targets.Any(target => target.TargetId == assignment.TargetId)
            )
        )
            throw new InvalidOperationException(
                "Stimulation electrode assignments do not match the configured target groups."
            );
        if (
            assignments.Any(assignment => string.IsNullOrWhiteSpace(assignment.SiteId))
            || assignments
                .Select(assignment => assignment.SiteId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != assignments.Count
        )
            throw new InvalidOperationException(
                "Stimulation electrode site assignments must be non-empty and unique."
            );

        var groups = source
            .Targets.OrderBy(target => target.DisplayOrder)
            .Select(target => CreateTargetGroup(target, assignments))
            .ToArray();
        ValidatePhysicalChannels(device, groups);

        var parameterError = StimulusParameterPolicy.Validate(
            source.Kind,
            source.ShamMode,
            source.Frequency,
            source.RampSeconds,
            source.DutyPercent,
            true
        );
        if (parameterError is not null)
            throw new InvalidOperationException(parameterError);
        var frequency = (decimal)source.Frequency;

        return new StimulationConfiguration(
            source.ArrayMode is StimulusArrayMode.Hd or StimulusArrayMode.MultiTarget,
            groups,
            MapWaveform(source.Kind, source.ShamMode),
            MapDirection(source.Direction),
            frequency,
            checked((int)source.DutyPercent),
            TimeSpan.FromSeconds(source.RampSeconds)
        );
    }

    public static StimulationConfiguration CreateFallback(
        IEggtCsDevice device,
        double currentMilliAmps
    )
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!double.IsFinite(currentMilliAmps) || currentMilliAmps <= 0d)
            throw new InvalidOperationException("Fallback stimulation current must be positive.");
        var groups = new[]
        {
            new StimulationTargetGroup(
                1,
                [
                    new StimulationChannel(
                        1,
                        (decimal)currentMilliAmps,
                        DeviceStimulationChannelRole.FixedActive
                    ),
                ]
            ),
        };
        ValidatePhysicalChannels(device, groups);
        return new StimulationConfiguration(
            false,
            groups,
            StimulationWaveform.TDcs,
            SdkDirection.Positive,
            0.1m,
            50,
            TimeSpan.Zero
        );
    }

    private static StimulationTargetGroup CreateTargetGroup(
        ExperimentStimulusTargetSnapshot target,
        IReadOnlyList<StimulusElectrodeAssignment> assignments
    )
    {
        if (target.Channels.Count == 0)
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} has no physical channels."
            );
        if (!double.IsFinite(target.PeakCurrent) || target.PeakCurrent <= 0d)
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} peak current must be positive."
            );
        var targetAssignments = assignments
            .Where(assignment => assignment.TargetId == target.TargetId)
            .ToArray();
        if (
            targetAssignments.Length != target.Channels.Count
            || target.Channels.Any(channel =>
                !targetAssignments.Any(assignment =>
                    assignment.PhysicalChannelId == channel.PhysicalChannelId
                    && assignment.Role == channel.Role
                )
            )
        )
        {
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} channel configuration does not match its electrode assignments."
            );
        }

        var channels = target
            .Channels.Select(channel =>
            {
                if (!double.IsFinite(channel.Current) || channel.Current <= 0d)
                    throw new InvalidOperationException(
                        $"Stimulation channel {channel.PhysicalChannelId} current must be positive."
                    );
                return new StimulationChannel(
                    channel.PhysicalChannelId,
                    (decimal)channel.Current,
                    channel.Role == StimulationChannelRole.FixedActive
                        ? DeviceStimulationChannelRole.FixedActive
                        : DeviceStimulationChannelRole.Selectable
                );
            })
            .ToArray();
        if (
            channels.Count(channel => channel.Role == DeviceStimulationChannelRole.FixedActive) != 1
        )
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} requires exactly one fixed active channel."
            );
        var fixedChannel = channels.Single(channel =>
            channel.Role == DeviceStimulationChannelRole.FixedActive
        );
        if (
            fixedChannel.PhysicalChannel != target.FixedActivePhysicalChannelId
            || !AreEqual((double)fixedChannel.CurrentMilliAmps, target.PeakCurrent)
        )
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} fixed channel does not match its peak-current configuration."
            );
        var selectableCurrent = channels
            .Where(channel => channel.Role == DeviceStimulationChannelRole.Selectable)
            .Sum(channel => (double)channel.CurrentMilliAmps);
        if (!AreEqual(selectableCurrent, target.PeakCurrent))
            throw new InvalidOperationException(
                $"Stimulation target {target.DisplayOrder} selectable-channel current must equal its peak current."
            );
        return new StimulationTargetGroup(target.DisplayOrder, channels);
    }

    private static bool AreEqual(double left, double right) => Math.Abs(left - right) <= 0.000001d;

    private static void ValidatePhysicalChannels(
        IEggtCsDevice device,
        IReadOnlyList<StimulationTargetGroup> groups
    )
    {
        var physicalChannels = groups
            .SelectMany(group => group.Channels)
            .Select(channel => channel.PhysicalChannel)
            .ToArray();
        if (
            physicalChannels.Distinct().Count() != physicalChannels.Length
            || physicalChannels.Any(channel =>
                channel < 1 || channel > device.Capabilities.StimulationPhysicalChannelCount
            )
        )
        {
            throw new InvalidOperationException(
                "Stimulation physical channels must be unique and within the device channel bank."
            );
        }
    }

    private static StimulationWaveform MapWaveform(StimulusKind kind, ShamWaveformMode shamMode) =>
        kind switch
        {
            StimulusKind.TDcs => StimulationWaveform.TDcs,
            StimulusKind.TAcs => StimulationWaveform.TAcs,
            StimulusKind.TRns => StimulationWaveform.TRns,
            StimulusKind.TPcs => StimulationWaveform.TPcs,
            StimulusKind.Sham when shamMode == ShamWaveformMode.Alternating =>
                StimulationWaveform.ShamAlternating,
            StimulusKind.Sham when shamMode == ShamWaveformMode.Direct =>
                StimulationWaveform.ShamDirect,
            StimulusKind.Sham => throw new InvalidOperationException(
                "Stimulation sham waveform mode is invalid."
            ),
            _ => throw new InvalidOperationException("Stimulation waveform is invalid."),
        };

    private static SdkDirection MapDirection(UiDirection direction) =>
        direction switch
        {
            UiDirection.Negative => SdkDirection.Negative,
            UiDirection.Bidirectional => SdkDirection.Bidirectional,
            UiDirection.Positive => SdkDirection.Positive,
            _ => throw new InvalidOperationException("Stimulation direction is invalid."),
        };
}
