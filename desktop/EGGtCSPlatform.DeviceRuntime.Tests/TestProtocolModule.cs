using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.DeviceRuntime.Tests;

// Test-only wire format: long transaction identifiers and textual message identifiers.
// This file is also linked by the integration tests which register Gen1 alongside this protocol.
internal sealed class TestProtocolModule(string id = "test.alpha", int channels = 12)
    : IDeviceProtocolModule
{
    public string ProtocolId => id;
    public IReadOnlySet<string> SupportedRevisions { get; } =
        new HashSet<string> { "1.0.1", "test.2" };
    public int Connections;
    public int Validations;
    public int Discoveries;
    public int Resolutions;
    public string? DefaultRevision { get; set; }
    public string? FailAddress { get; set; }
    public string DiscoveryAddress { get; set; } = "local";

    public ProtocolRevisionSelection ResolveRevision(DeviceCandidate candidate)
    {
        Interlocked.Increment(ref Resolutions);
        return
            DefaultRevision is not null
            && candidate.RevisionSource
                is ProtocolRevisionSource.SoftwareDefault
                    or ProtocolRevisionSource.CompatibilityAssumption
            ? new(DefaultRevision, ProtocolRevisionSource.SoftwareDefault)
            : new(candidate.ProtocolVersion, candidate.RevisionSource);
    }

    public List<ProtocolConnectionContext> Contexts { get; } = [];

    public bool CanConnect(DeviceCandidate candidate) =>
        SupportedRevisions.Contains(candidate.ProtocolVersion);

    public IReadOnlySet<Type> GetSupportedCommands(string revision) =>
        new HashSet<Type> { typeof(ReadDeviceStatusRequest) };

    public DeviceCapabilities GetCapabilities(
        DeviceCandidate candidate,
        IReadOnlySet<DeviceCapabilityKind> realCapabilities
    ) => new(true, true, true, true, true, false, channels, new HashSet<int> { 100 }, 2);

    public DeviceCandidate Candidate(string deviceId = "device", string revision = "1.0.1") =>
        new(new(new(deviceId), "Test", null, null), new("memory", "local", 0), revision)
        {
            ProtocolId = id,
            RevisionSource = ProtocolRevisionSource.Explicit,
        };

    public async Task<IEggtCsDevice> ConnectAsync(
        ProtocolConnectionContext context,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref Connections);
        lock (Contexts)
            Contexts.Add(context);
        if (context.Candidate.Endpoint.Address == FailAddress)
            throw new IOException("Test endpoint moved.");
        SessionEggtCsDevice? device = null;
        var session = new DeviceSession(
            context.TransportFactory.Create(context.Candidate.Endpoint),
            new TestProtocol(id),
            options: context.SessionOptions with
            {
                HeartbeatPolicy = context.Heartbeat.Enabled
                    ? new Heartbeat(context.Heartbeat, status => device!.ApplyStatus(status))
                    : null,
            }
        );
        try
        {
            await session.StartAsync(cancellationToken);
            device = new(
                context.Candidate.Identity,
                GetCapabilities(context.Candidate, context.RealCapabilities),
                session,
                faulted: context.SessionFaulted
            );
            return device;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public async Task<DeviceStatusResponse> ValidateConnectionAsync(
        IEggtCsDevice device,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref Validations);
        return await device.ReadStatusAsync(cancellationToken);
    }

    public IDeviceDiscovery CreateDiscovery(IDiscoveryTransport transport, int callbackPort)
    {
        Interlocked.Increment(ref Discoveries);
        return new Discovery(
            Candidate(revision: DefaultRevision ?? "1.0.1") with
            {
                Endpoint = new("memory", DiscoveryAddress, 0),
                RevisionSource = DefaultRevision is null
                    ? ProtocolRevisionSource.Explicit
                    : ProtocolRevisionSource.SoftwareDefault,
            }
        );
    }

    private sealed class Discovery(DeviceCandidate candidate) : IDeviceDiscovery
    {
        public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return candidate;
        }
    }

    private sealed class Heartbeat(
        ProtocolHeartbeatSettings settings,
        Action<DeviceStatusResponse> update
    ) : IHeartbeatPolicy
    {
        public TimeSpan Interval => settings.Interval;
        public TimeSpan FailureTimeout => settings.FailureTimeout;
        public bool ShouldRun => settings.ShouldRun();

        public async ValueTask ExecuteAsync(
            IDeviceSession session,
            CancellationToken cancellationToken
        )
        {
            var status = await session.SendAsync(new ReadDeviceStatusRequest(), cancellationToken);
            update(status);
            settings.StatusReceived(status);
        }
    }

    private sealed class TestProtocol(string id) : ISessionProtocol
    {
        private long _next = 65536;

        public CommandDescriptor Describe(IDeviceCommand command) =>
            command is ReadDeviceStatusRequest
                ? new(
                    command.GetType(),
                    CommunicationPattern.RequestResponse,
                    TimeSpan.FromMilliseconds(150),
                    true,
                    null,
                    typeof(DeviceStatusResponse)
                )
                : throw new NotSupportedException();

        public string AllocateCorrelationId(IReadOnlySet<string> pendingIds) =>
            $"transaction-{++_next}";

        public string? GetSerializationKey(CommandDescriptor descriptor) => null;

        public ProtocolFrame Encode(IDeviceCommand command, string correlationId) =>
            new(correlationId, id + ".status", ReadOnlyMemory<byte>.Empty);

        public byte[] EncodeFrame(ProtocolFrame frame) =>
            Encoding.UTF8.GetBytes($"{frame.CorrelationId}|{frame.MessageId}|0");

        public IReadOnlyList<FrameParseResult> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket)
        {
            var parts = Encoding.UTF8.GetString(bytes).Split('|');
            return
            [
                new(new(parts[0], parts[1], new byte[] { byte.Parse(parts[2]) }, bytes.ToArray())),
            ];
        }

        public ProtocolDecodedMessage Decode(ProtocolFrame frame) =>
            new(
                ProtocolMessageKind.Response,
                frame.CorrelationId,
                frame.MessageId,
                new DeviceStatusResponse(
                    DeviceCommandStatus.Success,
                    DeviceOperationState.Ready,
                    frame.Payload.Span[0]
                )
            );

        public bool IsMatch(
            CommandDescriptor descriptor,
            ProtocolFrame request,
            ProtocolDecodedMessage response
        ) => request.CorrelationId == response.CorrelationId && response.MessageId == id + ".reply";

        public void Reset() { }
    }
}

