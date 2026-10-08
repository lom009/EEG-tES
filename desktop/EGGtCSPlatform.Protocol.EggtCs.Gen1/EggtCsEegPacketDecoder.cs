using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsEegPacketDecoder : IEegPacketDecoder
{
    private readonly EggtCsFrameCodec _codec = new(new EggtCsUnverifiedFixedChecksum());

    // Historical files without protocol metadata always retain their V101 interpretation.
    private readonly EggtCsProtocolProfile _protocol = new(
        new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>(), revision: EggtCsRevisions.V101)
    );

    public IReadOnlyList<EegDataPacketReceivedEvent> Decode(
        ReadOnlyMemory<byte> bytes,
        bool endOfPacket = true
    )
    {
        var packets = new List<EegDataPacketReceivedEvent>();
        foreach (var result in _codec.FeedResults(bytes.Span, endOfPacket))
        {
            if (result.Error is not null)
                throw result.Error;
            if (
                result.Frame is { Command: (byte)EggtCsCommandCode.EegData } frame
                && _protocol.Decode(frame).Value is EegDataPacketReceivedEvent packet
            )
                packets.Add(packet);
        }
        return packets;
    }
}
