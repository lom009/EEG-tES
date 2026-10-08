namespace EGGtCSPlatform.Communication.Core;

public enum ByteTrafficDirection
{
    Transmit,
    Receive,
}

public sealed record ByteTrafficLogEntry(
    DateTimeOffset Timestamp,
    ByteTrafficDirection Direction,
    string Transport,
    string RemoteEndpoint,
    ReadOnlyMemory<byte> Data,
    bool DataIsImmutable = false
);

public interface IByteTrafficLogger
{
    void Log(ByteTrafficLogEntry entry);
}

public sealed class NullByteTrafficLogger : IByteTrafficLogger
{
    public static NullByteTrafficLogger Instance { get; } = new();

    private NullByteTrafficLogger() { }

    public void Log(ByteTrafficLogEntry entry) { }
}
