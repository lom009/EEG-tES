using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Persistence;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

namespace EGGtCSPlatform.Services;

public enum EegRecordingCompletionStatus
{
    Completed,
    EmergencyStopped,
    Canceled,
    Failed,
}

public sealed record EegRecordingMetadata(
    Guid RecordingId,
    string ExperimentId,
    string SubjectId,
    string DeviceId,
    int SampleRateHz,
    IReadOnlyList<string> ChannelIds,
    DateTimeOffset StartedAtUtc,
    EegDisplayFilterSettings InitialDisplayFilters,
    IReadOnlyDictionary<int, string>? PhysicalChannelNames = null,
    [property:
        System.Text.Json.Serialization.JsonPropertyName("generationParameters"),
        System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        )
    ]
        SimulationProvenance? Simulation = null
)
{
    // Accept previously saved headers without emitting the legacy field again.
    [System.Text.Json.Serialization.JsonPropertyName("Simulation")]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )]
    public SimulationProvenance? LegacyParameters
    {
        get => null;
        init => Simulation ??= value;
    }
}

public sealed record EegRawPacketRecord(
    Guid RecordingId,
    long Sequence,
    DateTimeOffset ReceivedAtUtc,
    long ReceivedTimestamp,
    double TimelineStartSeconds,
    int SampleRateHz,
    int SampleCount,
    int CurrentCycle,
    string AcquisitionStage,
    ReadOnlyMemory<byte> Datagram
);

public sealed record EegRecordedChannelSamples(int PhysicalChannel, IReadOnlyList<double> Samples);

public sealed record EegRecordedSampleBatch(
    Guid RecordingId,
    long Sequence,
    DateTimeOffset ReceivedAtUtc,
    long ReceivedTimestamp,
    double TimelineStartSeconds,
    int SampleRateHz,
    double SampleIntervalSeconds,
    int CurrentCycle,
    string AcquisitionStage,
    IReadOnlyList<EegRecordedChannelSamples> Channels
)
{
    public int SampleCount =>
        Channels.Select(channel => channel.Samples.Count).DefaultIfEmpty(0).Max();
}

public sealed record EegFilterChangeRecord(
    Guid RecordingId,
    DateTimeOffset ChangedAtUtc,
    double TimelineSeconds,
    EegDisplayFilterSettings Settings
);

public sealed record EegRecordingSummary(
    Guid RecordingId,
    string FilePath,
    bool IsComplete,
    EegRecordingCompletionStatus? CompletionStatus,
    long PacketCount,
    long DatagramBytes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    EegRecordingMetadata Metadata,
    string? EdfPath = null,
    string? CsvPath = null,
    long SampleBatchCount = 0,
    double? DataEndExclusiveSeconds = null
);

public sealed record EegRecordingCompletionResult(
    EegPacketStatistics PacketStatistics,
    double? DataEndExclusiveSeconds = null
);

public sealed class EegRawRecordingFailedEventArgs(Guid recordingId, Exception exception)
    : EventArgs
{
    public Guid RecordingId { get; } = recordingId;
    public Exception Exception { get; } = exception;
}

public interface IEegRawPacketRecorder
{
    event EventHandler<EegRawRecordingFailedEventArgs>? RecordingFailed;

    ValueTask BeginAsync(
        EegRecordingMetadata metadata,
        CancellationToken cancellationToken = default
    );

    ValueTask AppendAsync(EegRawPacketRecord packet, CancellationToken cancellationToken = default);

    ValueTask AppendSamplesAsync(
        EegRecordedSampleBatch batch,
        CancellationToken cancellationToken = default
    );

    ValueTask AppendFilterChangeAsync(
        EegFilterChangeRecord change,
        CancellationToken cancellationToken = default
    );

    ValueTask<EegRecordingSummary> CompleteAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status,
        CancellationToken cancellationToken = default
    );
}

public interface IEegRawPacketReader
{
    IEegPacketDecoder CreatePacketDecoder() => new EggtCsEegPacketDecoder();
    ValueTask<IReadOnlyList<EegRecordingSummary>> ListAsync(
        CancellationToken cancellationToken = default
    );

