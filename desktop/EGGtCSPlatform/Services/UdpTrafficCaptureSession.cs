using System;
using System.Collections.Generic;
using System.Threading;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Services;

public sealed class UdpTrafficDisplayEntry
{
    private readonly byte[] _data;
    private string? _hexText;

    public UdpTrafficDisplayEntry(
        DateTimeOffset timestamp,
        ByteTrafficDirection direction,
        string transport,
        string remoteEndpoint,
        byte[] data,
        byte? command,
        string commandName
    )
    {
        Timestamp = timestamp;
        Direction = direction;
        Transport = transport;
        RemoteEndpoint = remoteEndpoint;
        _data = data;
        Length = data.Length;
        Command = command;
        CommandName = commandName;
    }

    public DateTimeOffset Timestamp { get; }

    public ByteTrafficDirection Direction { get; }

    public string Transport { get; }

    public string RemoteEndpoint { get; }

    public int Length { get; }

    public byte? Command { get; }

    public string CommandName { get; }

    public string HexText =>
        LazyInitializer.EnsureInitialized(
            ref _hexText,
            () => BitConverter.ToString(_data).Replace('-', ' ')
        );

    internal bool IsHexTextCreated => Volatile.Read(ref _hexText) is not null;

    public bool IsTransmit => Direction == ByteTrafficDirection.Transmit;

    public string TimestampText => $"[{Timestamp:HH:mm:ss.fff}]";

    public string DirectionText => IsTransmit ? "TX" : "RX";

    public string CommandText => Command is { } command ? $"0x{command:X2}" : "--";

    public string MetadataText => $"{Transport} {RemoteEndpoint} · {Length}B";

    public string CopyText =>
        $"{TimestampText} {DirectionText} {CommandName} {CommandText} {MetadataText}{Environment.NewLine}{HexText}";
}

public sealed record UdpTrafficCaptureBatch(
    long Generation,
    IReadOnlyList<UdpTrafficDisplayEntry> Entries
);

public sealed class UdpTrafficCaptureSession : IByteTrafficLogger, IDisposable
{
    public const int MaximumEntries = 500;

    private readonly object _gate = new();
    private readonly List<UdpTrafficDisplayEntry> _pending = [];
    private long _generation;
    private bool _isPaused;
    private bool _disposed;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _isPaused;
        }
    }

    public void Log(ByteTrafficLogEntry entry)
    {
        if (!string.Equals(entry.Transport, "UDP", StringComparison.OrdinalIgnoreCase))
            return;
        lock (_gate)
        {
            if (_disposed || _isPaused)
                return;
        }
        var bytes = entry.Data.ToArray();
        var inspection = V101UdpPacketInspector.Inspect(bytes, entry.Direction);
        var displayEntry = new UdpTrafficDisplayEntry(
            entry.Timestamp,
            entry.Direction,
            "UDP",
            entry.RemoteEndpoint,
            bytes,
            inspection.Command,
            inspection.CommandName
        );
        lock (_gate)
        {
            if (_disposed || _isPaused)
                return;
            _pending.Add(displayEntry);
            if (_pending.Count > MaximumEntries)
                _pending.RemoveRange(0, _pending.Count - MaximumEntries);
        }
    }

    public UdpTrafficCaptureBatch Drain()
    {
        lock (_gate)
        {
            var entries = _pending.ToArray();
            _pending.Clear();
            return new UdpTrafficCaptureBatch(_generation, entries);
        }
    }

    public long Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            return ++_generation;
        }
    }

    public void SetPaused(bool isPaused)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _isPaused = isPaused;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _isPaused = true;
            _pending.Clear();
            ++_generation;
        }
    }
}
