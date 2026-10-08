namespace EGGtCSPlatform.Communication.Core;

public enum CommunicationFailureDecision
{
    UseDefault,
    FailCurrentOperation,
    ReconnectDevice,
}

public sealed record CommunicationFailureContext(
    string? DeviceId,
    Guid SessionId,
    long ConnectionGeneration,
    string Category,
    Exception Exception,
    Type? CommandType,
    bool WasSent,
    bool IsIdempotent,
    bool ResponseMayBeAmbiguous,
    IReadOnlySet<CommunicationFailureDecision> AllowedDecisions
)
{
    public string? ProtocolId { get; init; }
    public string? ProtocolRevision { get; init; }
}

public delegate ValueTask<CommunicationFailureDecision> CommunicationFailureHandler(
    CommunicationFailureContext context,
    CancellationToken cancellationToken
);

/// <summary>Reports a cleaned-up failure to the owning runtime; callbacks never run on the receive loop.</summary>
public interface ICommunicationFailureSink
{
    void ReportFailure(CommunicationFailureContext context);
}
