using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Communication.Udp;

public enum UdpConnectionMode
{
    Dedicated,
    Shared,
}

public sealed record UdpEndpoint(
    string RemoteAddress,
    int RemotePort,
    string LocalAddress = "0.0.0.0",
    int LocalPort = 0
) : ITransportEndpoint
{
    public string Scheme => "udp";
}

/// <summary>One registry per runtime. The registry owns the sockets and leases own routes.</summary>
public sealed class SharedUdpHubRegistry(IByteTrafficLogger? logger = null, int routeCapacity = 512)
    : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Hub> _hubs = [];
    private readonly List<Task> _retired = [];
    private bool _disposed;
    internal Func<IPEndPoint, UdpClient> SocketFactory { get; init; } =
        endpoint => new UdpClient(endpoint);

    internal Lease Acquire(
        IPAddress localAddress,
        int localPort,
        IPEndPoint? remote,
        bool receivesPackets = true
    )
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(routeCapacity);
            var hub =
                localPort == 0
                    ? null
                    : _hubs.FirstOrDefault(item =>
                        item.Port == localPort
                        && (
                            item.LocalAddress.Equals(localAddress)
                            || item.LocalAddress.Equals(IPAddress.Any)
                        )
                    );
            if (hub is null)
            {
                if (localAddress.AddressFamily != AddressFamily.InterNetwork)
                    throw new NotSupportedException("Shared UDP currently requires IPv4.");
                if (
                    localPort != 0
                    && localAddress.Equals(IPAddress.Any)
                    && _hubs.Any(item => item.Port == localPort)
                )
                    throw new InvalidOperationException(
                        "A specific interface already owns this port. Acquire the wildcard binding first to share across interfaces."
                    );
                hub = new Hub(localAddress, localPort, logger, SocketFactory);
                _hubs.Add(hub);
            }
            return hub.Subscribe(
                remote,
                routeCapacity,
                lease => Release(hub, lease),
                receivesPackets
            );
        }
    }

    private void Release(Hub hub, Lease lease)
    {
        lock (_gate)
        {
            hub.Remove(lease);
            if (hub.Count == 0)
            {
                _hubs.Remove(hub);
                hub.Close();
                _retired.RemoveAll(task => task.IsCompleted);
                _retired.Add(hub.Completion);
            }
        }
    }

    public ITransport CreateTransport(UdpEndpoint endpoint) =>
        new SharedUdpTransport(this, endpoint);

    public UdpBindingLease Bind(string localAddress, int localPort = 0) =>
        new(Acquire(IPAddress.Parse(localAddress), localPort, null, receivesPackets: false));

    public async ValueTask DisposeAsync()
    {
        Hub[] hubs;
        Task[] retired;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            hubs = _hubs.ToArray();
            _hubs.Clear();
            retired = _retired.ToArray();
            foreach (var hub in hubs)
                hub.Close();
        }
        foreach (var hub in hubs)
            await hub.Completion.ConfigureAwait(false);
        await Task.WhenAll(retired).ConfigureAwait(false);
    }

    public sealed class UdpBindingLease : IAsyncDisposable
    {
        private readonly Lease _lease;

        internal UdpBindingLease(Lease lease) => _lease = lease;

        public int LocalPort => _lease.LocalPort;

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    internal sealed class Lease(
        Hub hub,
        IPEndPoint? remote,
        int capacity,
        Action<Lease> release,
        bool receivesPackets
    ) : IAsyncDisposable
    {
        private readonly Channel<TransportPacket> _packets = Channel.CreateBounded<TransportPacket>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            }
        );
        private int _disposed;
        public int LocalPort => hub.Port;
        public IPEndPoint? Remote => remote;

        public IAsyncEnumerable<TransportPacket> ReceiveAsync(CancellationToken token) =>
            _packets.Reader.ReadAllAsync(token);

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> bytes,
            IPEndPoint endpoint,
            CancellationToken token
        ) => hub.SendAsync(bytes, endpoint, token);

        public void Publish(TransportPacket packet)
        {
            if (!receivesPackets)
                return;
            if (remote is null)
                packet = packet with { Data = packet.Data.ToArray() };
            if (!_packets.Writer.TryWrite(packet))
                _packets.Writer.TryComplete(
                    new TransportCommunicationException("UDP route queue overflowed.")
                );
        }

        public void Complete(Exception? error = null) => _packets.Writer.TryComplete(error);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Complete();
                release(this);
            }
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class Hub
    {
        private readonly UdpClient _socket;
        private readonly AsyncCommunicationDiagnostics _logs;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly List<Lease> _leases = [];
        private Exception? _failure;

        public Hub(
            IPAddress address,
            int port,
            IByteTrafficLogger? logger,
            Func<IPEndPoint, UdpClient> socketFactory
        )
        {
            LocalAddress = address;
            _socket = socketFactory(new IPEndPoint(address, port));
            _socket.EnableBroadcast = true;
            Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
            _logs = new(traffic: logger);
            Completion = Task.Run(ReceiveAsync);
        }

        public int Port { get; }
        public IPAddress LocalAddress { get; }
        public int Count
        {
            get
            {
                lock (_gate)
                    return _leases.Count;
            }
        }
        public Task Completion { get; }

        public Lease Subscribe(
            IPEndPoint? remote,
            int capacity,
            Action<Lease> release,
            bool receivesPackets
        )
        {
            lock (_gate)
            {
                if (_failure is not null)
                    throw new TransportCommunicationException(
                        "Shared socket has faulted.",
                        _failure
                    );
                if (remote is not null && _leases.Any(item => Equals(item.Remote, remote)))
                    throw new InvalidOperationException(
                        $"A UDP route for {remote} is already connected."
                    );
                var lease = new Lease(this, remote, capacity, release, receivesPackets);
                _leases.Add(lease);
                return lease;
            }
        }

        public void Remove(Lease lease)
        {
            lock (_gate)
                _leases.Remove(lease);
        }

        public void Close()
        {
            _lifetime.Cancel();
            _socket.Dispose();
        }

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> bytes,
            IPEndPoint endpoint,
            CancellationToken token
        )
        {
            await _socket.SendAsync(bytes, endpoint, token).ConfigureAwait(false);
            _logs.Log(
                new(
                    DateTimeOffset.Now,
                    ByteTrafficDirection.Transmit,
                    "UDP",
                    endpoint.ToString(),
                    bytes
                )
            );
        }

        private async Task ReceiveAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var result = await _socket.ReceiveAsync(_lifetime.Token).ConfigureAwait(false);
                    var receivedAt = DateTimeOffset.Now;
                    var packet = new TransportPacket(
                        result.Buffer,
                        result.RemoteEndPoint.ToString(),
                        true,
                        receivedAt.ToUniversalTime(),
                        Stopwatch.GetTimestamp()
                    );
                    _logs.Log(
                        new(
                            receivedAt,
                            ByteTrafficDirection.Receive,
                            "UDP",
                            packet.RemoteAddress!,
                            packet.Data,
                            true
                        )
                    );
                    lock (_gate)
                        foreach (var lease in _leases)
                            if (lease.Remote is null || lease.Remote.Equals(result.RemoteEndPoint))
                                lease.Publish(packet);
                }
            }
            catch (Exception error)
                when (_lifetime.IsCancellationRequested
                    && error
                        is OperationCanceledException
                            or ObjectDisposedException
                            or SocketException
                ) { }
            catch (Exception error)
            {
                lock (_gate)
                    _failure = new TransportCommunicationException(
                        "Shared UDP receive failed.",
                        error
                    );
            }
            finally
            {
                lock (_gate)
                    foreach (var lease in _leases)
                        lease.Complete(_failure);
                await _logs.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

public sealed class SharedUdpTransport(SharedUdpHubRegistry registry, UdpEndpoint endpoint)
    : ITransport
{
    private SharedUdpHubRegistry.Lease? _lease;
    private bool _disposed;
    public TransportConnectionState State { get; private set; } =
        TransportConnectionState.Disconnected;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != TransportConnectionState.Disconnected)
            throw new InvalidOperationException("Already connected.");
        _lease = registry.Acquire(
            IPAddress.Parse(endpoint.LocalAddress),
            endpoint.LocalPort,
            new IPEndPoint(IPAddress.Parse(endpoint.RemoteAddress), endpoint.RemotePort)
        );
        State = TransportConnectionState.Connected;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _lease, null) is { } lease)
            await lease.DisposeAsync().ConfigureAwait(false);
        State = TransportConnectionState.Disconnected;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    )
    {
        var lease = _lease ?? throw new InvalidOperationException("Not connected.");
        return lease.SendAsync(data, lease.Remote!, cancellationToken);
    }

    public IAsyncEnumerable<TransportPacket> ReceiveAsync(
        CancellationToken cancellationToken = default
    ) =>
        (_lease ?? throw new InvalidOperationException("Not connected.")).ReceiveAsync(
            cancellationToken
        );

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return DisconnectAsync();
    }
}

