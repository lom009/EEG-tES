using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.ViewModels.Pages;

namespace EGGtCSPlatform.Services;

public sealed record EegHistoryWindowRequest(
    Guid RecordingId,
    double StartTimeSeconds,
    double EndTimeSeconds,
    int SampleRateHz,
    EegDisplayFilterSettings FilterSettings,
    IReadOnlyDictionary<int, string> ChannelNames,
    long DataVersion,
    double AvailableThroughSeconds,
    int MaximumPointsPerChannel = 1200,
    string? RawFilePath = null
);

public sealed record EegHistoryChannelWindow(
    string ChannelId,
    IReadOnlyList<TimedWaveformPoint> Samples
);

public sealed record EegHistoryWindowResult(
    Guid RecordingId,
    double StartTimeSeconds,
    double EndTimeSeconds,
    EegDisplayFilterSettings FilterSettings,
    long DataVersion,
    IReadOnlyList<EegHistoryChannelWindow> Channels
);

public interface IEegHistoryWindowProvider
{
    Task<EegHistoryWindowResult> LoadAsync(
        EegHistoryWindowRequest request,
        CancellationToken cancellationToken = default
    );

    Task<EegHistoryWindowResult?> LoadCachedAsync(
        EegHistoryWindowRequest request,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<EegHistoryWindowResult?>(null);

    void InvalidateRecording(Guid recordingId);
}

public sealed class EegHistoryWindowProvider : IEegHistoryWindowProvider
{
    private const double WarmupSeconds = 60d;
    private const double ChunkSeconds = 10d;
    private const long DefaultMaximumCacheBytes = 128L * 1024L * 1024L;
    private readonly IEegRawPacketReader _reader;
    private readonly long _maximumCacheBytes;
    private readonly object _cacheGate = new();
    private readonly Dictionary<ChunkKey, CacheEntry> _cache = [];
    private long _cacheBytes;
    private long _accessSequence;

    public EegHistoryWindowProvider(
        IEegRawPacketReader reader,
        long maximumCacheBytes = DefaultMaximumCacheBytes
    )
    {
        _reader = reader;
        _maximumCacheBytes =
            maximumCacheBytes > 0
                ? maximumCacheBytes
                : throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
    }

    public async Task<EegHistoryWindowResult> LoadAsync(
        EegHistoryWindowRequest request,
        CancellationToken cancellationToken = default
    )
    {
        Validate(request);
        var warmupStart = Math.Max(0d, request.StartTimeSeconds - WarmupSeconds);
        var firstChunk = (long)Math.Floor(warmupStart / ChunkSeconds);
        var lastChunk = (long)
            Math.Floor(Math.Max(warmupStart, request.EndTimeSeconds - 0.0000001d) / ChunkSeconds);
        var decoded = new Dictionary<long, DecodedPacket>();
        for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkPackets = await GetChunkAsync(request, chunk, cancellationToken)
                .ConfigureAwait(false);
            foreach (var packet in chunkPackets)
                decoded[packet.Sequence] = packet;
        }

        return BuildResult(request, warmupStart, decoded.Values, cancellationToken);
    }

