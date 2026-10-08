using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SimulatedEggtCsDeviceTests
{
    [Theory]
    [InlineData(250, 15_000, 3_750)]
    [InlineData(500, 15_000, 7_500)]
    [InlineData(500, 123, 62)]
    public void AcquisitionScheduleCalculatesCompleteSampleRange(
        int sampleRateHz,
        int durationMilliseconds,
        long expectedSampleCount
    )
    {
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);

        var sampleCount = SimulatedAcquisitionSchedule.GetTotalSampleCount(duration, sampleRateHz);

        Assert.Equal(expectedSampleCount, sampleCount);
        if (sampleCount > 0)
        {
            var lastSampleTime = (sampleCount - 1d) / sampleRateHz;
            var nextSampleTime = sampleCount / (double)sampleRateHz;
            Assert.True(lastSampleTime < duration.TotalSeconds);
            Assert.True(nextSampleTime >= duration.TotalSeconds);
        }
    }

    [Fact]
    public void AcquisitionScheduleCatchesUpAfterDelayedIterationAndCapsAtDuration()
    {
        var duration = TimeSpan.FromSeconds(1);

        Assert.Equal(
            25,
            SimulatedAcquisitionSchedule.GetTargetSampleCount(
                TimeSpan.FromMilliseconds(50),
                duration,
                500
            )
        );
        Assert.Equal(
            144,
            SimulatedAcquisitionSchedule.GetTargetSampleCount(
                TimeSpan.FromMilliseconds(287),
                duration,
                500
            )
        );
        Assert.Equal(
            500,
            SimulatedAcquisitionSchedule.GetTargetSampleCount(
                TimeSpan.FromSeconds(2),
                duration,
                500
            )
        );
    }

    [Theory]
    [InlineData(137, 13)]
    [InlineData(150, 50)]
    public void AcquisitionScheduleWaitsForAbsoluteCadence(
        int elapsedMilliseconds,
        int expectedDelayMilliseconds
    )
    {
        var delay = SimulatedAcquisitionSchedule.GetDelayUntilNextCadence(
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            TimeSpan.FromMilliseconds(50)
        );

        Assert.Equal(TimeSpan.FromMilliseconds(expectedDelayMilliseconds), delay);
    }

    [Theory]
    [InlineData(49.99, 1)]
    [InlineData(49.1, 1)]
    [InlineData(37.1, 13)]
    public void AcquisitionScheduleDoesNotBusySpinOnFractionalMillisecondDelays(
        double elapsedMilliseconds,
        int expectedMilliseconds
    )
    {
        var delay = SimulatedAcquisitionSchedule.GetDelayUntilNextCadence(
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            TimeSpan.FromMilliseconds(50)
        );
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
        Assert.True(delay.TotalMilliseconds >= 1);
    }

    [Theory]
    [InlineData(250, 2)]
    [InlineData(500, 2)]
    [InlineData(500, 32)]
    public async Task SimulatedAcquisitionKeepsEveryChannelContinuousUntilConfiguredDuration(
        int rate,
        int channelCount
    )
    {
        var duration = TimeSpan.FromMilliseconds(1_100);
        await using var device = new SimulatedEggtCsDevice();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = device.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
        var nextEvent = events.MoveNextAsync().AsTask();

        var selectedChannels = Enumerable.Range(1, channelCount).ToHashSet();
        var result = await device.EegAcquisition.StartAsync(
            duration,
            selectedChannels,
            rate,
            timeout.Token
        );
        Assert.True(result.IsSuccess);

        var received = new List<DeviceEvent>();
        while (await nextEvent.WaitAsync(timeout.Token))
        {
            received.Add(events.Current.Event);
            if (events.Current.Event is AcquisitionCompletedEvent)
                break;
            nextEvent = events.MoveNextAsync().AsTask();
        }

        var completionIndex = received.FindIndex(item => item is AcquisitionCompletedEvent);
        var lastDataIndex = received.FindLastIndex(item => item is EegSamplesReceivedEvent);
        Assert.True(lastDataIndex >= 0);
        Assert.True(completionIndex > lastDataIndex);

        var dataEvents = received.OfType<EegSamplesReceivedEvent>().ToArray();
        Assert.NotEmpty(dataEvents);
        Assert.All(
            dataEvents,
            item =>
                Assert.Equal(
                    selectedChannels.Order(),
                    item.Channels.Select(channel => channel.PhysicalChannel)
                        .OrderBy(channel => channel)
                )
        );

        var channelBatches = received
            .OfType<EegSamplesReceivedEvent>()
            .SelectMany(item => item.Channels)
            .GroupBy(item => item.PhysicalChannel)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.StartTimeSeconds).ToArray()
            );
        Assert.Equal(selectedChannels.Order(), channelBatches.Keys.OrderBy(item => item));

        foreach (var batches in channelBatches.Values)
        {
            Assert.Equal(
                (int)(duration.TotalSeconds * rate),
                batches.Sum(batch => batch.Samples.Count)
            );
            foreach (var batch in batches)
                for (var i = 0; i < batch.Samples.Count; i++)
                    Assert.Equal(
                        SimulatedEegSignal.Sample(
                            SimulatedEegSignal.DefaultSeed,
                            SimulatedEegSignal.DefaultSubject,
                            batch.PhysicalChannel,
                            (long)Math.Round(batch.StartTimeSeconds * rate) + i,
                            rate
                        ),
                        batch.Samples[i]
                    );
            var last = batches[^1];
            var coveredUntil =
                last.StartTimeSeconds + last.Samples.Count * last.SampleIntervalSeconds;
            Assert.Equal(duration.TotalSeconds, coveredUntil, 10);
            Assert.True(
                last.StartTimeSeconds + (last.Samples.Count - 1) * last.SampleIntervalSeconds
                    < duration.TotalSeconds
            );
        }

        Assert.All(channelBatches.Values, batches => Assert.False(HasDiscontinuity(batches)));
    }

    [Fact]
    public async Task CancelledSimulatedAcquisitionDoesNotPublishNormalCompletion()
    {
        await using var device = new SimulatedEggtCsDevice();
        using var readerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = device
            .ReadEventsAsync(readerCancellation.Token)
            .GetAsyncEnumerator();
        var nextEvent = events.MoveNextAsync().AsTask();

        var result = await device.EegAcquisition.StartAsync(
            TimeSpan.FromSeconds(5),
            new HashSet<int> { 1, 2 },
            500,
            readerCancellation.Token
        );
        Assert.True(result.IsSuccess);

        var received = new List<DeviceEvent>();
        while (await nextEvent.WaitAsync(readerCancellation.Token))
        {
            received.Add(events.Current.Event);
            if (events.Current.Event is EegSamplesReceivedEvent)
                break;
            nextEvent = events.MoveNextAsync().AsTask();
        }

        await device.EegAcquisition.StopAsync();
        readerCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await events.MoveNextAsync())
                received.Add(events.Current.Event);
        }
        catch (OperationCanceledException) when (readerCancellation.IsCancellationRequested) { }

        Assert.DoesNotContain(received, item => item is AcquisitionCompletedEvent);
    }

    private static bool HasDiscontinuity(IReadOnlyList<EegChannelSamples> batches)
    {
        for (var index = 1; index < batches.Count; index++)
        {
            var previous = batches[index - 1];
            var expectedStart =
                previous.StartTimeSeconds + previous.Samples.Count * previous.SampleIntervalSeconds;
            if (
                batches[index].StartTimeSeconds - expectedStart
                > previous.SampleIntervalSeconds * 0.5d
            )
                return true;
        }
        return false;
    }
}
