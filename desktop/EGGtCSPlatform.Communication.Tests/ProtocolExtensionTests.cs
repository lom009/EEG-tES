using System.Buffers.Binary;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class ProtocolExtensionTests
{
    [Fact]
    public async Task TypedAlternativeTransportCanCarryV101WithoutReadingUdpSettings()
    {
        var endpoint = new MemoryEndpoint("test-channel");
        var factory = new MemoryTransportFactory();
        await using var runtime = new DeviceRuntimeBuilder()
            .AddProtocol(
                new EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsProtocolModule(
                    new() { AllowUnverifiedChecksum = true }
                )
            )
            .AddTransport(factory)
            .WithOptions(
                new()
                {
                    ValidateConnection = false,
                    HeartbeatEnabled = false,
                    NetworkSettings = () =>
                        throw new InvalidOperationException("UDP settings should not be used."),
                }
            )
            .Build();
        var candidate = new DeviceCandidate(
            new(new("alternative-transport"), "Test", null, null),
            new("memory", "legacy-placeholder", 0),
            "1.0.1"
        )
        {
            ConnectionEndpoint = endpoint,
        };
        var device = await runtime.Devices.ConnectAsync(candidate);
        Assert.Same(endpoint, factory.CreatedEndpoint);
        var status = device.ReadStatusAsync();
        var sent = Assert.Single(factory.Transport.SentPackets);
        factory.Transport.Inject(
            new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()).Encode(
                new(sent[2], (byte)EggtCsCommandCode.ReadDeviceStatusResponse, new byte[] { 1, 91 })
            )
        );
        Assert.Equal(91, (await status).BatteryPercent);
    }

    private sealed record MemoryEndpoint(string Channel) : ITransportEndpoint
    {
        public string Scheme => "memory";
    }

    private sealed class MemoryTransportFactory : ITransportFactory
    {
        public FakeTransport Transport { get; } = new();
        public ITransportEndpoint? CreatedEndpoint { get; private set; }

        public bool CanCreate(ITransportEndpoint endpoint) => endpoint is MemoryEndpoint;

        public ITransport Create(TransportEndpoint endpoint) =>
            throw new NotSupportedException("Typed endpoint required.");

        public ITransport Create(ITransportEndpoint endpoint)
        {
            CreatedEndpoint = endpoint;
            return Transport;
        }
    }

    [Fact]
    public async Task MoreThan256RequestsUseProtocolOwned32BitKeysAndCompleteOutOfOrder()
    {
        var transport = new FakeTransport();
        await using var session = new DeviceSession(transport, new ExtendedProtocol());
        await session.StartAsync();
        var requests = Enumerable
            .Range(0, 300)
            .Select(value => session.SendAsync(new Request(value)))
            .ToArray();
        Assert.Equal(300, transport.SentPackets.Count);
        foreach (var packet in transport.SentPackets.Reverse())
        {
            var response = packet.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 0x84010001);
            transport.Inject(response);
        }
        Assert.Equal(Enumerable.Range(0, 300), await Task.WhenAll(requests));
    }

    private sealed record Request(int Value) : IDeviceRequest<int>;

    private sealed class ExtendedProtocol : ISessionProtocol
    {
        private uint _next = 0x10000;

        public CommandDescriptor Describe(IDeviceCommand command) =>
            new(
                command.GetType(),
                CommunicationPattern.RequestResponse,
                TimeSpan.FromSeconds(5),
                true,
                null,
                typeof(int)
            );

        public string AllocateCorrelationId(IReadOnlySet<string> pendingIds) =>
            (++_next).ToString();

        public string? GetSerializationKey(CommandDescriptor descriptor) => null;

        public ProtocolFrame Encode(IDeviceCommand command, string correlationId) =>
            new(correlationId, "0x00040001", BitConverter.GetBytes(((Request)command).Value));

        public byte[] EncodeFrame(ProtocolFrame frame)
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.Parse(frame.CorrelationId));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x00040001);
            frame.Payload.CopyTo(bytes.AsMemory(8));
            return bytes;
        }

        public IReadOnlyList<FrameParseResult> Feed(ReadOnlySpan<byte> bytes, bool endOfPacket) =>
            [
                new(
                    new(
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(),
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]).ToString("X8"),
                        bytes[8..].ToArray(),
                        bytes.ToArray()
                    )
                ),
            ];

        public ProtocolDecodedMessage Decode(ProtocolFrame frame) =>
            new(
                ProtocolMessageKind.Response,
                frame.CorrelationId,
                frame.MessageId,
                BitConverter.ToInt32(frame.Payload.Span)
            );

        public bool IsMatch(
            CommandDescriptor descriptor,
            ProtocolFrame request,
            ProtocolDecodedMessage response
        ) => request.CorrelationId == response.CorrelationId && response.MessageId == "84010001";

        public void Reset() { }
    }
}
