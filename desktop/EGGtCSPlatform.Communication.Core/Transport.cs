using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EGGtCSPlatform.Communication.Core;

public enum TransportConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted,
}

public interface ITransportEndpoint
{
    string Scheme { get; }
}

public sealed record TransportEndpoint(string Scheme, string Address, int Port) : ITransportEndpoint
{
    public override string ToString() => $"{Scheme}://{Address}:{Port}";
}

public sealed record TransportPacket(
    ReadOnlyMemory<byte> Data,
    string? RemoteAddress = null,
    bool PreservesMessageBoundary = true,
    DateTimeOffset ReceivedAtUtc = default,
    long ReceivedTimestamp = 0
)
{
    public static TransportPacket Received(
        ReadOnlyMemory<byte> data,
        string? remoteAddress = null,
        bool preservesMessageBoundary = true
    ) =>
        new(
            data,
            remoteAddress,
            preservesMessageBoundary,
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp()
        );
}

public interface ITransport : IAsyncDisposable
{
    TransportConnectionState State { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TransportPacket> ReceiveAsync(CancellationToken cancellationToken = default);
}

public interface ITransportFactory
{
    ITransport Create(TransportEndpoint endpoint);

    bool CanCreate(ITransportEndpoint endpoint) => endpoint is TransportEndpoint;

    ITransport Create(ITransportEndpoint endpoint) =>
        endpoint is TransportEndpoint legacy
            ? Create(legacy)
            : throw new NotSupportedException($"Unsupported endpoint {endpoint.GetType().Name}.");
}

public sealed record DiscoveryPacket(ReadOnlyMemory<byte> Data, string RemoteAddress);

public interface IDiscoveryTransport
{
    IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan window,
        CancellationToken cancellationToken = default
    );
}

public interface IBoundDiscoveryTransport : IDiscoveryTransport
{
    IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        Func<int, ReadOnlyMemory<byte>> probeFactory,
        TimeSpan window,
        CancellationToken cancellationToken = default
    );
}

public sealed class FakeTransport : ITransport
{
    private System.Threading.Channels.Channel<TransportPacket> _incoming =
        System.Threading.Channels.Channel.CreateUnbounded<TransportPacket>();
    private readonly List<byte[]> _sent = [];
    private bool _disposed;

    public TransportConnectionState State { get; private set; } =
        TransportConnectionState.Disconnected;

    public IReadOnlyList<byte[]> SentPackets
    {
        get
        {
            lock (_sent)
                return _sent.ToArray();
        }
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_incoming.Reader.Completion.IsCompleted)
            _incoming = System.Threading.Channels.Channel.CreateUnbounded<TransportPacket>();
        State = TransportConnectionState.Connected;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = TransportConnectionState.Disconnected;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State != TransportConnectionState.Connected)
            throw new InvalidOperationException("Transport is not connected.");
        lock (_sent)
            _sent.Add(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<TransportPacket> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken))
            yield return packet;
    }

    public bool Inject(ReadOnlyMemory<byte> data, bool preservesMessageBoundary = true) =>
        _incoming.Writer.TryWrite(
            TransportPacket.Received(data.ToArray(), null, preservesMessageBoundary)
        );

    public bool Inject(
        ReadOnlyMemory<byte> data,
        DateTimeOffset receivedAtUtc,
        long receivedTimestamp,
        bool preservesMessageBoundary = true
    ) =>
        _incoming.Writer.TryWrite(
            new TransportPacket(
                data.ToArray(),
                null,
                preservesMessageBoundary,
                receivedAtUtc,
                receivedTimestamp
            )
        );

    public void ClearSentPackets()
    {
        lock (_sent)
            _sent.Clear();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        State = TransportConnectionState.Disconnected;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
