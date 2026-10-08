namespace EGGtCSPlatform.DeviceSdk;

/// <summary>Create one decoder per ordered recording stream; input memory is borrowed for the call.</summary>
public interface IEegPacketDecoder
{
    IReadOnlyList<EegDataPacketReceivedEvent> Decode(
        ReadOnlyMemory<byte> bytes,
        bool endOfPacket = true
    );
}
