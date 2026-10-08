using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Communication.Udp;

public sealed record UdpTransportOptions(
    string RemoteAddress,
    int RemotePort,
    string LocalAddress = "0.0.0.0",
    int LocalPort = 0
);

public sealed class UdpTransportFactory(
    string localAddress = "0.0.0.0",
    int localPort = 0,
    IByteTrafficLogger? byteTrafficLogger = null
) : ITransportFactory
{
    public bool CanCreate(ITransportEndpoint endpoint) =>
        endpoint.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase)
        && endpoint is TransportEndpoint or UdpEndpoint;

    public ITransport Create(ITransportEndpoint endpoint) =>
        endpoint is UdpEndpoint udp
            ? new UdpTransport(
                new(udp.RemoteAddress, udp.RemotePort, udp.LocalAddress, udp.LocalPort),
                byteTrafficLogger
            )
            : Create((TransportEndpoint)endpoint);

    public ITransport Create(TransportEndpoint endpoint)
    {
        if (!string.Equals(endpoint.Scheme, "udp", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Endpoint scheme '{endpoint.Scheme}' is not UDP.");
        return new UdpTransport(
            new UdpTransportOptions(endpoint.Address, endpoint.Port, localAddress, localPort),
            byteTrafficLogger
        );
    }
}

public sealed class UdpTransport(
    UdpTransportOptions options,
    IByteTrafficLogger? byteTrafficLogger = null
) : ITransport
{
    private readonly UdpTransportOptions _options = options;
    private readonly AsyncCommunicationDiagnostics _byteTrafficLogger = new(
        traffic: byteTrafficLogger
    );
    private UdpClient? _client;
    private bool _disposed;

    public TransportConnectionState State { get; private set; } =
        TransportConnectionState.Disconnected;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State != TransportConnectionState.Disconnected)
            throw new InvalidOperationException(
                $"Cannot connect a UDP transport in state {State}."
            );

        State = TransportConnectionState.Connecting;
        try
        {
            var local = new IPEndPoint(ParseAddress(_options.LocalAddress), _options.LocalPort);
            var client = new UdpClient(local);
            _client = client;
            client.Connect(ParseAddress(_options.RemoteAddress), _options.RemotePort);
            State = TransportConnectionState.Connected;
            return ValueTask.CompletedTask;
        }
        catch
        {
            State = TransportConnectionState.Faulted;
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == TransportConnectionState.Disconnected)
            return ValueTask.CompletedTask;
        State = TransportConnectionState.Disconnecting;
        _client?.Dispose();
        _client = null;
        State = TransportConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = GetConnectedClient();
        await client.SendAsync(data, cancellationToken).ConfigureAwait(false);
        _byteTrafficLogger.Log(
            new ByteTrafficLogEntry(
                DateTimeOffset.Now,
                ByteTrafficDirection.Transmit,
                "UDP",
                $"{_options.RemoteAddress}:{_options.RemotePort}",
                data.ToArray(),
                DataIsImmutable: true
            )
        );
    }

    public async IAsyncEnumerable<TransportPacket> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var client = GetConnectedClient();
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
                when (State
                        is TransportConnectionState.Disconnecting
                            or TransportConnectionState.Disconnected
                )
            {
                yield break;
            }

            // Capture the earliest timestamp observable by the application. Nothing that
            // follows (logging, decoding or dispatch) may redefine packet arrival time.
            var receivedAt = DateTimeOffset.Now;
            var receivedTimestamp = Stopwatch.GetTimestamp();
            _byteTrafficLogger.Log(
                new ByteTrafficLogEntry(
                    receivedAt,
                    ByteTrafficDirection.Receive,
                    "UDP",
                    result.RemoteEndPoint.ToString(),
                    result.Buffer,
                    DataIsImmutable: true
                )
            );
            yield return new TransportPacket(
                result.Buffer,
                result.RemoteEndPoint.ToString(),
                PreservesMessageBoundary: true,
                receivedAt.ToUniversalTime(),
                receivedTimestamp
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        await _byteTrafficLogger.DisposeAsync().ConfigureAwait(false);
    }

    private UdpClient GetConnectedClient()
    {
        if (State != TransportConnectionState.Connected || _client is null)
            throw new InvalidOperationException("UDP transport is not connected.");
        return _client;
    }

    private static IPAddress ParseAddress(string value)
    {
        if (IPAddress.TryParse(value, out var address))
            return address;
        var addresses = Dns.GetHostAddresses(value, AddressFamily.InterNetwork);
        return addresses.FirstOrDefault()
            ?? throw new ArgumentException(
                $"Unable to resolve IPv4 address '{value}'.",
                nameof(value)
            );
    }
}
