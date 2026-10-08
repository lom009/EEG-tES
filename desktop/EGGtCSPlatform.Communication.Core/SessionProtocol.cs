namespace EGGtCSPlatform.Communication.Core;

/// <summary>Identifiers are protocol-owned opaque values, not limited to a byte.</summary>
public sealed record ProtocolFrame(
    string CorrelationId,
    string MessageId,
    ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> RawFrame = default,
    object? NativeFrame = null
);

public sealed record ProtocolDecodedMessage(
    ProtocolMessageKind Kind,
    string CorrelationId,
    string MessageId,
    object Value,
    EventDeliveryClass DeliveryClass = EventDeliveryClass.Control
);

public sealed record FrameParseResult(ProtocolFrame? Frame, Exception? Error = null);

public interface ISessionProtocol
{
    CommandDescriptor Describe(IDeviceCommand command);
    string AllocateCorrelationId(IReadOnlySet<string> pendingIds);
    string? GetSerializationKey(CommandDescriptor descriptor);
    ProtocolFrame Encode(IDeviceCommand command, string correlationId);
    byte[] EncodeFrame(ProtocolFrame frame);
    IReadOnlyList<FrameParseResult> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket);
    ProtocolDecodedMessage Decode(ProtocolFrame frame);
    bool IsMatch(
        CommandDescriptor descriptor,
        ProtocolFrame request,
        ProtocolDecodedMessage response
    );
    void Reset();
}

public sealed record WireFrameParseResult(
    WireMessage? Frame,
    ReadOnlyMemory<byte> RawFrame = default,
    Exception? Error = null
);

/// <summary>Optional lossless, per-frame parsing extension to the original codec.</summary>
public interface IFrameResultCodec : IFrameCodec
{
    IReadOnlyList<WireFrameParseResult> FeedResults(ReadOnlySpan<byte> bytes, bool endOfPacket);
}

public sealed class LegacySessionProtocol(
    IFrameCodec codec,
    IDeviceProtocolProfile profile,
    IResponseMatcher? matcher = null
) : ISessionProtocol
{
    private readonly IResponseMatcher _matcher = matcher ?? new IndexAndCommandResponseMatcher();
    private int _nextIndex;

    public CommandDescriptor Describe(IDeviceCommand command) => profile.Describe(command);

    public string AllocateCorrelationId(IReadOnlySet<string> pendingIds)
    {
        for (var i = 0; i < 256; i++)
        {
            var key = unchecked((byte)++_nextIndex).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            if (!pendingIds.Contains(key))
                return key;
        }
        throw new InvalidOperationException("All protocol request indexes are in use.");
    }

    public string? GetSerializationKey(CommandDescriptor descriptor) =>
        descriptor.Pattern == CommunicationPattern.RequestResponse
        && descriptor.ResponseCorrelation == ResponseCorrelationMode.CommandOnly
            ? descriptor.ExpectedResponseCommand?.ToString()
                ?? throw new InvalidOperationException("Missing response command.")
            : null;

    public ProtocolFrame Encode(IDeviceCommand command, string correlationId) =>
        Wrap(
            profile.Encode(
                command,
                byte.Parse(correlationId, System.Globalization.CultureInfo.InvariantCulture)
            )
        );

    public byte[] EncodeFrame(ProtocolFrame frame) => codec.Encode((WireMessage)frame.NativeFrame!);

    public IReadOnlyList<FrameParseResult> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket) =>
        codec is IFrameResultCodec results
            ? results
                .FeedResults(bytes, endOfPacket)
                .Select(item => new FrameParseResult(
                    item.Frame is null ? null : Wrap(item.Frame, item.RawFrame),
                    item.Error
                ))
                .ToArray()
            : codec
                .Feed(bytes, endOfPacket)
                .Select(item => new FrameParseResult(Wrap(item)))
                .ToArray();

    public ProtocolDecodedMessage Decode(ProtocolFrame frame)
    {
        var value = profile.Decode((WireMessage)frame.NativeFrame!);
        return new(
            value.Kind,
            value.Index.ToString(),
            value.Command.ToString(),
            value.Value,
            value.DeliveryClass
        );
    }

    public bool IsMatch(
        CommandDescriptor descriptor,
        ProtocolFrame request,
        ProtocolDecodedMessage response
    ) =>
        _matcher.IsMatch(
            descriptor,
            (WireMessage)request.NativeFrame!,
            new DecodedProtocolMessage(
                response.Kind,
                byte.Parse(response.CorrelationId),
                byte.Parse(response.MessageId),
                response.Value,
                response.DeliveryClass
            )
        );

    public void Reset() => codec.Reset();

    private static ProtocolFrame Wrap(WireMessage frame, ReadOnlyMemory<byte> raw = default) =>
        new(frame.Index.ToString(), frame.Command.ToString(), frame.Payload, raw, frame);
}

internal sealed class ProtocolRequestScheduler(ISessionProtocol protocol) : IRequestScheduler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        SemaphoreSlim
    > _gates = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        CommandDescriptor descriptor,
        CancellationToken cancellationToken = default
    )
    {
        var key = protocol.GetSerializationKey(descriptor);
        if (key is null)
            return new Lease(null);
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    // Leases can still be unwinding during session shutdown. Do not dispose their semaphores.
    public ValueTask DisposeAsync()
    {
        _gates.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class Lease(SemaphoreSlim? gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