/// <summary>A probe factory receives the actual bound port, including when LocalPort is zero.</summary>
public sealed class UdpTargetedDiscoveryTransport(
    string localAddress,
    int localPort,
    IReadOnlyList<IPEndPoint> targets,
    SharedUdpHubRegistry? registry = null,
    IByteTrafficLogger? logger = null
) : IBoundDiscoveryTransport
{
    public IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan window,
        CancellationToken cancellationToken = default
    ) => DiscoverAsync(_ => probe, window, cancellationToken);

    public async IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
        Func<int, ReadOnlyMemory<byte>> probeFactory,
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));
        await using var ownedRegistry = registry is null ? new SharedUdpHubRegistry(logger) : null;
        await using var lease = (registry ?? ownedRegistry!).Acquire(
            IPAddress.Parse(localAddress),
            localPort,
            null
        );
        var probe = probeFactory(lease.LocalPort);
        var sent = false;
        foreach (var target in targets)
        {
            try
            {
                await lease.SendAsync(probe, target, cancellationToken).ConfigureAwait(false);
                sent = true;
            }
            catch (SocketException) { }
        }
        if (!sent)
            throw new TransportCommunicationException("No discovery target could be reached.");
        using var timeout = new CancellationTokenSource(window);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            cancellationToken
        );
        await using var reader = lease.ReceiveAsync(linked.Token).GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool next;
            try
            {
                next = await reader.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            if (!next)
                yield break;
            yield return new(reader.Current.Data, reader.Current.RemoteAddress!);
        }
    }
}
