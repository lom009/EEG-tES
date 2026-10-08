using System.Buffers.Binary;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using Xunit;

namespace EGGtCSPlatform.DeviceRuntime.Tests;

// Fictional test protocol only. Neither these revisions nor opcodes belong to Gen1/V102.
public sealed class CommandEvolutionTests
{
    [Fact]
    public void AddedCommandUsesDefaultAndChangedPayloadKeepsTheOriginalRevisionRules()
    {
        var overrides = new Dictionary<Type, TimeSpan>
        {
            [typeof(ReadValue)] = TimeSpan.FromMilliseconds(900),
        };
        var original = new ExampleProfile("test.1", overrides);
        var updated = new ExampleProfile("test.2", overrides);
        Assert.Equal(TimeSpan.FromMilliseconds(900), updated.Describe(new ReadValue()).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), updated.Describe(new ReadLabel()).Timeout);
        Assert.Throws<NotSupportedException>(() => original.Describe(new ReadLabel()));
        Assert.Throws<NotSupportedException>(() => original.Encode(new ReadLabel(), 1));
        Assert.Throws<NotSupportedException>(() =>
            new ExampleProfile("test.3", overrides).Encode(new ReadValue(), 1)
        );

        Assert.Equal(new byte[] { 0 }, original.Encode(new ReadValue(), 1).Payload.ToArray());
        Assert.Equal(new byte[] { 0, 0 }, updated.Encode(new ReadValue(), 1).Payload.ToArray());
        Assert.Equal(7, original.Decode(new(1, 0xA0, new byte[] { 7 })).Value);
        Assert.Equal(263, updated.Decode(new(1, 0xA0, new byte[] { 7, 1 })).Value);
        Assert.Throws<ProtocolDecodingException>(() =>
            original.Decode(new(1, 0xA0, new byte[] { 7, 1 }))
        );
        Assert.Throws<ProtocolDecodingException>(() =>
            updated.Decode(new(1, 0xA0, new byte[] { 7 }))
        );
        Assert.Equal(new byte[] { 0 }, original.Encode(new ReadValue(), 1).Payload.ToArray());
    }

    private sealed record ReadValue : IDeviceRequest<int>;

    private sealed record ReadLabel : IDeviceRequest<int>;

    private sealed class ExampleProfile(
        string revision,
        IReadOnlyDictionary<Type, TimeSpan> overrides
    ) : IDeviceProtocolProfile
    {
        private static readonly ProtocolCommandCatalog Catalog = new([
            new(
                typeof(ReadValue),
                TimeSpan.FromSeconds(1),
                new HashSet<string> { "test.1", "test.2" }
            )
            {
                IntroducedIn = "test.1",
                DisplayName = "Fictional value",
            },
            new(typeof(ReadLabel), TimeSpan.FromSeconds(3), new HashSet<string> { "test.2" })
            {
                IntroducedIn = "test.2",
                DisplayName = "Fictional label",
            },
        ]);
        public string Version => revision;

        public CommandDescriptor Describe(IDeviceCommand command) =>
            new(
                command.GetType(),
                CommunicationPattern.RequestResponse,
                Catalog.GetTimeout(command.GetType(), revision, overrides),
                true,
                command is ReadLabel ? (byte)0xA1 : (byte)0xA0,
                typeof(int)
            );

        public WireMessage Encode(IDeviceCommand command, byte index)
        {
            Describe(command); // Explicit support check precedes any output.
            return new(
                index,
                command is ReadLabel ? (byte)0x21 : (byte)0x20,
                revision == "test.1" ? new byte[] { 0 } : new byte[] { 0, 0 }
            );
        }

        public DecodedProtocolMessage Decode(WireMessage message)
        {
            var expectedLength = revision == "test.1" ? 1 : 2;
            if (message.Payload.Length != expectedLength)
                throw new ProtocolDecodingException("Test payload length mismatch.");
            return new(
                ProtocolMessageKind.Response,
                message.Index,
                message.Command,
                revision == "test.1"
                    ? (int)message.Payload.Span[0]
                    : (int)BinaryPrimitives.ReadUInt16LittleEndian(message.Payload.Span)
            );
        }
    }
}
