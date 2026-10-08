namespace EGGtCSPlatform.Communication.Core;

public abstract class CommunicationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class TransportCommunicationException(
    string message,
    Exception? innerException = null
) : CommunicationException(message, innerException);

public sealed class FrameValidationException(string message) : CommunicationException(message);

public sealed class ProtocolDecodingException(string message, Exception? innerException = null)
    : CommunicationException(message, innerException);

public sealed class RequestTimeoutException(Type commandType, TimeSpan timeout)
    : CommunicationException($"{commandType.Name} did not receive a response within {timeout}.")
{
    public Type CommandType { get; } = commandType;

    public TimeSpan Timeout { get; } = timeout;
}

public sealed class HeartbeatTimeoutException(
    Guid sessionId,
    TimeSpan timeout,
    Exception? innerException = null
)
    : CommunicationException(
        $"Device session {sessionId} did not receive a successful heartbeat within {timeout}.",
        innerException
    )
{
    public Guid SessionId { get; } = sessionId;

    public TimeSpan Timeout { get; } = timeout;
}

public sealed class DeviceDisconnectedException(Guid sessionId)
    : CommunicationException($"Device session {sessionId} was disconnected.");

public sealed class ResponseMatchException(string message) : CommunicationException(message);

public sealed class DeviceEventBufferOverflowException(EventDeliveryClass deliveryClass)
    : CommunicationException(
        $"The {deliveryClass} event buffer is full; the session was faulted to avoid silent data loss."
    );
