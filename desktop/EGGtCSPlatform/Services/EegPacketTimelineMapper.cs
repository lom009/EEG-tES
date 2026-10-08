using System;
using System.Collections.Generic;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Services;

public static class EegPacketTimelineMapper
{
    public static IReadOnlyList<WaveformChannelBatch> CreateBatches(
        EegDataPacketReceivedEvent packet,
        IReadOnlyDictionary<int, string> channelNames,
        int sampleRateHz,
        TimeSpan timelineOffset,
        TimeSpan acquisitionDuration
    ) =>
        CreateBatches(
            packet,
            channelNames,
            sampleRateHz,
            timelineOffset,
            acquisitionDuration,
            timeline: null
        );

    public static IReadOnlyList<WaveformChannelBatch> CreateBatches(
        EegDataPacketReceivedEvent packet,
        IReadOnlyDictionary<int, string> channelNames,
        int sampleRateHz,
        TimeSpan timelineOffset,
        TimeSpan acquisitionDuration,
        EegSampleTimeline? timeline
    )
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(channelNames);
        if (sampleRateHz is not (250 or 500))
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (
            packet.SampleCount <= 0
            || packet.Channels.Any(channel => channel.SamplesMicrovolts.Count != packet.SampleCount)
        )
            throw new ArgumentException(
                "Every EEG channel must contain the declared sample count.",
                nameof(packet)
            );
        if (acquisitionDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(acquisitionDuration));

        var sampleIntervalTicks = TimeSpan.TicksPerSecond / sampleRateHz;
        var firstSampleTicks = timeline?.GetNextPacketFirstSampleTicks(packet.SampleCount) ?? 0L;
        var lastSampleTicks = firstSampleTicks + (packet.SampleCount - 1L) * sampleIntervalTicks;
        if (lastSampleTicks < 0 || firstSampleTicks > acquisitionDuration.Ticks)
            return [];

        var firstSampleIndex =
            firstSampleTicks >= 0
                ? 0
                : checked((int)DivideRoundUp(-firstSampleTicks, sampleIntervalTicks));
        var lastSampleIndex =
            lastSampleTicks <= acquisitionDuration.Ticks
                ? packet.SampleCount - 1
                : checked(
                    (int)((acquisitionDuration.Ticks - firstSampleTicks) / sampleIntervalTicks)
                );
        firstSampleIndex = Math.Clamp(firstSampleIndex, 0, packet.SampleCount);
        lastSampleIndex = Math.Clamp(lastSampleIndex, -1, packet.SampleCount - 1);
        if (firstSampleIndex > lastSampleIndex)
            return [];

        var keptSampleCount = lastSampleIndex - firstSampleIndex + 1;
        var keptFirstSampleTicks = firstSampleTicks + firstSampleIndex * sampleIntervalTicks;
        var startTime =
            timelineOffset.TotalSeconds + TimeSpan.FromTicks(keptFirstSampleTicks).TotalSeconds;
        var batches = new List<WaveformChannelBatch>(channelNames.Count);
        var keepsWholePacket = firstSampleIndex == 0 && keptSampleCount == packet.SampleCount;
        foreach (var channel in packet.Channels)
        {
            if (!channelNames.TryGetValue(channel.PhysicalChannel, out var channelName))
                continue;
            IReadOnlyList<double> samples = keepsWholePacket
                ? channel.SamplesMicrovolts
                : channel.SamplesMicrovolts.Skip(firstSampleIndex).Take(keptSampleCount).ToArray();
            batches.Add(
                new WaveformChannelBatch(channelName, startTime, 1d / sampleRateHz, samples)
            );
        }
        return batches;
    }

    private static long DivideRoundUp(long value, long divisor) =>
        checked((value + divisor - 1) / divisor);
}

/// <summary>
/// Produces a gap-free sampling clock from packet order and the configured sample rate.
/// The first packet starts at sample index zero. Packet arrival and processing times
/// never compress, stretch, or shift the waveform timeline.
/// </summary>
public sealed class EegSampleTimeline(int sampleRateHz)
{
    private readonly long _sampleIntervalTicks = sampleRateHz is 250 or 500
        ? TimeSpan.TicksPerSecond / sampleRateHz
        : throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
    private long _nextSampleIndex;

    public long LastPacketFirstSampleTicks { get; private set; }

    public void AdvanceMissingPackets(int missingPacketCount, int estimatedSamplesPerPacket)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(missingPacketCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(estimatedSamplesPerPacket);
        _nextSampleIndex = checked(
            _nextSampleIndex + (long)missingPacketCount * estimatedSamplesPerPacket
        );
    }

    public long GetNextPacketFirstSampleTicks(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        var packetFirstSampleTicks = checked(_nextSampleIndex * _sampleIntervalTicks);
        LastPacketFirstSampleTicks = packetFirstSampleTicks;
        _nextSampleIndex = checked(_nextSampleIndex + sampleCount);
        return packetFirstSampleTicks;
    }
}
