using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegRawPacketStoreTests
{
    [Fact]
    public async Task CompletionBoundaryIncludesLastSampleAndIgnoresEarlierArrivals()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var id = Guid.NewGuid();
            await using var store = new FileEegRawPacketStore(directory);
            await store.BeginAsync(CreateMetadata(id, DateTimeOffset.UtcNow));
            await store.AppendAsync(CreatePacket(id, 1, 2d, 1, CreateV101EegFrame(1200)));
            // Completion drains buffered records, so assert the durable boundary after completing.
            await store.AppendSamplesAsync(
                new(
                    id,
                    2,
                    DateTimeOffset.UtcNow,
                    0,
                    5d,
                    500,
                    0.002d,
                    2,
                    "Acquisition",
                    [new(1, new double[] { 1d, 2d, 3d })]
                )
            );
            await store.AppendSamplesAsync(
                new(
                    id,
                    3,
                    DateTimeOffset.UtcNow,
                    0,
                    1d,
                    500,
                    0.002d,
                    1,
                    "Acquisition",
                    [new(1, new double[] { 1d })]
                )
            );
            var completed = await store.CompleteAsync(id, EegRecordingCompletionStatus.Canceled);
            Assert.Equal(5.006d, completed.DataEndExclusiveSeconds!.Value, 8);
            Assert.Equal(
                completed.DataEndExclusiveSeconds,
                (await store.GetSummaryAsync(id))!.DataEndExclusiveSeconds
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionBoundarySupportsRawPacketsAndEmptyRecordings(bool hasPacket)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var id = Guid.NewGuid();
            await using var store = new FileEegRawPacketStore(directory);
            await store.BeginAsync(CreateMetadata(id, DateTimeOffset.UtcNow));
            if (hasPacket)
                await store.AppendAsync(
                    CreatePacket(id, 1, 3d, 1, CreateV101EegFrame(1200)) with
                    {
                        SampleCount = 1,
                    }
                );
            var completed = await store.CompleteAsync(id, EegRecordingCompletionStatus.Canceled);
            var reopened = await store.GetSummaryAsync(id);
            if (hasPacket)
                Assert.Equal(3.002d, completed.DataEndExclusiveSeconds!.Value, 8);
            else
                Assert.Null(completed.DataEndExclusiveSeconds);
            Assert.Equal(completed.DataEndExclusiveSeconds, reopened!.DataEndExclusiveSeconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MultipleFramesInOneHistoricalRecordAreAllDecodedWithoutChangingRawBytes()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var id = Guid.NewGuid();
            var bytes = CreateV101EegFrame(1200).Concat(CreateV101EegFrame(2400)).ToArray();
            await using var store = new FileEegRawPacketStore(directory);
            await store.BeginAsync(CreateMetadata(id, DateTimeOffset.UtcNow));
            await store.AppendAsync(CreatePacket(id, 1, 0d, 1, bytes));
            await store.CompleteAsync(id, EegRecordingCompletionStatus.Completed);
            var samples = await ReadAllSamplesAsync(store.ReadSamplesAsync(id));
            var channel = Assert.Single(samples).Channels.Single(item => item.PhysicalChannel == 1);
            Assert.Equal(2, channel.Samples.Count);
            Assert.Equal(channel.Samples[0] * 2, channel.Samples[1]);
            Assert.Equal(
                bytes,
                Assert.Single(await ReadAllAsync(store.ReadPacketsAsync(id))).Datagram.ToArray()
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HistoricalReaderUsesInjectedDecoder()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var id = Guid.NewGuid();
            await using var store = new FileEegRawPacketStore(
                directory,
                packetDecoderFactory: () => new AlternateDecoder()
            );
            await store.BeginAsync(CreateMetadata(id, DateTimeOffset.UtcNow));
            await store.AppendAsync(CreatePacket(id, 1, 0d, 1, [42]));
            await store.CompleteAsync(id, EegRecordingCompletionStatus.Completed);
            var batch = Assert.Single(await ReadAllSamplesAsync(store.ReadSamplesAsync(id)));
            Assert.Equal(42d, Assert.Single(Assert.Single(batch.Channels).Samples));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class AlternateDecoder : EGGtCSPlatform.DeviceSdk.IEegPacketDecoder
    {
        public IReadOnlyList<EGGtCSPlatform.DeviceSdk.EegDataPacketReceivedEvent> Decode(
            ReadOnlyMemory<byte> bytes,
            bool endOfPacket = true
        ) => [new(TimeSpan.Zero, 80, 1, [new(1, new double[] { bytes.Span[0] })], 0)];
    }

    [Fact]
    public async Task VersionOneRawRecordingRemainsReadableThroughUnifiedSampleReader()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            string recordingPath;
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendAsync(
                    CreatePacket(recordingId, 1, 3d, 1, CreateV101EegFrame(1200))
                );
                recordingPath = (
                    await writer.CompleteAsync(recordingId, EegRecordingCompletionStatus.Completed)
                ).FilePath;
            }

            await using (
                var stream = new FileStream(
                    recordingPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            using (var writer = new BinaryWriter(stream))
            {
                stream.Seek(8, SeekOrigin.Begin);
                writer.Write(1);
            }
            File.Delete(recordingPath + ".idx");

            await using var reader = new FileEegRawPacketStore(directory);
            var summary = await reader.GetSummaryAsync(recordingId);
            var samples = await ReadAllSamplesAsync(reader.ReadSamplesAsync(recordingId, 2d, 4d));

            Assert.NotNull(summary);
            Assert.Equal(1, summary.PacketCount);
            Assert.Equal(0, summary.SampleBatchCount);
            var batch = Assert.Single(samples);
            Assert.Equal(3d, batch.TimelineStartSeconds);
            Assert.Contains(
                batch.Channels,
                channel => channel.PhysicalChannel == 1 && channel.Samples.Count == 1
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VersionTwoSampleBatchesRoundTripWithChannelGapsAndRangeIndex()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            var first = CreateSampleBatch(
                recordingId,
                1,
                1d,
                [
                    new EegRecordedChannelSamples(1, new double[] { 1, 2, 3 }),
                    new EegRecordedChannelSamples(2, new double[] { 4, 5, 6 }),
                ]
            );
            var second = CreateSampleBatch(
                recordingId,
                2,
                2d,
                [new EegRecordedChannelSamples(1, new double[] { 7, 8, 9 })]
            );
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendSamplesAsync(first);
                await writer.AppendSamplesAsync(second);
                var summary = await writer.CompleteAsync(
                    recordingId,
                    EegRecordingCompletionStatus.Completed
                );

                Assert.Equal(0, summary.PacketCount);
                Assert.Equal(2, summary.SampleBatchCount);
            }

            await using var reader = new FileEegRawPacketStore(directory);
            var summaryAfterReload = await reader.GetSummaryAsync(recordingId);
            var samples = await ReadAllSamplesAsync(
                reader.ReadSamplesAsync(recordingId, 1.5d, 2.5d)
            );

            Assert.NotNull(summaryAfterReload);
            Assert.Equal(2, summaryAfterReload.SampleBatchCount);
            var loaded = Assert.Single(samples);
            Assert.Equal(2, loaded.Sequence);
            Assert.Equal(new[] { 1 }, loaded.Channels.Select(channel => channel.PhysicalChannel));
            Assert.Equal(new double[] { 7, 8, 9 }, loaded.Channels[0].Samples);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedRecordingRoundTripsExactDatagramsAndMetadata()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
            var metadata = CreateMetadata(recordingId, startedAt);
            var firstBytes = new byte[] { 0xAA, 0x01, 0x10, 0xFE };
            var secondBytes = new byte[] { 0xAA, 0x02, 0x20, 0x30, 0xFD };
            await using (var writer = new FileEegRawPacketStore(directory, queueCapacity: 8))
            {
                await writer.BeginAsync(metadata);
                await writer.AppendAsync(CreatePacket(recordingId, 1, 1.25d, 1, firstBytes));
                await writer.AppendFilterChangeAsync(
                    new EegFilterChangeRecord(
                        recordingId,
                        startedAt.AddSeconds(1),
                        1d,
                        new EegDisplayFilterSettings(null, 30d, 50d)
                    )
                );
                await writer.AppendAsync(CreatePacket(recordingId, 2, 1.258d, 2, secondBytes));
                var completed = await writer.CompleteAsync(
                    recordingId,
                    EegRecordingCompletionStatus.Completed
                );

                Assert.True(completed.IsComplete);
                Assert.Equal(2, completed.PacketCount);
                Assert.Equal(firstBytes.Length + secondBytes.Length, completed.DatagramBytes);
                Assert.EndsWith(".eegraw", completed.FilePath, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(directory, $"{recordingId:N}.eegraw.tmp")));
                Assert.True(File.Exists(Path.Combine(directory, $"{recordingId:N}.eegraw.idx")));
                Assert.False(
                    File.Exists(Path.Combine(directory, $"{recordingId:N}.eegraw.idx.tmp"))
                );
            }

            await using var reader = new FileEegRawPacketStore(directory);
            var summary = await reader.GetSummaryAsync(recordingId);
            Assert.NotNull(summary);
            Assert.Equal("experiment-1", summary.Metadata.ExperimentId);
            Assert.Equal("subject-1", summary.Metadata.SubjectId);
            Assert.Equal(EegRecordingCompletionStatus.Completed, summary.CompletionStatus);
            var packets = await ReadAllAsync(reader.ReadPacketsAsync(recordingId));
            Assert.Equal(new long[] { 1, 2 }, packets.Select(packet => packet.Sequence));
            Assert.Equal(firstBytes, packets[0].Datagram.ToArray());
            Assert.Equal(secondBytes, packets[1].Datagram.ToArray());
            Assert.Equal(new[] { 1, 2 }, packets.Select(packet => packet.CurrentCycle));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisposedActiveRecordingRemainsReadableAndMarkedIncomplete()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendAsync(
                    CreatePacket(recordingId, 1, 0d, 1, new byte[] { 1, 2, 3 })
                );
            }

            await using var reader = new FileEegRawPacketStore(directory);
            var summary = await reader.GetSummaryAsync(recordingId);
            Assert.NotNull(summary);
            Assert.False(summary.IsComplete);
            Assert.Null(summary.CompletionStatus);
            Assert.EndsWith(".eegraw.tmp", summary.FilePath, StringComparison.OrdinalIgnoreCase);
            var packets = await ReadAllAsync(reader.ReadPacketsAsync(recordingId));
            Assert.Single(packets);
            Assert.Equal(new byte[] { 1, 2, 3 }, packets[0].Datagram.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TimeRangeReaderReturnsOnlyMatchingPackets()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using var store = new FileEegRawPacketStore(directory);
            await store.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
            await store.AppendAsync(CreatePacket(recordingId, 1, 2d, 1, new byte[] { 1 }));
            await store.AppendAsync(CreatePacket(recordingId, 2, 8d, 1, new byte[] { 2 }));
            await store.CompleteAsync(recordingId, EegRecordingCompletionStatus.Completed);

            var packets = await ReadAllAsync(store.ReadPacketsAsync(recordingId, 5d, 10d));

            Assert.Single(packets);
            Assert.Equal(2, packets[0].Sequence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatedTemporaryTailStillRecoversEarlierCompletePackets()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendAsync(
                    CreatePacket(recordingId, 1, 0d, 1, new byte[] { 1, 2, 3 })
                );
                await writer.AppendAsync(
                    CreatePacket(recordingId, 2, 1d, 1, new byte[] { 4, 5, 6 })
                );
            }
            var path = Path.Combine(directory, $"{recordingId:N}.eegraw.tmp");
            await using (
                var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)
            )
                stream.SetLength(stream.Length - 2);

            await using var reader = new FileEegRawPacketStore(directory);
            var packets = await ReadAllAsync(reader.ReadPacketsAsync(recordingId));
            var summary = await reader.GetSummaryAsync(recordingId);

            Assert.Single(packets);
            Assert.Equal(1, packets[0].Sequence);
            Assert.NotNull(summary);
            Assert.False(summary.IsComplete);
            Assert.Equal(1, summary.PacketCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatedTemporarySampleBatchTailStillRecoversEarlierBatch()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendSamplesAsync(
                    CreateSampleBatch(
                        recordingId,
                        1,
                        0d,
                        [new EegRecordedChannelSamples(1, new double[] { 1, 2, 3 })]
                    )
                );
                await writer.AppendSamplesAsync(
                    CreateSampleBatch(
                        recordingId,
                        2,
                        1d,
                        [new EegRecordedChannelSamples(1, new double[] { 4, 5, 6 })]
                    )
                );
            }
            var path = Path.Combine(directory, $"{recordingId:N}.eegraw.tmp");
            await using (
                var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)
            )
                stream.SetLength(stream.Length - sizeof(double));

            await using var reader = new FileEegRawPacketStore(directory);
            var batches = await ReadAllSamplesAsync(reader.ReadSamplesAsync(recordingId));
            var summary = await reader.GetSummaryAsync(recordingId);

            var loaded = Assert.Single(batches);
            Assert.Equal(1, loaded.Sequence);
            Assert.NotNull(summary);
            Assert.Equal(1, summary.SampleBatchCount);
            Assert.False(summary.IsComplete);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptCompletedIndexIsRebuiltForRangeReads(bool corrupt)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using (var writer = new FileEegRawPacketStore(directory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendAsync(CreatePacket(recordingId, 1, 2d, 1, new byte[] { 1 }));
                await writer.AppendAsync(CreatePacket(recordingId, 2, 8d, 1, new byte[] { 2 }));
                await writer.CompleteAsync(recordingId, EegRecordingCompletionStatus.Completed);
            }
            var indexPath = Path.Combine(directory, $"{recordingId:N}.eegraw.idx");
            if (corrupt)
                await File.WriteAllBytesAsync(indexPath, new byte[] { 1, 2, 3, 4 });
            else
                File.Delete(indexPath);

            await using var reader = new FileEegRawPacketStore(directory);
            var packets = await ReadAllAsync(reader.ReadPacketsAsync(recordingId, 7d, 9d));

            Assert.Single(packets);
            Assert.Equal(2, packets[0].Sequence);
            Assert.True(File.Exists(indexPath));
            Assert.True(new FileInfo(indexPath).Length > 32);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRemovesRawRecordingAndItsIndex()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            await using var store = new FileEegRawPacketStore(directory);
            await store.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
            await store.AppendAsync(CreatePacket(recordingId, 1, 0d, 1, new byte[] { 1 }));
            await store.CompleteAsync(recordingId, EegRecordingCompletionStatus.Completed);

            await store.DeleteAsync(recordingId);

            Assert.False(File.Exists(Path.Combine(directory, $"{recordingId:N}.eegraw")));
            Assert.False(File.Exists(Path.Combine(directory, $"{recordingId:N}.eegraw.idx")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitFilePathSupportsPlaybackOutsideConfiguredDirectoryAndRebuildsIndex()
    {
        var recordingDirectory = CreateTemporaryDirectory();
        var unrelatedDirectory = CreateTemporaryDirectory();
        try
        {
            var recordingId = Guid.NewGuid();
            string rawPath;
            await using (var writer = new FileEegRawPacketStore(recordingDirectory))
            {
                await writer.BeginAsync(CreateMetadata(recordingId, DateTimeOffset.UtcNow));
                await writer.AppendAsync(CreatePacket(recordingId, 1, 3d, 1, [1, 2, 3]));
                rawPath = (
                    await writer.CompleteAsync(recordingId, EegRecordingCompletionStatus.Completed)
                ).FilePath;
            }
            File.Delete(rawPath + ".idx");

            await using var reader = new FileEegRawPacketStore(unrelatedDirectory);
            var summary = await reader.GetSummaryFromFileAsync(recordingId, rawPath);
            var packets = await ReadAllAsync(
                reader.ReadPacketsFromFileAsync(recordingId, rawPath, 2d, 4d)
            );

            Assert.NotNull(summary);
            Assert.Single(packets);
            Assert.Equal(recordingId, packets[0].RecordingId);
            Assert.True(File.Exists(rawPath + ".idx"));
        }
        finally
        {
            Directory.Delete(recordingDirectory, recursive: true);
            Directory.Delete(unrelatedDirectory, recursive: true);
        }
    }

    private static EegRecordingMetadata CreateMetadata(
        Guid recordingId,
        DateTimeOffset startedAt
    ) =>
        new(
            recordingId,
            "experiment-1",
            "subject-1",
            "device-1",
            500,
            new[] { "Fp1", "Fp2" },
            startedAt,
            new EegDisplayFilterSettings(0.5d, 70d, null)
        );

    private static EegRawPacketRecord CreatePacket(
        Guid recordingId,
        long sequence,
        double timeline,
        int cycle,
        byte[] bytes
    ) =>
        new(
            recordingId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            sequence * 100,
            timeline,
            500,
            4,
            cycle,
            "Acquisition",
            bytes
        );

    private static EegRecordedSampleBatch CreateSampleBatch(
        Guid recordingId,
        long sequence,
        double timeline,
        IReadOnlyList<EegRecordedChannelSamples> channels
    ) =>
        new(
            recordingId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            sequence * 100,
            timeline,
            500,
            1d / 500d,
            1,
            "Acquisition",
            channels
        );

    private static byte[] CreateV101EegFrame(int channelOneRawValue)
    {
        var payload = new byte[3 + 32 * 3 + 1];
        payload[2] = 32;
        payload[3] = (byte)(channelOneRawValue >> 16);
        payload[4] = (byte)(channelOneRawValue >> 8);
        payload[5] = (byte)channelOneRawValue;
        payload[^1] = 80;
        var frame = new byte[6 + payload.Length + 2];
        frame[0] = 0xAA;
        frame[1] = 0xBB;
        frame[2] = 1;
        frame[3] = (byte)frame.Length;
        frame[4] = (byte)(frame.Length >> 8);
        frame[5] = (byte)EggtCsCommandCode.EegData;
        payload.CopyTo(frame, 6);
        frame[^2] = 0xFF;
        frame[^1] = 0xFF;
        return frame;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eggtcs-eegraw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<List<EegRawPacketRecord>> ReadAllAsync(
        IAsyncEnumerable<EegRawPacketRecord> source
    )
    {
        var result = new List<EegRawPacketRecord>();
        await foreach (var packet in source)
            result.Add(packet);
        return result;
    }

    private static async Task<List<EegRecordedSampleBatch>> ReadAllSamplesAsync(
        IAsyncEnumerable<EegRecordedSampleBatch> source
    )
    {
        var result = new List<EegRecordedSampleBatch>();
        await foreach (var batch in source)
            result.Add(batch);
        return result;
    }
}
