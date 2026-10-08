using System.Buffers.Binary;
using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public enum DatagramRemainderPolicy
{
    Preserve,
    Discard,
}

public sealed class EggtCsFrameCodec(
    IChecksum checksum,
    int maximumFrameLength = ushort.MaxValue,
    DatagramRemainderPolicy remainderPolicy = DatagramRemainderPolicy.Preserve
) : IFrameResultCodec
{
    private const byte HeaderFirst = 0xAA;
    private const byte ControlHeaderSecond = 0xCC;
    private const byte DataHeaderSecond = 0xBB;
    private readonly IChecksum _checksum = checksum;
    private readonly int _maximumFrameLength = maximumFrameLength;
    private readonly List<byte> _buffer = [];

    public byte[] Encode(WireMessage message)
    {
        if (
            message.FrameKind != WireFrameKind.Control
            && !(
                message.FrameKind == WireFrameKind.Data
                && message.Command == (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest
            )
        )
            throw new NotSupportedException(
                "The host encodes Gen1 control frames and envelope configuration frames only."
            );
        var envelope = message.FrameKind == WireFrameKind.Data;
        var payloadOffset = envelope ? 6 : 5;
        var length = checked(payloadOffset + message.Payload.Length + _checksum.Size);
        if (length > _maximumFrameLength || length > (envelope ? ushort.MaxValue : byte.MaxValue))
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "Frame length exceeds the configured limit or the length field capacity."
            );

        var frame = new byte[length];
        frame[0] = HeaderFirst;
        frame[1] = envelope ? DataHeaderSecond : ControlHeaderSecond;
        frame[2] = message.Index;
        if (envelope)
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), checked((ushort)length));
        else
            frame[3] = checked((byte)length);
        frame[payloadOffset - 1] = message.Command;
        message.Payload.Span.CopyTo(frame.AsSpan(payloadOffset));
        _checksum.Write(
            frame.AsSpan(0, length - _checksum.Size),
            frame.AsSpan(length - _checksum.Size)
        );
        return frame;
    }

    public IReadOnlyList<WireMessage> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket)
    {
        var results = FeedResults(bytes, endOfPacket);
        var error = results.FirstOrDefault(item => item.Error is not null)?.Error;
        if (error is not null)
            throw error;
        return results.Where(item => item.Frame is not null).Select(item => item.Frame!).ToArray();
    }

    public IReadOnlyList<WireFrameParseResult> FeedResults(
        ReadOnlySpan<byte> bytes,
        bool endOfPacket
    )
    {
        if (!bytes.IsEmpty)
            _buffer.AddRange(bytes.ToArray());
        var messages = new List<WireFrameParseResult>();

        while (true)
        {
            var header = FindHeader();
            if (header < 0)
            {
                if (_buffer.Count > 1)
                    _buffer.RemoveRange(0, _buffer.Count - 1);
                break;
            }
            if (header > 0)
                _buffer.RemoveRange(0, header);
            if (_buffer.Count < 4)
                break;

            var frameKind =
                _buffer[1] == ControlHeaderSecond ? WireFrameKind.Control : WireFrameKind.Data;
            if (frameKind == WireFrameKind.Data && _buffer.Count < 5)
                break;
            // 0xCA is the protocol's eight-byte short acknowledgement on AA BB.
            var shortEnvelopeAck =
                frameKind == WireFrameKind.Data
                && _buffer[3] == 8
                && _buffer[4] == (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse;
            if (shortEnvelopeAck)
            {
                if (_buffer.Count < 8)
                    break;
                // 0xCA08 is also a legal long-frame length. A valid short ACK wins;
                // otherwise a known long-frame command keeps its two-byte length.
                var prefix = _buffer.GetRange(0, 8).ToArray();
                if (
                    !_checksum.Validate(prefix.AsSpan(0, 6), prefix.AsSpan(6))
                    && _buffer[5]
                        is (byte)EggtCsCommandCode.EegData
                            or (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest
                )
                    shortEnvelopeAck = false;
            }
            var lengthFieldSize = frameKind == WireFrameKind.Control || shortEnvelopeAck ? 1 : 2;
            var minimumLength = 2 + 1 + lengthFieldSize + 1 + _checksum.Size;
            if (_buffer.Count < 3 + lengthFieldSize)
                break;
            var length =
                lengthFieldSize == 1
                    ? _buffer[3]
                    : BinaryPrimitives.ReadUInt16LittleEndian(new[] { _buffer[3], _buffer[4] });
            if (length < minimumLength || length > _maximumFrameLength)
            {
                _buffer.RemoveAt(0);
                continue;
            }
            if (_buffer.Count < length)
                break;

            var frame = _buffer.GetRange(0, length).ToArray();
            _buffer.RemoveRange(0, length);
            var contentLength = length - _checksum.Size;
            if (!_checksum.Validate(frame.AsSpan(0, contentLength), frame.AsSpan(contentLength)))
            {
                messages.Add(
                    new(
                        null,
                        frame,
                        new FrameValidationException("Gen1 frame checksum validation failed.")
                    )
                );
                continue;
            }

            var commandOffset = 3 + lengthFieldSize;
            var payloadOffset = commandOffset + 1;
            var payloadLength = contentLength - payloadOffset;
            messages.Add(
                new WireFrameParseResult(
                    new WireMessage(
                        frame[2],
                        frame[commandOffset],
                        frame.AsMemory(payloadOffset, payloadLength).ToArray(),
                        frameKind
                    ),
                    frame
                )
            );
        }

        if (endOfPacket && remainderPolicy == DatagramRemainderPolicy.Discard)
            _buffer.Clear();
        return messages;
    }

    public void Reset() => _buffer.Clear();

    private int FindHeader()
    {
        for (var index = 0; index + 1 < _buffer.Count; index++)
        {
            if (
                _buffer[index] == HeaderFirst
                && _buffer[index + 1] is ControlHeaderSecond or DataHeaderSecond
            )
                return index;
        }
        return -1;
    }
}
