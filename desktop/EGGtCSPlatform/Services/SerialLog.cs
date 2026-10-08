using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Crash;
using Microsoft.Extensions.Options;

namespace EGGtCSPlatform.Services;

public sealed class SerialLog
    : IByteTrafficLogger,
        IApplicationLogger,
        IGlobalExceptionLogger,
        IAsyncDisposable
{
    private SerialLogOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly DatedLogWriter _writer;
    private readonly Channel<Pending> _operations = CreateQueue();
    private readonly Channel<Pending> _traffic = CreateQueue();
    private readonly Task _operationTask,
        _trafficTask;
    private int _disposed;

    public SerialLog(IOptions<SerialLogOptions> options)
        : this(options.Value, DefaultLogDirectory) { }

    public static string DefaultLogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EGGtCSPlatform",
            "logs"
        );

    public SerialLog(
        SerialLogOptions options,
        string logDirectory,
        TimeProvider? timeProvider = null,
        bool deferRetention = false
    )
    {
        _options = Snapshot(options);
        LogDirectory = logDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _writer = new(logDirectory, _timeProvider);
        if (!deferRetention)
            _writer.ConfigureRetention(_options);
        _operationTask = Task.Run(() => ConsumeAsync(_operations));
        _trafficTask = Task.Run(() => ConsumeAsync(_traffic));
        if (_options.Enabled)
            _operations.Writer.TryWrite(new(_options, Maintenance: true));
    }

    public string LogDirectory { get; }

    public void Configure(SerialLogOptions options)
    {
        var snapshot = Snapshot(options);
        Volatile.Write(ref _options, snapshot);
        _writer.ConfigureRetention(snapshot);
        if (snapshot.Enabled)
            _operations.Writer.TryWrite(new(snapshot, Maintenance: true));
    }

    public void Log(ApplicationLogEntry entry)
    {
        var options = Volatile.Read(ref _options);
        if (!options.Enabled || entry.Level < options.MinimumLevel || !Enum.IsDefined(entry.Level))
            return;
        _operations.Writer.TryWrite(new(options, Operation: entry));
    }

    public void Log(ByteTrafficLogEntry entry)
    {
        var options = Volatile.Read(ref _options);
        if (!options.Enabled)
            return;
        _traffic.Writer.TryWrite(new(options, Traffic: entry with { Data = entry.Data.ToArray() }));
    }

    public async ValueTask LogAsync(
        Exception exception,
        GlobalExceptionContext context,
        CancellationToken cancellationToken = default
    )
    {
        Log(
            new ApplicationLogEntry(
                context.OccurredAt,
                context.IsTerminating ? ApplicationLogLevel.Fatal : ApplicationLogLevel.Error,
                context.Source,
                "GLOBAL_EXCEPTION",
                $"terminating={context.IsTerminating}",
                Exception: exception
            )
        );
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Barrier(_operations, _operationTask), Barrier(_traffic, _trafficTask))
            .WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operations.Writer.TryComplete();
            _traffic.Writer.TryComplete();
        }
        await Task.WhenAll(_operationTask, _trafficTask).ConfigureAwait(false);
    }

    private static Channel<Pending> CreateQueue() =>
        Channel.CreateUnbounded<Pending>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                AllowSynchronousContinuations = false,
            }
        );

    private Task Barrier(Channel<Pending> queue, Task consumer)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        return queue.Writer.TryWrite(new(Volatile.Read(ref _options), Completion: completion))
            ? completion.Task
            : consumer;
    }

    private async Task ConsumeAsync(Channel<Pending> queue)
    {
        await foreach (var item in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item.Maintenance)
                    _writer.Maintain();
                if (item.Operation is { } operation)
                {
                    var line =
                        $"{FormatTimestamp(operation.Timestamp)} | {operation.Level}"
                        + $" | {Clean(operation.Source)} | {Clean(operation.EventName)}"
                        + $" | id={Clean(operation.CorrelationId)} | {Clean(operation.Message)}"
                        + (
                            operation.Exception is null
                                ? ""
                                : Environment.NewLine + operation.Exception
                        );
                    _writer.Append(
                        operation.Level.ToString().ToLowerInvariant(),
                        operation.Timestamp,
                        line,
                        item.Options
                    );
                    _writer.Append("all", operation.Timestamp, line, item.Options);
                }
                if (item.Traffic is { } traffic)
                {
                    var direction =
                        traffic.Direction == ByteTrafficDirection.Transmit ? "TX" : "RX";
                    var line =
                        $"{FormatTimestamp(traffic.Timestamp)} | {direction} | {Clean(traffic.Transport)}"
                        + $" | remote={Clean(traffic.RemoteEndpoint)} | length={traffic.Data.Length}"
                        + $" | {BitConverter.ToString(traffic.Data.ToArray()).Replace('-', ' ')}";
                    _writer.Append("bytes", traffic.Timestamp, line, item.Options);
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Log entry failed: {exception}");
            }
            finally
            {
                item.Completion?.TrySetResult();
            }
        }
    }

    private string FormatTimestamp(DateTimeOffset timestamp) =>
        TimeZoneInfo
            .ConvertTime(timestamp, _timeProvider.LocalTimeZone)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private static string Clean(string? text) =>
        (text ?? "-").Replace("\r", "\\r").Replace("\n", "\\n");

    private static SerialLogOptions Snapshot(SerialLogOptions options)
    {
        if (!options.IsValid())
            throw new ArgumentException("Invalid log options.", nameof(options));
        return new()
        {
            Enabled = options.Enabled,
            MaxFileSizeBytes = options.MaxFileSizeBytes,
            RetentionDays = options.RetentionDays,
            MinimumLevel = options.MinimumLevel,
        };
    }

    private sealed record Pending(
        SerialLogOptions Options,
        ApplicationLogEntry? Operation = null,
        ByteTrafficLogEntry? Traffic = null,
        TaskCompletionSource? Completion = null,
        bool Maintenance = false
    );
}