internal sealed class TestTransportFactory : ITransportFactory
{
    public ConcurrentQueue<AnsweringTransport> Transports { get; } = new();

    public bool CanCreate(ITransportEndpoint endpoint) => endpoint.Scheme == "memory";

    public ITransport Create(TransportEndpoint endpoint)
    {
        var transport = new AnsweringTransport();
        Transports.Enqueue(transport);
        return transport;
    }
}

internal sealed class AnsweringTransport : ITransport
{
    private readonly FakeTransport _inner = new();
    public bool Answer = true;
    public int Battery = 77;
    public bool Disposed;
    public TransportConnectionState State => _inner.State;
    public IReadOnlyList<byte[]> Sent => _inner.SentPackets;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(cancellationToken);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
        _inner.DisconnectAsync(cancellationToken);

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default
    )
    {
        await _inner.SendAsync(data, cancellationToken);
        if (Answer)
        {
            var fields = Encoding.UTF8.GetString(data.Span).Split('|');
            _inner.Inject(
                Encoding.UTF8.GetBytes(
                    $"{fields[0]}|{fields[1].Replace(".status", ".reply")}|{Battery}"
                )
            );
        }
    }

    public IAsyncEnumerable<TransportPacket> ReceiveAsync(
        CancellationToken cancellationToken = default
    ) => _inner.ReceiveAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return _inner.DisposeAsync();
    }
}