    ValueTask<EegRecordingSummary?> GetSummaryAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default
    );

    ValueTask<EegRecordingSummary?> GetSummaryFromFileAsync(
        Guid recordingId,
        string filePath,
        CancellationToken cancellationToken = default
    ) => GetSummaryAsync(recordingId, cancellationToken);

    IAsyncEnumerable<EegRawPacketRecord> ReadPacketsAsync(
        Guid recordingId,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        CancellationToken cancellationToken = default
    );

    IAsyncEnumerable<EegRawPacketRecord> ReadPacketsFromFileAsync(
        Guid recordingId,
        string filePath,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        CancellationToken cancellationToken = default
    ) => ReadPacketsAsync(recordingId, timelineStartSeconds, timelineEndSeconds, cancellationToken);

    async IAsyncEnumerable<EegRecordedSampleBatch> ReadSamplesAsync(
        Guid recordingId,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var decoder = new EegRecordedSampleDecoderSession(CreatePacketDecoder());
        await foreach (
            var packet in ReadPacketsAsync(
                    recordingId,
                    timelineStartSeconds,
                    timelineEndSeconds,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (decoder.TryDecode(packet, out var batch))
                yield return batch;
        }
    }

    async IAsyncEnumerable<EegRecordedSampleBatch> ReadSamplesFromFileAsync(
        Guid recordingId,
        string filePath,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var decoder = new EegRecordedSampleDecoderSession(CreatePacketDecoder());
        await foreach (
            var packet in ReadPacketsFromFileAsync(
                    recordingId,
                    filePath,
                    timelineStartSeconds,
                    timelineEndSeconds,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            if (decoder.TryDecode(packet, out var batch))
                yield return batch;
        }
    }

    ValueTask DeleteAsync(Guid recordingId, CancellationToken cancellationToken = default);
}

public interface IEegRawRecordingStore : IEegRawPacketRecorder, IEegRawPacketReader { }

public sealed class FileEegRawPacketStore : IEegRawRecordingStore, IAsyncDisposable
{
    private const int FormatVersion = 2;
    private const int MinimumSupportedFormatVersion = 1;
    private const byte PacketRecordType = 1;
    private const byte FilterChangeRecordType = 2;
    private const byte CompletionRecordType = 3;
    private const byte SampleBatchRecordType = 4;
    private static readonly byte[] Magic = "EEGRAW1\0"u8.ToArray();
    private const int IndexFormatVersion = 2;
    private const int Version1IndexEntrySize =
        sizeof(long) + sizeof(double) + sizeof(double) + sizeof(int) + sizeof(long);
    private const int IndexEntrySize = Version1IndexEntrySize + sizeof(byte);
    private static readonly byte[] IndexMagic = "EEGIDX1\0"u8.ToArray();
    private readonly string _directory;
    private readonly Channel<WriteCommand> _commands;
    private readonly Task _writerTask;
    private readonly IEegArtifactFinalizer? _artifactFinalizer;
    private readonly IEegFileRegistry? _fileRegistry;
    private readonly bool _waitForSampleWrites;
    private readonly Func<IEegPacketDecoder> _packetDecoderFactory;

    public IEegPacketDecoder CreatePacketDecoder() => _packetDecoderFactory();

    private RecordingWriter? _active;
    private Exception? _fatalException;

    public event EventHandler<EegRawRecordingFailedEventArgs>? RecordingFailed;

    public FileEegRawPacketStore(
        string? directory = null,
        int queueCapacity = 4096,
        IEegArtifactFinalizer? artifactFinalizer = null,
        IEegFileRegistry? fileRegistry = null,
        bool waitForSampleWrites = false,
        Func<IEegPacketDecoder>? packetDecoderFactory = null
    )
    {
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        _directory = directory ?? AppPaths.TemporaryEegDirectory;
        _artifactFinalizer = artifactFinalizer;
        _fileRegistry = fileRegistry;
        _waitForSampleWrites = waitForSampleWrites;
        _packetDecoderFactory = packetDecoderFactory ?? (() => new EggtCsEegPacketDecoder());
        Directory.CreateDirectory(_directory);
        _commands = Channel.CreateBounded<WriteCommand>(
            new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            }
        );
        _writerTask = Task.Run(ProcessCommandsAsync);
    }

    public async ValueTask BeginAsync(
        EegRecordingMetadata metadata,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.RecordingId == Guid.Empty)
            throw new ArgumentException("Recording id cannot be empty.", nameof(metadata));
        await EnqueueAsync(new BeginCommand(metadata), waitForWrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(
        EegRawPacketRecord packet,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Datagram.IsEmpty)
            throw new ArgumentException("Raw EEG datagram cannot be empty.", nameof(packet));
        EnqueueDataCommand(new PacketCommand(packet), cancellationToken);
        await ValueTask.CompletedTask;
    }

    public async ValueTask AppendSamplesAsync(
        EegRecordedSampleBatch batch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateSampleBatch(batch);
        if (_waitForSampleWrites)
            await EnqueueAsync(new SampleBatchCommand(batch), waitForWrite: true, cancellationToken)
                .ConfigureAwait(false);
        else
            EnqueueDataCommand(new SampleBatchCommand(batch), cancellationToken);
    }

    public async ValueTask AppendFilterChangeAsync(
        EegFilterChangeRecord change,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(change);
        EnqueueDataCommand(new FilterChangeCommand(change), cancellationToken);
        await ValueTask.CompletedTask;
    }

    public async ValueTask<EegRecordingSummary> CompleteAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status,
        CancellationToken cancellationToken = default
    )
    {
        var command = new CompleteCommand(recordingId, status);
        await EnqueueAsync(command, waitForWrite: true, cancellationToken).ConfigureAwait(false);
        return await command.Summary.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<EegRecordingSummary>> ListAsync(
        CancellationToken cancellationToken = default
    )
    {
        var files = Directory
            .EnumerateFiles(_directory, "*.eegraw")
            .Concat(Directory.EnumerateFiles(_directory, "*.eegraw.tmp"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        var results = new List<EegRecordingSummary>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ReadSummaryAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidDataException)
            {
                // A malformed unrelated file must not hide valid recordings.
            }
        }
        return results;
    }

    public async ValueTask<EegRecordingSummary?> GetSummaryAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default
    )
    {
        var path = FindRecordingPath(recordingId);
        return path is null
            ? null
            : await ReadSummaryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EegRecordingSummary?> GetSummaryFromFileAsync(
        Guid recordingId,
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            return null;
        var summary = await ReadSummaryAsync(Path.GetFullPath(filePath), cancellationToken)
            .ConfigureAwait(false);
        if (summary.RecordingId != recordingId)
            throw new InvalidDataException(
                "EEG raw recording id does not match the experiment run."
            );
        return summary;
    }

    public async IAsyncEnumerable<EegRawPacketRecord> ReadPacketsAsync(
        Guid recordingId,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var path =
            FindRecordingPath(recordingId)
            ?? throw new FileNotFoundException($"EEG recording {recordingId} was not found.");
        await foreach (
            var packet in ReadPacketsFromFileAsync(
                recordingId,
                path,
                timelineStartSeconds,
                timelineEndSeconds,
                cancellationToken
            )
        )
            yield return packet;
    }

    public async IAsyncEnumerable<EegRawPacketRecord> ReadPacketsFromFileAsync(
        Guid recordingId,
        string filePath,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("原始数据文件不存在。", path);
        if (timelineStartSeconds.HasValue || timelineEndSeconds.HasValue)
        {
            var entries = await LoadOrRebuildIndexAsync(recordingId, path, cancellationToken)
                .ConfigureAwait(false);
            await using var indexedStream = OpenRead(path, randomAccess: true);
            using var indexedReader = new BinaryReader(
                indexedStream,
                Encoding.UTF8,
                leaveOpen: true
            );
            var indexedRecordingIsIncomplete = path.EndsWith(
                ".tmp",
                StringComparison.OrdinalIgnoreCase
            );
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.RecordType != PacketRecordType)
                    continue;
                if (
                    timelineStartSeconds.HasValue
                    && entry.TimelineEndSeconds < timelineStartSeconds.Value
                )
                    continue;
                if (
                    timelineEndSeconds.HasValue
                    && entry.TimelineStartSeconds > timelineEndSeconds.Value
                )
                    continue;
                EegRawPacketRecord packet;
                try
                {
                    indexedStream.Seek(entry.FileOffset, SeekOrigin.Begin);
                    if (indexedReader.ReadByte() != PacketRecordType)
                        throw new InvalidDataException(
                            "EEG raw index points to a non-packet record."
                        );
                    packet = ReadPacket(indexedReader, recordingId);
                    if (packet.Sequence != entry.Sequence)
                        throw new InvalidDataException(
                            "EEG raw index sequence does not match the recording."
                        );
                }
                catch (Exception exception)
                    when (indexedRecordingIsIncomplete
                        && exception is EndOfStreamException or InvalidDataException
                    )
                {
                    break;
                }
                yield return packet;
                await Task.Yield();
            }
            yield break;
        }
        await using var stream = OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var metadata = ReadHeader(reader);
        var isIncomplete = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EegRawPacketRecord? packet = null;
            try
            {
                var type = reader.ReadByte();
                if (type == PacketRecordType)
                    packet = ReadPacket(reader, metadata.RecordingId);
                else if (type == FilterChangeRecordType)
                    SkipFilterChange(reader);
                else if (type == CompletionRecordType)
                    SkipCompletion(reader);
                else if (type == SampleBatchRecordType)
                    SkipSampleBatch(reader);
                else
                    throw new InvalidDataException($"Unknown EEG raw record type {type}.");
            }
            catch (Exception exception)
                when (isIncomplete && exception is EndOfStreamException or InvalidDataException)
            {
                break;
            }
            if (packet is not null)
            {
                var packetEnd =
                    packet.TimelineStartSeconds
                    + Math.Max(0, packet.SampleCount - 1)
                        / (double)Math.Max(1, packet.SampleRateHz);
                if (
                    (!timelineStartSeconds.HasValue || packetEnd >= timelineStartSeconds.Value)
                    && (
                        !timelineEndSeconds.HasValue
                        || packet.TimelineStartSeconds <= timelineEndSeconds.Value
                    )
                )
                    yield return packet;
            }
            await Task.Yield();
        }
    }

    public ValueTask DeleteAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_active?.Metadata.RecordingId == recordingId)
            throw new InvalidOperationException("Cannot delete an active EEG recording.");
        var completed = GetCompletedPath(recordingId);
        var temporary = GetTemporaryPath(recordingId);
        var completedIndex = GetCompletedIndexPath(recordingId);
        var temporaryIndex = GetTemporaryIndexPath(recordingId);
        if (File.Exists(completed))
            File.Delete(completed);
        if (File.Exists(temporary))
            File.Delete(temporary);
        if (File.Exists(completedIndex))
            File.Delete(completedIndex);
        if (File.Exists(temporaryIndex))
            File.Delete(temporaryIndex);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        if (_active is not null)
        {
            await _active.Stream.FlushAsync().ConfigureAwait(false);
            await _active.IndexStream.FlushAsync().ConfigureAwait(false);
            await _active.Stream.DisposeAsync().ConfigureAwait(false);
            await _active.IndexStream.DisposeAsync().ConfigureAwait(false);
            _active = null;
        }
    }

    private async ValueTask EnqueueAsync(
        WriteCommand command,
        bool waitForWrite,
        CancellationToken cancellationToken
    )
    {
        if (_fatalException is { } failure)
            throw new IOException("The EEG raw recording writer has failed.", failure);
        await _commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        if (waitForWrite)
            await command.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnqueueDataCommand(WriteCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_fatalException is { } failure)
            throw new IOException("The EEG raw recording writer has failed.", failure);
        if (!_commands.Writer.TryWrite(command))
            throw new EegRawRecordingQueueOverflowException();
        _ = command.Completion.Task.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private async Task ProcessCommandsAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (command)
                {
                    case BeginCommand begin:
                        await BeginCoreAsync(begin.Metadata).ConfigureAwait(false);
                        break;
                    case PacketCommand packet:
                        await AppendCoreAsync(packet.Packet).ConfigureAwait(false);
                        break;
                    case SampleBatchCommand samples:
                        await AppendSampleBatchCoreAsync(samples.Batch).ConfigureAwait(false);
                        break;
                    case FilterChangeCommand filter:
                        await AppendFilterChangeCoreAsync(filter.Change).ConfigureAwait(false);
                        break;
                    case CompleteCommand complete:
                        complete.Summary.TrySetResult(
                            await CompleteCoreAsync(complete.RecordingId, complete.Status)
                                .ConfigureAwait(false)
                        );
                        break;
                }
                command.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                _fatalException ??= exception;
                command.Completion.TrySetException(exception);
                if (command is CompleteCommand complete)
                    complete.Summary.TrySetException(exception);
                RecordingFailed?.Invoke(
                    this,
                    new EegRawRecordingFailedEventArgs(
                        _active?.Metadata.RecordingId ?? GetCommandRecordingId(command),
                        exception
                    )
                );
            }
        }
    }

    private static Guid GetCommandRecordingId(WriteCommand command) =>
        command switch
        {
            BeginCommand begin => begin.Metadata.RecordingId,
            PacketCommand packet => packet.Packet.RecordingId,
            SampleBatchCommand samples => samples.Batch.RecordingId,
            FilterChangeCommand filter => filter.Change.RecordingId,
            CompleteCommand complete => complete.RecordingId,
            _ => Guid.Empty,
        };

    private async Task BeginCoreAsync(EegRecordingMetadata metadata)
    {
        if (_active is not null)
            throw new InvalidOperationException("An EEG recording is already active.");
        var path = GetTemporaryPath(metadata.RecordingId);
        var indexPath = GetTemporaryIndexPath(metadata.RecordingId);
        if (
            File.Exists(path)
            || File.Exists(GetCompletedPath(metadata.RecordingId))
            || File.Exists(indexPath)
            || File.Exists(GetCompletedIndexPath(metadata.RecordingId))
        )
            throw new IOException($"EEG recording {metadata.RecordingId} already exists.");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var indexStream = new FileStream(
            indexPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(FormatVersion);
            var json = JsonSerializer.SerializeToUtf8Bytes(metadata);
            writer.Write(json.Length);
            writer.Write(json);
            using var indexWriter = new BinaryWriter(indexStream, Encoding.UTF8, leaveOpen: true);
            WriteIndexHeader(indexWriter, metadata.RecordingId);
            await stream.FlushAsync().ConfigureAwait(false);
            await indexStream.FlushAsync().ConfigureAwait(false);
            _active = new RecordingWriter(metadata, path, stream, indexPath, indexStream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            await indexStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AppendCoreAsync(EegRawPacketRecord packet)
    {
        var active = RequireActive(packet.RecordingId);
        var fileOffset = active.Stream.Position;
        using var writer = new BinaryWriter(active.Stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(PacketRecordType);
        writer.Write(packet.Sequence);
        writer.Write(packet.ReceivedAtUtc.UtcTicks);
        writer.Write(packet.ReceivedTimestamp);
        writer.Write(packet.TimelineStartSeconds);
        writer.Write(packet.SampleRateHz);
        writer.Write(packet.SampleCount);
        writer.Write(packet.CurrentCycle);
        writer.Write(packet.AcquisitionStage ?? string.Empty);
        writer.Write(packet.Datagram.Length);
        writer.Write(packet.Datagram.Span);
        WriteIndexEntry(
            active.IndexStream,
            new DataRecordIndexEntry(
                PacketRecordType,
                packet.Sequence,
                packet.TimelineStartSeconds,
                packet.TimelineStartSeconds
                    + Math.Max(0, packet.SampleCount - 1)
                        / (double)Math.Max(1, packet.SampleRateHz),
                packet.CurrentCycle,
                fileOffset
            )
        );
        active.PacketCount++;
        active.DataEndExclusiveSeconds = ExtendDataEnd(
            active.DataEndExclusiveSeconds,
            packet.TimelineStartSeconds,
            packet.SampleCount,
            1d / Math.Max(1, packet.SampleRateHz)
        );
        active.DatagramBytes += packet.Datagram.Length;
        if ((active.PacketCount + active.SampleBatchCount) % 64 == 0)
        {
            await active.Stream.FlushAsync().ConfigureAwait(false);
            await active.IndexStream.FlushAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<EegRecordedSampleBatch> ReadSamplesAsync(
        Guid recordingId,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var path =
            FindRecordingPath(recordingId)
            ?? throw new FileNotFoundException($"EEG recording {recordingId} was not found.");
        await foreach (
            var batch in ReadSamplesFromFileAsync(
                recordingId,
                path,
                timelineStartSeconds,
                timelineEndSeconds,
                cancellationToken
            )
        )
            yield return batch;
    }

    public async IAsyncEnumerable<EegRecordedSampleBatch> ReadSamplesFromFileAsync(
        Guid recordingId,
        string filePath,
        double? timelineStartSeconds = null,
        double? timelineEndSeconds = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("原始数据文件不存在。", path);
        var entries = await LoadOrRebuildIndexAsync(recordingId, path, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = OpenRead(path, randomAccess: true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var decoder = new EegRecordedSampleDecoderSession(CreatePacketDecoder());
        var isIncomplete = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                timelineStartSeconds.HasValue
                && entry.TimelineEndSeconds < timelineStartSeconds.Value
            )
                continue;
            if (
                timelineEndSeconds.HasValue
                && entry.TimelineStartSeconds > timelineEndSeconds.Value
            )
                continue;
            EegRecordedSampleBatch? batch;
            try
            {
                stream.Seek(entry.FileOffset, SeekOrigin.Begin);
                var type = reader.ReadByte();
                if (type != entry.RecordType)
                    throw new InvalidDataException(
                        "EEG raw index record type does not match the recording."
                    );
                if (type == PacketRecordType)
                {
                    var packet = ReadPacket(reader, recordingId);
                    batch = decoder.TryDecode(packet, out var decoded) ? decoded : null;
                }
                else if (type == SampleBatchRecordType)
                    batch = ReadSampleBatch(reader, recordingId);
                else
                    throw new InvalidDataException("EEG raw index points to a non-data record.");
                if (batch is not null && batch.Sequence != entry.Sequence)
                    throw new InvalidDataException(
                        "EEG raw index sequence does not match the recording."
                    );
            }
            catch (Exception exception)
                when (isIncomplete && exception is EndOfStreamException or InvalidDataException)
            {
                break;
            }
            if (batch is not null)
                yield return batch;
            await Task.Yield();
        }
    }

    private async Task AppendSampleBatchCoreAsync(EegRecordedSampleBatch batch)
    {
        var active = RequireActive(batch.RecordingId);
        var fileOffset = active.Stream.Position;
        using var writer = new BinaryWriter(active.Stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(SampleBatchRecordType);
        writer.Write(batch.Sequence);
        writer.Write(batch.ReceivedAtUtc.UtcTicks);
        writer.Write(batch.ReceivedTimestamp);
        writer.Write(batch.TimelineStartSeconds);
        writer.Write(batch.SampleRateHz);
        writer.Write(batch.SampleIntervalSeconds);
        writer.Write(batch.CurrentCycle);
        writer.Write(batch.AcquisitionStage ?? string.Empty);
        writer.Write(batch.Channels.Count);
        foreach (var channel in batch.Channels)
        {
            writer.Write(channel.PhysicalChannel);
            writer.Write(channel.Samples.Count);
            foreach (var sample in channel.Samples)
                writer.Write(sample);
        }
        WriteIndexEntry(
            active.IndexStream,
            new DataRecordIndexEntry(
                SampleBatchRecordType,
                batch.Sequence,
                batch.TimelineStartSeconds,
                GetSampleBatchEnd(batch),
                batch.CurrentCycle,
                fileOffset
            )
        );
        active.SampleBatchCount++;
        active.DataEndExclusiveSeconds = ExtendDataEnd(
            active.DataEndExclusiveSeconds,
            batch.TimelineStartSeconds,
            batch.SampleCount,
            batch.SampleIntervalSeconds
        );
        if ((active.PacketCount + active.SampleBatchCount) % 64 == 0)
        {
            await active.Stream.FlushAsync().ConfigureAwait(false);
            await active.IndexStream.FlushAsync().ConfigureAwait(false);
        }
    }

    private Task AppendFilterChangeCoreAsync(EegFilterChangeRecord change)
    {
        var active = RequireActive(change.RecordingId);
        using var writer = new BinaryWriter(active.Stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(FilterChangeRecordType);
        writer.Write(change.ChangedAtUtc.UtcTicks);
        writer.Write(change.TimelineSeconds);
        WriteNullableDouble(writer, change.Settings.HighPassHz);
        WriteNullableDouble(writer, change.Settings.NotchHz);
        WriteNullableDouble(writer, change.Settings.LowPassHz);
        return Task.CompletedTask;
    }

    private async Task<EegRecordingSummary> CompleteCoreAsync(
        Guid recordingId,
        EegRecordingCompletionStatus status
    )
    {
        var active = RequireActive(recordingId);
        var completedAtUtc = DateTimeOffset.UtcNow;
        using (var writer = new BinaryWriter(active.Stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(CompletionRecordType);
            writer.Write((int)status);
            writer.Write(completedAtUtc.UtcTicks);
        }
        await active.Stream.FlushAsync().ConfigureAwait(false);
        await active.IndexStream.FlushAsync().ConfigureAwait(false);
        await active.Stream.DisposeAsync().ConfigureAwait(false);
        await active.IndexStream.DisposeAsync().ConfigureAwait(false);
        var completedPath = GetCompletedPath(recordingId);
        var completedIndexPath = GetCompletedIndexPath(recordingId);
        File.Move(active.TemporaryPath, completedPath);
        File.Move(active.TemporaryIndexPath, completedIndexPath);
        _active = null;
        var summary = new EegRecordingSummary(
            recordingId,
            completedPath,
            true,
            status,
            active.PacketCount,
            active.DatagramBytes,
            active.Metadata.StartedAtUtc,
            completedAtUtc,
            active.Metadata,
            SampleBatchCount: active.SampleBatchCount,
            DataEndExclusiveSeconds: active.DataEndExclusiveSeconds
        );
        if (_fileRegistry is not null)
            await _fileRegistry.RegisterRawAsync(recordingId, completedPath);
        if (_artifactFinalizer is null)
            return summary;
        try
        {
            var result = await _artifactFinalizer.FinalizeAsync(
                summary,
                token => ReadSamplesAsync(recordingId, cancellationToken: token)
            );
            if (_fileRegistry is not null)
                await _fileRegistry.RegisterTemporaryAsync(recordingId, result);
            return summary with { EdfPath = result.EdfPath, CsvPath = result.CsvPath };
        }
        catch (Exception exception)
        {
            if (_fileRegistry is not null)
                await _fileRegistry.RegisterFailureAsync(recordingId, exception.Message);
            throw;
        }
    }

    private RecordingWriter RequireActive(Guid recordingId)
    {
        if (_active is null || _active.Metadata.RecordingId != recordingId)
            throw new InvalidOperationException($"EEG recording {recordingId} is not active.");
        return _active;
    }

    private async Task<EegRecordingSummary> ReadSummaryAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var metadata = ReadHeader(reader);
        long packetCount = 0;
        long datagramBytes = 0;
        long sampleBatchCount = 0;
        double? dataEnd = null;
        EegRecordingCompletionStatus? status = null;
        DateTimeOffset? completedAtUtc = null;
        var isIncomplete = path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var type = reader.ReadByte();
                if (type == PacketRecordType)
                {
                    reader.ReadInt64();
                    reader.ReadInt64();
                    reader.ReadInt64();
                    var start = reader.ReadDouble();
                    var rate = reader.ReadInt32();
                    var count = reader.ReadInt32();
                    reader.ReadInt32();
                    reader.ReadString();
                    var length = reader.ReadInt32();
                    ValidateDatagramLength(length, stream);
                    stream.Seek(length, SeekOrigin.Current);
                    packetCount++;
                    datagramBytes += length;
                    dataEnd = ExtendDataEnd(dataEnd, start, count, 1d / Math.Max(1, rate));
                }
                else if (type == FilterChangeRecordType)
                    SkipFilterChange(reader);
                else if (type == SampleBatchRecordType)
                {
                    var end = SkipSampleBatch(reader);
                    dataEnd = Math.Max(dataEnd ?? 0d, end);
                    sampleBatchCount++;
                }
                else if (type == CompletionRecordType)
                {
                    status = (EegRecordingCompletionStatus)reader.ReadInt32();
                    completedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
                }
                else
                    throw new InvalidDataException($"Unknown EEG raw record type {type}.");
            }
            catch (Exception exception)
                when (isIncomplete && exception is EndOfStreamException or InvalidDataException)
            {
                break;
            }
        }
        return new EegRecordingSummary(
            metadata.RecordingId,
            path,
            status.HasValue && path.EndsWith(".eegraw", StringComparison.OrdinalIgnoreCase),
            status,
            packetCount,
            datagramBytes,
            metadata.StartedAtUtc,
            completedAtUtc,
            metadata,
            SampleBatchCount: sampleBatchCount,
            DataEndExclusiveSeconds: dataEnd
        );
    }

    private static FileStream OpenRead(string path, bool randomAccess = false) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous
                | (randomAccess ? FileOptions.RandomAccess : FileOptions.SequentialScan)
        );

    private static EegRecordingMetadata ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not an EGGtCS EEG raw recording.");
        var version = reader.ReadInt32();
        if (version is < MinimumSupportedFormatVersion or > FormatVersion)
            throw new InvalidDataException($"Unsupported EEG raw format version {version}.");
        var jsonLength = reader.ReadInt32();
        if (jsonLength <= 0 || jsonLength > 1024 * 1024)
            throw new InvalidDataException("Invalid EEG raw metadata length.");
        var bytes = reader.ReadBytes(jsonLength);
        if (bytes.Length != jsonLength)
            throw new EndOfStreamException();
        return JsonSerializer.Deserialize<EegRecordingMetadata>(bytes)
            ?? throw new InvalidDataException("EEG raw metadata is missing.");
    }

    private static EegRawPacketRecord ReadPacket(BinaryReader reader, Guid recordingId)
    {
        var sequence = reader.ReadInt64();
        var receivedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var receivedTimestamp = reader.ReadInt64();
        var timelineStart = reader.ReadDouble();
        var sampleRate = reader.ReadInt32();
        var sampleCount = reader.ReadInt32();
        var currentCycle = reader.ReadInt32();
        var stage = reader.ReadString();
        var length = reader.ReadInt32();
        ValidateDatagramLength(length, reader.BaseStream);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return new EegRawPacketRecord(
            recordingId,
            sequence,
            receivedAtUtc,
            receivedTimestamp,
            timelineStart,
            sampleRate,
            sampleCount,
            currentCycle,
            stage,
            bytes
        );
    }

    private static EegRecordedSampleBatch ReadSampleBatch(BinaryReader reader, Guid recordingId)
    {
        var sequence = reader.ReadInt64();
        var receivedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var receivedTimestamp = reader.ReadInt64();
        var timelineStart = reader.ReadDouble();
        var sampleRate = reader.ReadInt32();
        var sampleInterval = reader.ReadDouble();
        var currentCycle = reader.ReadInt32();
        var stage = reader.ReadString();
        var channelCount = reader.ReadInt32();
        if (channelCount is < 1 or > 32)
            throw new InvalidDataException("Invalid recorded EEG channel count.");
        var channels = new EegRecordedChannelSamples[channelCount];
        var physicalChannels = new HashSet<int>();
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            var physicalChannel = reader.ReadInt32();
            var sampleCount = reader.ReadInt32();
            if (physicalChannel is < 1 or > 32 || !physicalChannels.Add(physicalChannel))
                throw new InvalidDataException(
                    "Invalid or duplicate recorded EEG physical channel."
                );
            if (
                sampleCount is < 1 or > 10_000_000
                || sampleCount
                    > (reader.BaseStream.Length - reader.BaseStream.Position) / sizeof(double)
            )
                throw new InvalidDataException("Invalid recorded EEG sample count.");
            var samples = new double[sampleCount];
            for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                samples[sampleIndex] = reader.ReadDouble();
            channels[channelIndex] = new EegRecordedChannelSamples(physicalChannel, samples);
        }
        var batch = new EegRecordedSampleBatch(
            recordingId,
            sequence,
            receivedAtUtc,
            receivedTimestamp,
            timelineStart,
            sampleRate,
            sampleInterval,
            currentCycle,
            stage,
            channels
        );
        ValidateSampleBatch(batch);
        return batch;
    }

    private static double SkipSampleBatch(BinaryReader reader)
    {
        reader.ReadInt64();
        reader.ReadInt64();
        reader.ReadInt64();
        var start = reader.ReadDouble();
        reader.ReadInt32();
        var interval = reader.ReadDouble();
        reader.ReadInt32();
        reader.ReadString();
        var channelCount = reader.ReadInt32();
        if (channelCount is < 1 or > 32)
            throw new InvalidDataException("Invalid recorded EEG channel count.");
        var maximumCount = 0;
        for (var channelIndex = 0; channelIndex < channelCount; channelIndex++)
        {
            reader.ReadInt32();
            var sampleCount = reader.ReadInt32();
            var byteCount = checked((long)sampleCount * sizeof(double));
            if (
                sampleCount < 1
                || byteCount > reader.BaseStream.Length - reader.BaseStream.Position
            )
                throw new InvalidDataException("Invalid recorded EEG sample count.");
            reader.BaseStream.Seek(byteCount, SeekOrigin.Current);
            maximumCount = Math.Max(maximumCount, sampleCount);
        }
        return ExtendDataEnd(null, start, maximumCount, interval) ?? 0d;
    }

    private static double? ExtendDataEnd(double? current, double start, int count, double interval)
    {
        var end = start + count * interval;
        return count > 0 && start >= 0d && interval > 0d && double.IsFinite(end)
            ? Math.Max(current ?? 0d, end)
            : current;
    }

    private static void ValidateSampleBatch(EegRecordedSampleBatch batch)
    {
        if (
            batch.RecordingId == Guid.Empty
            || !double.IsFinite(batch.TimelineStartSeconds)
            || batch.TimelineStartSeconds < 0d
            || batch.SampleRateHz <= 0
            || !double.IsFinite(batch.SampleIntervalSeconds)
            || batch.SampleIntervalSeconds <= 0d
            || batch.CurrentCycle <= 0
            || batch.Channels.Count is < 1 or > 32
        )
            throw new ArgumentException("Recorded EEG sample batch is invalid.", nameof(batch));
        var sampleCount = batch.Channels[0].Samples.Count;
        if (
            sampleCount <= 0
            || batch.Channels.Any(channel =>
                channel.PhysicalChannel is < 1 or > 32
                || channel.Samples.Count != sampleCount
                || channel.Samples.Any(sample => !double.IsFinite(sample))
            )
            || batch.Channels.Select(channel => channel.PhysicalChannel).Distinct().Count()
                != batch.Channels.Count
        )
            throw new ArgumentException(
                "Recorded EEG channels must be unique and contain equal finite sample counts.",
                nameof(batch)
            );
    }

    private static double GetSampleBatchEnd(EegRecordedSampleBatch batch) =>
        batch.TimelineStartSeconds
        + Math.Max(0, batch.SampleCount - 1) * batch.SampleIntervalSeconds;

    private async Task<IReadOnlyList<DataRecordIndexEntry>> LoadOrRebuildIndexAsync(
        Guid recordingId,
        string recordingPath,
        CancellationToken cancellationToken
    )
    {
        var indexPath = GetIndexPathForRecording(recordingPath);
        if (!File.Exists(indexPath))
            indexPath = null;
        if (indexPath is not null)
        {
            try
            {
                return await ReadIndexAsync(indexPath, recordingId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is EndOfStreamException or InvalidDataException)
            {
                // Fall through and reconstruct from the authoritative raw recording.
            }
        }

        var isIncomplete = recordingPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        var rebuilt = await BuildIndexAsync(
                recordingPath,
                recordingId,
                isIncomplete,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!isIncomplete)
            await PersistRebuiltIndexAsync(
                    recordingId,
                    GetIndexPathForRecording(recordingPath),
                    rebuilt,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return rebuilt;
    }

    private static async Task<IReadOnlyList<DataRecordIndexEntry>> ReadIndexAsync(
        string indexPath,
        Guid recordingId,
        CancellationToken cancellationToken
    )
    {
        await using var stream = OpenRead(indexPath, randomAccess: true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (version, entrySize) = ReadIndexHeader(reader, recordingId);
        var entries = new List<DataRecordIndexEntry>();
        var isIncomplete = indexPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length - stream.Position < entrySize)
            {
                if (isIncomplete)
                    break;
                throw new InvalidDataException("EEG raw index has an incomplete tail entry.");
            }
            var entry = new DataRecordIndexEntry(
                version == 1 ? PacketRecordType : reader.ReadByte(),
                reader.ReadInt64(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadInt32(),
                reader.ReadInt64()
            );
            ValidateIndexEntry(entry);
            entries.Add(entry);
            if (entries.Count % 1024 == 0)
                await Task.Yield();
        }
        return entries;
    }

    private static async Task<IReadOnlyList<DataRecordIndexEntry>> BuildIndexAsync(
        string recordingPath,
        Guid recordingId,
        bool tolerateIncompleteTail,
        CancellationToken cancellationToken
    )
    {
        await using var stream = OpenRead(recordingPath, randomAccess: true);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var metadata = ReadHeader(reader);
        if (metadata.RecordingId != recordingId)
            throw new InvalidDataException("EEG raw recording id does not match its filename.");
        var entries = new List<DataRecordIndexEntry>();
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileOffset = stream.Position;
                var type = reader.ReadByte();
                if (type == PacketRecordType)
                {
                    var packet = ReadPacket(reader, recordingId);
                    entries.Add(
                        new DataRecordIndexEntry(
                            PacketRecordType,
                            packet.Sequence,
                            packet.TimelineStartSeconds,
                            packet.TimelineStartSeconds
                                + Math.Max(0, packet.SampleCount - 1)
                                    / (double)Math.Max(1, packet.SampleRateHz),
                            packet.CurrentCycle,
                            fileOffset
                        )
                    );
                }
                else if (type == SampleBatchRecordType)
                {
                    var batch = ReadSampleBatch(reader, recordingId);
                    entries.Add(
                        new DataRecordIndexEntry(
                            SampleBatchRecordType,
                            batch.Sequence,
                            batch.TimelineStartSeconds,
                            GetSampleBatchEnd(batch),
                            batch.CurrentCycle,
                            fileOffset
                        )
                    );
                }
                else if (type == FilterChangeRecordType)
                    SkipFilterChange(reader);
                else if (type == CompletionRecordType)
                    SkipCompletion(reader);
                else
                    throw new InvalidDataException($"Unknown EEG raw record type {type}.");
            }
            catch (Exception exception)
                when (tolerateIncompleteTail
                    && exception is EndOfStreamException or InvalidDataException
                )
            {
                break;
            }
            if (entries.Count % 1024 == 0)
                await Task.Yield();
        }
        return entries;
    }

    private async Task PersistRebuiltIndexAsync(
        Guid recordingId,
        string destination,
        IReadOnlyList<DataRecordIndexEntry> entries,
        CancellationToken cancellationToken
    )
    {
        var temporary = destination + ".rebuild.tmp";
        await using (
            var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            )
        )
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteIndexHeader(writer, recordingId);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteIndexEntry(stream, entry);
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private static string GetIndexPathForRecording(string recordingPath) =>
        recordingPath.EndsWith(".eegraw.tmp", StringComparison.OrdinalIgnoreCase)
            ? recordingPath[..^4] + ".idx.tmp"
            : recordingPath + ".idx";

    private static void WriteIndexHeader(BinaryWriter writer, Guid recordingId)
    {
        writer.Write(IndexMagic);
        writer.Write(IndexFormatVersion);
        writer.Write(recordingId.ToByteArray());
        writer.Write(IndexEntrySize);
    }

    private static (int Version, int EntrySize) ReadIndexHeader(
        BinaryReader reader,
        Guid recordingId
    )
    {
        var magic = reader.ReadBytes(IndexMagic.Length);
        if (!magic.AsSpan().SequenceEqual(IndexMagic))
            throw new InvalidDataException("Not an EGGtCS EEG raw index.");
        var version = reader.ReadInt32();
        if (version is < 1 or > IndexFormatVersion)
            throw new InvalidDataException($"Unsupported EEG raw index version {version}.");
        var idBytes = reader.ReadBytes(16);
        if (idBytes.Length != 16 || new Guid(idBytes) != recordingId)
            throw new InvalidDataException("EEG raw index recording id does not match.");
        var entrySize = reader.ReadInt32();
        var expectedEntrySize = version == 1 ? Version1IndexEntrySize : IndexEntrySize;
        if (entrySize != expectedEntrySize)
            throw new InvalidDataException("Unsupported EEG raw index entry size.");
        return (version, entrySize);
    }

    private static void WriteIndexEntry(Stream stream, DataRecordIndexEntry entry)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(entry.RecordType);
        writer.Write(entry.Sequence);
        writer.Write(entry.TimelineStartSeconds);
        writer.Write(entry.TimelineEndSeconds);
        writer.Write(entry.CurrentCycle);
        writer.Write(entry.FileOffset);
    }

    private static void ValidateIndexEntry(DataRecordIndexEntry entry)
    {
        if (
            entry.RecordType is not (PacketRecordType or SampleBatchRecordType)
            || !double.IsFinite(entry.TimelineStartSeconds)
            || !double.IsFinite(entry.TimelineEndSeconds)
            || entry.TimelineEndSeconds < entry.TimelineStartSeconds
            || entry.FileOffset < 0
        )
            throw new InvalidDataException("EEG raw index contains an invalid entry.");
    }

    private static void ValidateDatagramLength(int length, Stream stream)
    {
        if (length <= 0 || length > 16 * 1024 * 1024 || length > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid EEG datagram length.");
    }

    private static void SkipFilterChange(BinaryReader reader)
    {
        reader.ReadInt64();
        reader.ReadDouble();
        ReadNullableDouble(reader);
        ReadNullableDouble(reader);
        ReadNullableDouble(reader);
    }

    private static void SkipCompletion(BinaryReader reader)
    {
        reader.ReadInt32();
        reader.ReadInt64();
    }

    private static void WriteNullableDouble(BinaryWriter writer, double? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
            writer.Write(value.Value);
    }

    private static double? ReadNullableDouble(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadDouble() : null;

    private string? FindRecordingPath(Guid recordingId)
    {
        var completed = GetCompletedPath(recordingId);
        if (File.Exists(completed))
            return completed;
        var temporary = GetTemporaryPath(recordingId);
        return File.Exists(temporary) ? temporary : null;
    }

    private string? FindIndexPath(Guid recordingId)
    {
        var completed = GetCompletedIndexPath(recordingId);
        if (File.Exists(completed))
            return completed;
        var temporary = GetTemporaryIndexPath(recordingId);
        return File.Exists(temporary) ? temporary : null;
    }

    private string GetTemporaryPath(Guid recordingId) =>
        Path.Combine(_directory, $"{recordingId:N}.eegraw.tmp");

    private string GetCompletedPath(Guid recordingId) =>
        Path.Combine(_directory, $"{recordingId:N}.eegraw");

    private string GetTemporaryIndexPath(Guid recordingId) =>
        Path.Combine(_directory, $"{recordingId:N}.eegraw.idx.tmp");

    private string GetCompletedIndexPath(Guid recordingId) =>
        Path.Combine(_directory, $"{recordingId:N}.eegraw.idx");

    private abstract class WriteCommand
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BeginCommand(EegRecordingMetadata metadata) : WriteCommand
    {
        public EegRecordingMetadata Metadata { get; } = metadata;
    }

    private sealed class PacketCommand(EegRawPacketRecord packet) : WriteCommand
    {
        public EegRawPacketRecord Packet { get; } = packet;
    }

    private sealed class SampleBatchCommand(EegRecordedSampleBatch batch) : WriteCommand
    {
        public EegRecordedSampleBatch Batch { get; } = batch;
    }

    private sealed class FilterChangeCommand(EegFilterChangeRecord change) : WriteCommand
    {
        public EegFilterChangeRecord Change { get; } = change;
    }

    private sealed class CompleteCommand(Guid recordingId, EegRecordingCompletionStatus status)
        : WriteCommand
    {
        public Guid RecordingId { get; } = recordingId;
        public EegRecordingCompletionStatus Status { get; } = status;
        public TaskCompletionSource<EegRecordingSummary> Summary { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingWriter(
        EegRecordingMetadata metadata,
        string temporaryPath,
        FileStream stream,
        string temporaryIndexPath,
        FileStream indexStream
    )
    {
        public EegRecordingMetadata Metadata { get; } = metadata;
        public string TemporaryPath { get; } = temporaryPath;
        public FileStream Stream { get; } = stream;
        public string TemporaryIndexPath { get; } = temporaryIndexPath;
        public FileStream IndexStream { get; } = indexStream;
        public long PacketCount { get; set; }
        public long DatagramBytes { get; set; }
        public long SampleBatchCount { get; set; }
        public double? DataEndExclusiveSeconds { get; set; }
    }

    private readonly record struct DataRecordIndexEntry(
        byte RecordType,
        long Sequence,
        double TimelineStartSeconds,
        double TimelineEndSeconds,
        int CurrentCycle,
        long FileOffset
    );
}

internal static class EegRecordedSampleDecoder
{
    public static EegRecordedSampleDecoderSession CreateSession() => new();

    public static bool TryDecode(EegRawPacketRecord packet, out EegRecordedSampleBatch batch) =>
        CreateSession().TryDecode(packet, out batch);
}

internal sealed class EegRecordedSampleDecoderSession(IEegPacketDecoder? decoder = null)
{
    private readonly IEegPacketDecoder _decoder = decoder ?? new EggtCsEegPacketDecoder();

    public bool TryDecode(EegRawPacketRecord packet, out EegRecordedSampleBatch batch)
    {
        var packets = _decoder.Decode(packet.Datagram);
        if (packets.Count == 0)
        {
            batch = null!;
            return false;
        }
        var channels = packets
            .SelectMany(item => item.Channels)
            .GroupBy(channel => channel.PhysicalChannel)
            .Select(group => new EegRecordedChannelSamples(
                group.Key,
                group.SelectMany(channel => channel.SamplesMicrovolts).ToArray()
            ))
            .ToArray();
        batch = new EegRecordedSampleBatch(
            packet.RecordingId,
            packet.Sequence,
            packet.ReceivedAtUtc,
            packet.ReceivedTimestamp,
            packet.TimelineStartSeconds,
            packet.SampleRateHz,
            1d / Math.Max(1, packet.SampleRateHz),
            packet.CurrentCycle,
            packet.AcquisitionStage,
            channels
        );
        return true;
    }
}

public sealed class EegRawRecordingQueueOverflowException()
    : IOException("原始 EEG 数据记录队列已满，本地存储速度低于采集速度。可恢复的临时记录已保留。");
