using System;
using System.Linq;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EggtCsFrameCodecTests
{
    private readonly EggtCsUnverifiedFixedChecksum _checksum = new();

    [Fact]
    public void EncodesControlFrameUsingDocumentedTotalLength()
    {
        var codec = new EggtCsFrameCodec(_checksum);

        var bytes = codec.Encode(
            new WireMessage(
                1,
                (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
                ReadOnlyMemory<byte>.Empty
            )
        );

        Assert.Equal(new byte[] { 0xAA, 0xCC, 0x01, 0x07, 0x04, 0xFF, 0xFF }, bytes);
    }

    [Fact]
    public void ReassemblesFragmentedFrameAndSplitsConcatenatedFrames()
    {
        var codec = new EggtCsFrameCodec(_checksum);
        var first = codec.Encode(
            new WireMessage(
                3,
                (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                new byte[] { 0x01, 0x64 }
            )
        );
        var second = codec.Encode(
            new WireMessage(
                4,
                (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                new byte[] { 0x00 }
            )
        );

        Assert.Empty(codec.Feed(first.AsSpan(0, 4), endOfPacket: false));
        var messages = codec.Feed(
            first.AsSpan(4).ToArray().Concat(second).ToArray(),
            endOfPacket: true
        );

        Assert.Equal(2, messages.Count);
        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusResponse, messages[0].Command);
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceResponse, messages[1].Command);
    }

    [Fact]
    public void DecodesAaBbFrameWithTwoByteLittleEndianLength()
    {
        var codec = new EggtCsFrameCodec(_checksum);
        var frame = new byte[] { 0xAA, 0xBB, 0x05, 0x0A, 0x00, 0x97, 0x11, 0x22, 0xFF, 0xFF };

        var message = Assert.Single(codec.Feed(frame, endOfPacket: true));

        Assert.Equal(WireFrameKind.Data, message.FrameKind);
        Assert.Equal((byte)EggtCsCommandCode.EegData, message.Command);
        Assert.Equal(new byte[] { 0x11, 0x22 }, message.Payload.ToArray());
    }

    [Fact]
    public void RejectsInvalidChecksumWithoutConsumingFollowingFrame()
    {
        var codec = new EggtCsFrameCodec(_checksum);
        var invalid = new byte[] { 0xAA, 0xCC, 0x01, 0x07, 0x04, 0x00, 0x00 };

        Assert.Throws<FrameValidationException>(() => codec.Feed(invalid, endOfPacket: true));
    }
}
