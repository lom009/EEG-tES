using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels.Pages;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EegHistoryWindowProviderTests
{
    private const double MicrovoltScale = 2d * 4.5d / (12d * (1 << 24)) * 1_000_000d;

    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public async Task OldWindowUsesCurrentFiltersAndMatchesSequentialOfflineCalculation(
        int sampleRateHz
    )
    {
        var recordingId = Guid.NewGuid();
        var records = CreateRecords(recordingId, sampleRateHz, durationSeconds: 72d);
        var reader = new InMemoryRawPacketReader(records);
        var provider = new EegHistoryWindowProvider(reader);
        var settings = new EegDisplayFilterSettings(0.5d, 70d, 50d);
        var request = new EegHistoryWindowRequest(
            recordingId,
            65d,
            70d,
            sampleRateHz,
            settings,
            new Dictionary<int, string> { [1] = "Fp1" },
            1,
            72d,
            MaximumPointsPerChannel: sampleRateHz * 6
        );

        var result = await provider.LoadAsync(request);

        var actual = Assert.Single(result.Channels).Samples;
        var expected = CalculateExpected(records, sampleRateHz, settings, 65d, 70d);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].TimeSeconds, actual[index].TimeSeconds, 10);
            Assert.Equal(expected[index].Value, actual[index].Value, 9);
        }
    }

    [Fact]
    public async Task ChangingFiltersReusesDecodedRawCacheButProducesNewValues()
    {
        var recordingId = Guid.NewGuid();
        var records = CreateRecords(recordingId, 250, durationSeconds: 72d);
        var reader = new InMemoryRawPacketReader(records);
        var provider = new EegHistoryWindowProvider(reader);
        var firstRequest = CreateRequest(
            recordingId,
            new EegDisplayFilterSettings(null, null, null)
        );

        var unfiltered = await provider.LoadAsync(firstRequest);
        var readsAfterFirstLoad = reader.ReadCount;
        var filtered = await provider.LoadAsync(
            firstRequest with
            {
                FilterSettings = new EegDisplayFilterSettings(1d, 30d, 50d),
            }
        );

        Assert.Equal(readsAfterFirstLoad, reader.ReadCount);
        Assert.False(
            unfiltered
                .Channels[0]
                .Samples.Select(point => point.Value)
                .SequenceEqual(filtered.Channels[0].Samples.Select(point => point.Value))
        );
    }

    [Fact]
    public async Task CachedPreviewNeverReadsRawPacketsAndUsesPreviouslyDecodedChunks()
    {
        var recordingId = Guid.NewGuid();
        var reader = new InMemoryRawPacketReader(
            CreateRecords(recordingId, 250, durationSeconds: 72d)
        );
        var provider = new EegHistoryWindowProvider(reader);
        var request = CreateRequest(recordingId, new EegDisplayFilterSettings(0.5d, 70d, 50d));

        var miss = await provider.LoadCachedAsync(request with { MaximumPointsPerChannel = 40 });

        Assert.Null(miss);
        Assert.Equal(0, reader.ReadCount);

        await provider.LoadAsync(request);
        var readsAfterDetailedLoad = reader.ReadCount;
        var preview = await provider.LoadCachedAsync(request with { MaximumPointsPerChannel = 40 });

        Assert.NotNull(preview);
        Assert.Equal(readsAfterDetailedLoad, reader.ReadCount);
        Assert.InRange(Assert.Single(preview.Channels).Samples.Count, 2, 40);
    }

    [Fact]
    public async Task StageAndTimeDiscontinuitiesStartNewVisibleSegments()
    {
        var recordingId = Guid.NewGuid();
        var records = new[]
        {
            CreateRecord(recordingId, 1, 0d, 250, 1, [1, 2, 3, 4]),
            CreateRecord(recordingId, 2, 0.016d, 250, 1, [5, 6, 7, 8], "FinalAcquisition"),
            CreateRecord(recordingId, 3, 2d, 250, 1, [9, 10, 11, 12], "FinalAcquisition"),
        };
        var provider = new EegHistoryWindowProvider(new InMemoryRawPacketReader(records));

        var result = await provider.LoadAsync(
            new EegHistoryWindowRequest(
                recordingId,
                0d,
                3d,
                250,
                new EegDisplayFilterSettings(0.5d, 70d, null),
                new Dictionary<int, string> { [1] = "Fp1" },
                1,
                3d,
                100
            )
        );

        var points = Assert.Single(result.Channels).Samples;
        Assert.True(points[0].StartsNewSegment);
        Assert.True(
            points
                .Single(point => Math.Abs(point.TimeSeconds - 0.016d) < 0.0000001d)
                .StartsNewSegment
        );
        Assert.True(
            points.Single(point => Math.Abs(point.TimeSeconds - 2d) < 0.0000001d).StartsNewSegment
        );
    }

    [Fact]
    public async Task CancellationStopsSlowHistoricalRead()
    {
        var recordingId = Guid.NewGuid();
        var reader = new InMemoryRawPacketReader(
            CreateRecords(recordingId, 250, durationSeconds: 72d),
            TimeSpan.FromMilliseconds(20)
        );
        var provider = new EegHistoryWindowProvider(reader);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.LoadAsync(
                CreateRequest(recordingId, new EegDisplayFilterSettings(0.5d, 70d, null)),
                cancellation.Token
            )
        );
    }

    private static EegHistoryWindowRequest CreateRequest(
        Guid recordingId,
        EegDisplayFilterSettings settings
    ) =>
        new(
            recordingId,
            65d,
            70d,
            250,
            settings,
            new Dictionary<int, string> { [1] = "Fp1" },
            1,
            72d,
            2000
        );

    private static IReadOnlyList<TimedWaveformPoint> CalculateExpected(
        IReadOnlyList<EegRawPacketRecord> records,
        int sampleRateHz,
        EegDisplayFilterSettings settings,
        double startSeconds,
        double endSeconds
    )
    {
        var filter = new CausalEegDisplayFilter(sampleRateHz, settings);
        var warmupStart = Math.Max(0d, startSeconds - 60d);
        var result = new List<TimedWaveformPoint>();
        foreach (
            var record in records.Where(record =>
                PacketEnd(record) >= warmupStart && record.TimelineStartSeconds <= endSeconds
            )
        )
        {
            var values = DecodeFirstChannelRawValues(record.Datagram.Span);
            for (var index = 0; index < values.Count; index++)
            {
                var time = record.TimelineStartSeconds + index / (double)sampleRateHz;
                var value = filter.Process(values[index] * MicrovoltScale);
                if (time >= startSeconds && time <= endSeconds)
                    result.Add(new TimedWaveformPoint(time, value, result.Count == 0));
            }
        }
        return result;
    }

    private static IReadOnlyList<EegRawPacketRecord> CreateRecords(
        Guid recordingId,
        int sampleRateHz,
        double durationSeconds
    )
    {
        const int samplesPerPacket = 8;
        var packetCount = (int)Math.Ceiling(durationSeconds * sampleRateHz / samplesPerPacket);
        var records = new List<EegRawPacketRecord>(packetCount);
        for (var packet = 0; packet < packetCount; packet++)
        {
            var values = Enumerable
                .Range(0, samplesPerPacket)
                .Select(sample =>
                {
                    var sampleIndex = packet * samplesPerPacket + sample;
                    return (int)
                        Math.Round(
                            Math.Sin(2d * Math.PI * 10d * sampleIndex / sampleRateHz) * 200_000d
                                + Math.Sin(2d * Math.PI * 50d * sampleIndex / sampleRateHz)
                                    * 80_000d
                        );
                })
                .ToArray();
            records.Add(
                CreateRecord(
                    recordingId,
                    packet + 1,
                    packet * samplesPerPacket / (double)sampleRateHz,
                    sampleRateHz,
                    1,
                    values
                )
            );
        }
        return records;
    }

    private static EegRawPacketRecord CreateRecord(
        Guid recordingId,
        long sequence,
        double timelineSeconds,
        int sampleRateHz,
        int cycle,
        IReadOnlyList<int> firstChannelRawValues,
        string acquisitionStage = "Acquisition"
    ) =>
        new(
            recordingId,
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(timelineSeconds),
            sequence,
            timelineSeconds,
            sampleRateHz,
            firstChannelRawValues.Count,
            cycle,
            acquisitionStage,
            CreateEegFrame((byte)sequence, firstChannelRawValues)
        );

    private static byte[] CreateEegFrame(byte index, IReadOnlyList<int> firstChannelRawValues)
    {
        const int channelCount = 32;
        var payload = new byte[3 + channelCount * firstChannelRawValues.Count * 3 + 1];
        payload[2] = channelCount;
        for (var sample = 0; sample < firstChannelRawValues.Count; sample++)
        {
            var raw = firstChannelRawValues[sample];
            var offset = 3 + sample * channelCount * 3;
            payload[offset] = (byte)(raw >> 16);
            payload[offset + 1] = (byte)(raw >> 8);
            payload[offset + 2] = (byte)raw;
        }
        payload[^1] = 80;
        var length = checked(8 + payload.Length);
        var frame = new byte[length];
        frame[0] = 0xAA;
        frame[1] = 0xBB;
        frame[2] = index;
        frame[3] = (byte)length;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)EggtCsCommandCode.EegData;
        payload.CopyTo(frame, 6);
        frame[^2] = 0xFF;
        frame[^1] = 0xFF;
        return frame;
    }

    private static IReadOnlyList<int> DecodeFirstChannelRawValues(ReadOnlySpan<byte> frame)
    {
        const int channelCount = 32;
        var payloadLength = frame.Length - 8;
        var sampleCount = (payloadLength - 4) / (channelCount * 3);
        var values = new int[sampleCount];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var offset = 6 + 3 + sample * channelCount * 3;
            var raw = frame[offset] << 16 | frame[offset + 1] << 8 | frame[offset + 2];
            if ((raw & 0x800000) != 0)
                raw |= unchecked((int)0xFF000000);
            values[sample] = raw;
        }
        return values;
    }

    private static double PacketEnd(EegRawPacketRecord packet) =>
        packet.TimelineStartSeconds
        + Math.Max(0, packet.SampleCount - 1) / (double)Math.Max(1, packet.SampleRateHz);

    private sealed class InMemoryRawPacketReader(
        IReadOnlyList<EegRawPacketRecord> packets,
        TimeSpan? readDelay = null
    ) : IEegRawPacketReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<IReadOnlyList<EegRecordingSummary>> ListAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IReadOnlyList<EegRecordingSummary>>([]);

        public ValueTask<EegRecordingSummary?> GetSummaryAsync(
            Guid recordingId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<EegRecordingSummary?>(null);

        public async IAsyncEnumerable<EegRawPacketRecord> ReadPacketsAsync(
            Guid recordingId,
            double? timelineStartSeconds = null,
            double? timelineEndSeconds = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            ReadCount++;
            if (readDelay is { } delay)
                await Task.Delay(delay, cancellationToken);
            foreach (var packet in packets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    packet.RecordingId != recordingId
                    || timelineStartSeconds.HasValue
                        && PacketEnd(packet) < timelineStartSeconds.Value
                    || timelineEndSeconds.HasValue
                        && packet.TimelineStartSeconds > timelineEndSeconds.Value
                )
                    continue;
                yield return packet;
            }
        }

        public ValueTask DeleteAsync(
            Guid recordingId,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
