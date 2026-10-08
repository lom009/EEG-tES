namespace EGGtCSPlatform.Communication.Core;

public interface IDeviceCommand;

public interface IDeviceRequest<out TResponse> : IDeviceCommand;

public enum CommunicationPattern
{
    RequestResponse,
    FireAndForget,
}

public enum ResponseCorrelationMode
{
    IndexAndCommand,
    CommandOnly,
}

public enum ProtocolMessageKind
{
    Response,
    DeviceEvent,
}

public enum EventDeliveryClass
{
    Control,
    Data,
}

public enum WireFrameKind
{
    Control,
    Data,
}

public sealed record CommandDescriptor(
    Type CommandType,
    CommunicationPattern Pattern,
    TimeSpan Timeout,
    bool IsIdempotent,
    byte? ExpectedResponseCommand,
    Type? ResponseType,
    ResponseCorrelationMode ResponseCorrelation = ResponseCorrelationMode.IndexAndCommand
);

public sealed record WireMessage(
    byte Index,
    byte Command,
    ReadOnlyMemory<byte> Payload,
    WireFrameKind FrameKind = WireFrameKind.Control
);

public sealed record DecodedProtocolMessage(
    ProtocolMessageKind Kind,
    byte Index,
    byte Command,
    object Value,
    EventDeliveryClass DeliveryClass = EventDeliveryClass.Control
);

public interface IFrameCodec
{
    byte[] Encode(WireMessage message);

    IReadOnlyList<WireMessage> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket);

    void Reset();
}

public interface IChecksum
{
    int Size { get; }

    bool IsVerifiedForPhysicalDevices { get; }

    void Write(ReadOnlySpan<byte> content, Span<byte> destination);

    bool Validate(ReadOnlySpan<byte> content, ReadOnlySpan<byte> checksum);
}

public interface IDeviceProtocolProfile
{
    string Version { get; }

    CommandDescriptor Describe(IDeviceCommand command);

    WireMessage Encode(IDeviceCommand command, byte index);

    DecodedProtocolMessage Decode(WireMessage message);
}

public interface IResponseMatcher
{
    bool IsMatch(
        CommandDescriptor descriptor,
        WireMessage request,
        DecodedProtocolMessage response
    );
}

public sealed class IndexAndCommandResponseMatcher : IResponseMatcher
{
    public bool IsMatch(
        CommandDescriptor descriptor,
        WireMessage request,
        DecodedProtocolMessage response
    ) =>
        response.Kind == ProtocolMessageKind.Response
        && response.Index == request.Index
        && descriptor.ExpectedResponseCommand == response.Command;
}