    public Task<EegHistoryWindowResult?> LoadCachedAsync(
        EegHistoryWindowRequest request,
        CancellationToken cancellationToken = default
    )
    {
        Validate(request);
        var warmupStart = Math.Max(0d, request.StartTimeSeconds - WarmupSeconds);
        var firstChunk = (long)Math.Floor(warmupStart / ChunkSeconds);
        var lastChunk = (long)
            Math.Floor(Math.Max(warmupStart, request.EndTimeSeconds - 0.0000001d) / ChunkSeconds);
        var cachedPackets = new List<IReadOnlyList<DecodedPacket>>();
        lock (_cacheGate)
        {
            for (var chunk = firstChunk; chunk <= lastChunk; chunk++)
            {
                var key = new ChunkKey(request.RecordingId, chunk);
                if (
                    !_cache.TryGetValue(key, out var cached)
                    || !(cached.IsSealed || cached.DataVersion == request.DataVersion)
                )
                    return Task.FromResult<EegHistoryWindowResult?>(null);
                cached.LastAccess = ++_accessSequence;
                cachedPackets.Add(cached.Packets);
            }
        }

        return Task.Run<EegHistoryWindowResult?>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return BuildResult(
                    request,
                    warmupStart,
                    cachedPackets.SelectMany(packets => packets),
                    cancellationToken
                );
            },
            cancellationToken
        );
    }

    private static EegHistoryWindowResult BuildResult(
        EegHistoryWindowRequest request,
        double warmupStart,
        IEnumerable<DecodedPacket> decodedPackets,
        CancellationToken cancellationToken
    )
    {
        var decoded = new Dictionary<long, DecodedPacket>();
        foreach (var packet in decodedPackets)
            decoded[packet.Sequence] = packet;

        var orderedPackets = decoded
            .Values.Where(packet =>
                PacketEnd(packet) >= warmupStart
                && packet.StartTimeSeconds <= request.EndTimeSeconds
            )
            .OrderBy(packet => packet.Sequence)
            .ToArray();
        var channels = request
            .ChannelNames.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(channelId =>
                FilterChannel(request, channelId, orderedPackets, cancellationToken)
            )
            .ToArray();
        return new EegHistoryWindowResult(
            request.RecordingId,
            request.StartTimeSeconds,
            request.EndTimeSeconds,
            request.FilterSettings,
            request.DataVersion,
            channels
        );
    }

    public void InvalidateRecording(Guid recordingId)
    {
        lock (_cacheGate)
        {
            foreach (var key in _cache.Keys.Where(key => key.RecordingId == recordingId).ToArray())
            {
                _cacheBytes -= _cache[key].EstimatedBytes;
                _cache.Remove(key);
            }
        }
    }

    private async Task<IReadOnlyList<DecodedPacket>> GetChunkAsync(
        EegHistoryWindowRequest request,
        long chunkIndex,
        CancellationToken cancellationToken
    )
    {
        var key = new ChunkKey(request.RecordingId, chunkIndex);
        var chunkStart = chunkIndex * ChunkSeconds;
        var chunkEnd = chunkStart + ChunkSeconds;
        var isSealed = chunkEnd < request.AvailableThroughSeconds - ChunkSeconds;
        lock (_cacheGate)
        {
            if (
                _cache.TryGetValue(key, out var cached)
                && (cached.IsSealed || cached.DataVersion == request.DataVersion)
            )
            {
                cached.LastAccess = ++_accessSequence;
                return cached.Packets;
            }
        }

        var packets = new List<DecodedPacket>();
        var records = string.IsNullOrWhiteSpace(request.RawFilePath)
            ? _reader.ReadSamplesAsync(request.RecordingId, chunkStart, chunkEnd, cancellationToken)
            : _reader.ReadSamplesFromFileAsync(
                request.RecordingId,
                request.RawFilePath,
                chunkStart,
                chunkEnd,
                cancellationToken
            );
        await foreach (var record in records.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var channelSamples = record
                .Channels.Where(channel =>
                    request.ChannelNames.ContainsKey(channel.PhysicalChannel)
                )
                .ToDictionary(
                    channel => request.ChannelNames[channel.PhysicalChannel],
                    channel => channel.Samples,
                    StringComparer.OrdinalIgnoreCase
                );
            packets.Add(
                new DecodedPacket(
                    record.Sequence,
                    record.TimelineStartSeconds,
                    record.SampleIntervalSeconds,
                    record.CurrentCycle,
                    record.AcquisitionStage,
                    channelSamples
                )
            );
        }

        var result = packets
            .GroupBy(packet => packet.Sequence)
            .Select(group => group.First())
            .OrderBy(packet => packet.Sequence)
            .ToArray();
        StoreChunk(key, result, request.DataVersion, isSealed);
        return result;
    }

    private void StoreChunk(
        ChunkKey key,
        IReadOnlyList<DecodedPacket> packets,
        long dataVersion,
        bool isSealed
    )
    {
        var estimatedBytes = packets.Sum(packet =>
            64L
            + packet.Channels.Sum(channel =>
                48L + channel.Key.Length * 2L + channel.Value.Count * sizeof(double)
            )
        );
        lock (_cacheGate)
        {
            if (_cache.Remove(key, out var previous))
                _cacheBytes -= previous.EstimatedBytes;
            _cache[key] = new CacheEntry(
                packets,
                dataVersion,
                isSealed,
                estimatedBytes,
                ++_accessSequence
            );
            _cacheBytes += estimatedBytes;
            while (_cacheBytes > _maximumCacheBytes && _cache.Count > 1)
            {
                var oldest = _cache.MinBy(item => item.Value.LastAccess);
                _cacheBytes -= oldest.Value.EstimatedBytes;
                _cache.Remove(oldest.Key);
            }
        }
    }

    private static EegHistoryChannelWindow FilterChannel(
        EegHistoryWindowRequest request,
        string channelId,
        IReadOnlyList<DecodedPacket> packets,
        CancellationToken cancellationToken
    )
    {
        var filter = new CausalEegDisplayFilter(request.SampleRateHz, request.FilterSettings);
        var output = new List<TimedWaveformPoint>();
        var lastCycle = int.MinValue;
        string? lastStage = null;
        var lastSampleTime = double.NaN;
        var startsNewVisibleSegment = true;
        foreach (var packet in packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packet.Channels.TryGetValue(channelId, out var samples) || samples.Count == 0)
                continue;
            var discontinuity =
                packet.CurrentCycle != lastCycle
                || !string.Equals(packet.AcquisitionStage, lastStage, StringComparison.Ordinal)
                || double.IsFinite(lastSampleTime)
                    && Math.Abs(
                        packet.StartTimeSeconds - (lastSampleTime + packet.SampleIntervalSeconds)
                    )
                        > packet.SampleIntervalSeconds * 1.5d;
            if (discontinuity)
            {
                filter.Reset();
                startsNewVisibleSegment = true;
            }
            for (var index = 0; index < samples.Count; index++)
            {
                var time = packet.StartTimeSeconds + index * packet.SampleIntervalSeconds;
                var value = filter.Process(samples[index]);
                if (time < request.StartTimeSeconds || time > request.EndTimeSeconds)
                    continue;
                output.Add(new TimedWaveformPoint(time, value, startsNewVisibleSegment));
                startsNewVisibleSegment = false;
            }
            lastCycle = packet.CurrentCycle;
            lastStage = packet.AcquisitionStage;
            lastSampleTime =
                packet.StartTimeSeconds + (samples.Count - 1) * packet.SampleIntervalSeconds;
        }
        return new EegHistoryChannelWindow(
            channelId,
            DownsampleStableEnvelope(output, request.MaximumPointsPerChannel)
        );
    }

    private static IReadOnlyList<TimedWaveformPoint> DownsampleStableEnvelope(
        IReadOnlyList<TimedWaveformPoint> samples,
        int maximumPoints
    )
    {
        if (samples.Count <= maximumPoints)
            return samples;
        var segments = new List<List<TimedWaveformPoint>>();
        foreach (var sample in samples)
        {
            if (segments.Count == 0 || sample.StartsNewSegment)
                segments.Add([]);
            segments[^1].Add(sample);
        }

        var result = new List<TimedWaveformPoint>(maximumPoints + segments.Count * 2);
        var totalSamples = segments.Sum(segment => segment.Count);
        foreach (var segment in segments)
        {
            var segmentBudget = Math.Max(
                2,
                (int)Math.Round(maximumPoints * segment.Count / (double)totalSamples)
            );
            result.AddRange(DownsampleSegment(segment, segmentBudget));
        }
        return result;
    }

    private static IReadOnlyList<TimedWaveformPoint> DownsampleSegment(
        IReadOnlyList<TimedWaveformPoint> samples,
        int maximumPoints
    )
    {
        if (samples.Count <= maximumPoints)
        {
            var copy = samples.ToArray();
            copy[0] = copy[0] with { StartsNewSegment = true };
            return copy;
        }
        var bucketCount = Math.Max(1, maximumPoints / 2);
        var start = samples[0].TimeSeconds;
        var span = Math.Max(0.0000001d, samples[^1].TimeSeconds - start);
        var buckets = new List<TimedWaveformPoint>[bucketCount];
        foreach (var sample in samples)
        {
            var index = Math.Clamp(
                (int)((sample.TimeSeconds - start) / span * bucketCount),
                0,
                bucketCount - 1
            );
            (buckets[index] ??= []).Add(sample);
        }
        var result = new List<TimedWaveformPoint>(maximumPoints + 4);
        foreach (var bucket in buckets)
        {
            if (bucket is null || bucket.Count == 0)
                continue;
            var minimum = bucket.MinBy(point => point.Value);
            var maximum = bucket.MaxBy(point => point.Value);
            if (minimum.TimeSeconds <= maximum.TimeSeconds)
            {
                result.Add(minimum);
                if (maximum.TimeSeconds != minimum.TimeSeconds)
                    result.Add(maximum);
            }
            else
            {
                result.Add(maximum);
                result.Add(minimum);
            }
        }
        if (result.Count > 0)
            result[0] = result[0] with { StartsNewSegment = true };
        return result;
    }

    private static double PacketEnd(DecodedPacket packet)
    {
        var sampleCount = packet
            .Channels.Values.Select(samples => samples.Count)
            .DefaultIfEmpty(0)
            .Max();
        return packet.StartTimeSeconds
            + Math.Max(0, sampleCount - 1) * packet.SampleIntervalSeconds;
    }

    private static void Validate(EegHistoryWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecordingId == Guid.Empty)
            throw new ArgumentException("Recording id cannot be empty.", nameof(request));
        if (request.SampleRateHz is not (250 or 500))
            throw new ArgumentOutOfRangeException(nameof(request.SampleRateHz));
        if (
            !double.IsFinite(request.StartTimeSeconds)
            || !double.IsFinite(request.EndTimeSeconds)
            || request.StartTimeSeconds < 0d
            || request.EndTimeSeconds <= request.StartTimeSeconds
        )
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.MaximumPointsPerChannel < 2)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumPointsPerChannel));
    }

    private readonly record struct ChunkKey(Guid RecordingId, long ChunkIndex);

    private sealed class CacheEntry(
        IReadOnlyList<DecodedPacket> packets,
        long dataVersion,
        bool isSealed,
        long estimatedBytes,
        long lastAccess
    )
    {
        public IReadOnlyList<DecodedPacket> Packets { get; } = packets;
        public long DataVersion { get; } = dataVersion;
        public bool IsSealed { get; } = isSealed;
        public long EstimatedBytes { get; } = estimatedBytes;
        public long LastAccess { get; set; } = lastAccess;
    }

    private sealed record DecodedPacket(
        long Sequence,
        double StartTimeSeconds,
        double SampleIntervalSeconds,
        int CurrentCycle,
        string AcquisitionStage,
        IReadOnlyDictionary<string, IReadOnlyList<double>> Channels
    );
}
