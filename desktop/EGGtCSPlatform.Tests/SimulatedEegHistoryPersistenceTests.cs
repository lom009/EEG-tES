using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SimulatedEegHistoryPersistenceTests
{
    [Fact]
    public async Task ConcurrentCompletionAndCanceledWaiterShareDurableResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eggtcs-completion-{Guid.NewGuid():N}");
        var finalizer = new PausedFinalizer();
        try
        {
            var id = Guid.NewGuid();
            var device = new SimulatedEggtCsDevice();
            await using var manager = new DeviceManager(connectedDevices: [device]);
            await using var store = new FileEegRawPacketStore(
                directory,
                artifactFinalizer: finalizer
            );
            using var service = new DeviceExperimentRunService(
                manager,
                new TestChannelMappingService([new("C4", 1)]),
                rawPacketRecorder: store
            );
            await service.BeginRecordingAsync(
                new(
                    id,
                    "EXP-TEST",
                    "SUB-TEST",
                    device.Identity.DeviceId.Value,
                    500,
                    ["C4"],
                    DateTimeOffset.UtcNow,
                    new(null, null, null)
                )
            );
            await store.AppendSamplesAsync(
                new(
                    id,
                    1,
                    DateTimeOffset.UtcNow,
                    0,
                    2d,
                    500,
                    0.002d,
                    1,
                    "Acquisition",
                    [new(1, new double[] { 1d, 2d })]
                )
            );
            var first = service.CompleteRecordingAsync(id, EegRecordingCompletionStatus.Canceled);
            try
            {
                await finalizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var concurrent = service.CompleteRecordingAsync(
                    id,
                    EegRecordingCompletionStatus.EmergencyStopped
                );
                using var cancellation = new CancellationTokenSource();
                var canceledWait = service.CompleteRecordingAsync(
                    id,
                    EegRecordingCompletionStatus.Completed,
                    cancellation.Token
                );
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
                Assert.False(first.IsCompleted);
                finalizer.Release.TrySetResult();
                var result = await first.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Same(result, await concurrent);
                Assert.Equal(2.004d, result.DataEndExclusiveSeconds!.Value, 8);
                Assert.Equal(1, finalizer.Calls);
                Assert.Equal(
                    EegRecordingCompletionStatus.Canceled,
                    (await store.GetSummaryAsync(id))!.CompletionStatus
                );
            }
            finally
            {
                finalizer.Release.TrySetResult();
                await first;
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PausedFinalizer : IEegArtifactFinalizer
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<EegArtifactResult> FinalizeAsync(
            EegRecordingSummary summary,
            Func<CancellationToken, IAsyncEnumerable<EegRecordedSampleBatch>> sampleSource,
            CancellationToken cancellationToken = default,
            string? outputStem = null
        )
        {
            Calls++;
            Started.TrySetResult();
            await Release.Task;
            return new(summary.FilePath + ".edf", summary.FilePath + ".csv", 2);
        }
    }

    [Fact]
    public async Task DisconnectedAcquisitionKeepsReceivedSamplesAndFinalizesAsFailed()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"eggtcs-sim-interrupted-{Guid.NewGuid():N}"
        );
        try
        {
            var id = Guid.NewGuid();
            var device = new SimulatedEggtCsDevice();
            await using var manager = new DeviceManager(connectedDevices: [device]);
            await using var store = new FileEegRawPacketStore(directory);
            using var service = new DeviceExperimentRunService(
                manager,
                new TestChannelMappingService([new("C4", 1)]),
                rawPacketRecorder: store
            );
            var received = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            service.TelemetryReceived += (_, e) =>
            {
                if (e.Telemetry.WaveformBatches.Count > 0)
                    received.TrySetResult();
            };
            await service.BeginRecordingAsync(
                new(
                    id,
                    "EXP-TEST",
                    "SUB-TEST",
                    device.Identity.DeviceId.Value,
                    500,
                    ["C4"],
                    DateTimeOffset.UtcNow,
                    new(null, null, null),
                    new Dictionary<int, string> { [1] = "C4" }
                )
            );
            var running = service.StartAcquisitionAsync(
                new(
                    TimeSpan.FromMinutes(1),
                    ["C4"],
                    500,
                    device.Identity.DeviceId.Value,
                    RecordingId: id
                )
            );
            await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await manager.RemoveAsync(device.Identity.DeviceId);
            await Assert.ThrowsAsync<EegAcquisitionException>(() =>
                running.WaitAsync(TimeSpan.FromSeconds(2))
            );
            await service.CompleteRecordingAsync(id, EegRecordingCompletionStatus.Failed);
            var summary = await store.GetSummaryAsync(id);
            Assert.NotNull(summary);
            Assert.True(summary.IsComplete);
            Assert.Equal(EegRecordingCompletionStatus.Failed, summary.CompletionStatus);
            Assert.True(summary.SampleBatchCount > 0);
            var sampleCount = 0;
            await foreach (var batch in store.ReadSamplesAsync(id))
                sampleCount += batch.SampleCount;
            Assert.True(sampleCount > 0);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class TestChannelMappingService(
        IReadOnlyList<EegPhysicalChannelMapping> mappings
    ) : IEegPhysicalChannelMappingService
    {
        public EegPhysicalChannelMappingSnapshot Load() =>
            new(EegPhysicalChannelMappingService.SupportedPhysicalChannelCount, mappings, []);

        public void Save(
            int physicalChannelCount,
            IReadOnlyList<EegPhysicalChannelMapping> values
        ) => throw new NotSupportedException();

        public string StoragePath => "memory";
    }

    [Fact]
    public async Task SimulatedRealtimeSamplesRoundTripThroughHistoricalPlayback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eggtcs-sim-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var recordingId = Guid.NewGuid();
            var device = new SimulatedEggtCsDevice();
            await using var manager = new DeviceManager(connectedDevices: [device]);
            await using var store = new FileEegRawPacketStore(directory);
            using var service = new DeviceExperimentRunService(
                manager,
                new TestChannelMappingService([
                    new EegPhysicalChannelMapping("C4", 1),
                    new EegPhysicalChannelMapping("C6", 2),
                ]),
                rawPacketRecorder: store
            );
            var live = new List<WaveformChannelBatch>();
            service.TelemetryReceived += (_, args) => live.AddRange(args.Telemetry.WaveformBatches);
            var startedAt = DateTimeOffset.UtcNow;
            await service.BeginRecordingAsync(
                new EegRecordingMetadata(
                    recordingId,
                    "EXP-SIM-HISTORY",
                    "SUB-001",
                    device.Identity.DeviceId.Value,
                    500,
                    ["C4", "C6"],
                    startedAt,
                    new EegDisplayFilterSettings(null, null, null),
                    new Dictionary<int, string> { [1] = "C4", [2] = "C6" }
                )
            );

            await service.StartAcquisitionAsync(
                new AcquisitionRunRequest(
                    TimeSpan.FromMilliseconds(1_100),
                    ["C4", "C6"],
                    500,
                    device.Identity.DeviceId.Value,
                    TimelineOffset: TimeSpan.FromSeconds(2),
                    RecordingId: recordingId
                )
            );
            var statistics = await service.CompleteRecordingAsync(
                recordingId,
                EegRecordingCompletionStatus.Completed
            );
            var summary = await store.GetSummaryAsync(recordingId);

            Assert.NotNull(summary);
            Assert.Equal(0, summary.PacketCount);
            Assert.True(summary.SampleBatchCount > 0);
            Assert.True(statistics.PacketStatistics.ReceivedPacketCount > 0);
            Assert.All(
                live.GroupBy(batch => batch.StartTimeSeconds),
                group =>
                    Assert.Equal(
                        new[] { "C4", "C6" },
                        group.Select(batch => batch.ChannelId).OrderBy(channel => channel)
                    )
            );

            var history = await new EegHistoryWindowProvider(store).LoadAsync(
                new EegHistoryWindowRequest(
                    recordingId,
                    2d,
                    3.1d,
                    500,
                    new EegDisplayFilterSettings(null, null, null),
                    new Dictionary<int, string> { [1] = "C4", [2] = "C6" },
                    1,
                    3.1d,
                    2_000
                )
            );

            foreach (var channelId in new[] { "C4", "C6" })
            {
                var expected = live.Where(batch => batch.ChannelId == channelId)
                    .SelectMany(batch =>
                        batch.Samples.Select(
                            (value, index) =>
                                (
                                    Time: batch.StartTimeSeconds
                                        + index * batch.SampleIntervalSeconds,
                                    Value: value
                                )
                        )
                    )
                    .ToArray();
                var actual = history
                    .Channels.Single(channel => channel.ChannelId == channelId)
                    .Samples;
                Assert.NotEmpty(actual);
                Assert.Equal(expected.Length, actual.Count);
                for (var index = 0; index < expected.Length; index++)
                {
                    Assert.Equal(expected[index].Time, actual[index].TimeSeconds, 10);
                    Assert.Equal(expected[index].Value, actual[index].Value, 10);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
