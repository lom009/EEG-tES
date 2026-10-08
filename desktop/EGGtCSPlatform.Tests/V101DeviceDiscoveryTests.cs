using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EggtCsDeviceDiscoveryTests
{
    [Fact]
    public async Task DiscoveryEncodesCallbackPortAndParsesRealIdentityAndEndpoint()
    {
        var response = CreateResponse(
            "ESP32_EEG",
            "EEG-6982A8",
            [0xB8, 0xF8, 0x62, 0x69, 0x82, 0xA8]
        );
        var transport = new StubDiscoveryTransport([
            new DiscoveryPacket(response, "192.168.1.101:30307"),
        ]);
        var discovery = new EggtCsDeviceDiscovery(
            transport,
            new EggtCsUnverifiedFixedChecksum(),
            callbackPort: 30302
        );

        var devices = await CollectAsync(discovery.DiscoverAsync(TimeSpan.FromSeconds(1)));

        var device = Assert.Single(devices);
        Assert.Equal(
            new byte[] { 0xAA, 0xCC, 0x01, 0x09, 0x01, 0x5E, 0x76, 0xFF, 0xFF },
            transport.Probe
        );
        Assert.Equal("ESP32_EEG", device.Identity.Model);
        Assert.Equal("EEG-6982A8", device.Identity.SerialNumber);
        Assert.Equal("B8:F8:62:69:82:A8", device.Identity.MacAddress);
        Assert.Equal("192.168.1.101", device.Endpoint.Address);
        Assert.Equal(30307, device.Endpoint.Port);
        Assert.Equal(EggtCsProtocolModule.Id, device.ProtocolId);
        Assert.Equal("1.0.1", device.ProtocolVersion);
        Assert.Equal(
            EGGtCSPlatform.DeviceSdk.ProtocolRevisionSource.SoftwareDefault,
            device.RevisionSource
        );
    }

    [Fact]
    public async Task DiscoveryIgnoresMalformedMacAndDuplicateSerialResponses()
    {
        var valid = CreateResponse("ESP32_EEG", "SERIAL-1", [1, 2, 3, 4, 5, 6]);
        var malformed = CreateResponse("ESP32_EEG", "SERIAL-2", [1, 2, 3, 4, 5]);
        var transport = new StubDiscoveryTransport([
            new DiscoveryPacket(malformed, "192.168.1.102:30307"),
            new DiscoveryPacket(valid, "192.168.1.101:30307"),
            new DiscoveryPacket(valid, "192.168.1.101:30307"),
        ]);
        var discovery = new EggtCsDeviceDiscovery(transport, new EggtCsUnverifiedFixedChecksum());

        var devices = await CollectAsync(discovery.DiscoverAsync(TimeSpan.FromSeconds(1)));

        var device = Assert.Single(devices);
        Assert.Equal("SERIAL-1", device.Identity.DeviceId.Value);
    }

    [Fact]
    public async Task EmptySerialFallsBackToMacForStableIdentity()
    {
        var response = CreateResponse(
            "ESP32_EEG",
            string.Empty,
            [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]
        );
        var discovery = new EggtCsDeviceDiscovery(
            new StubDiscoveryTransport([new DiscoveryPacket(response, "192.168.1.103:30307")]),
            new EggtCsUnverifiedFixedChecksum()
        );

        var device = Assert.Single(
            await CollectAsync(discovery.DiscoverAsync(TimeSpan.FromSeconds(1)))
        );

        Assert.Equal("AA:BB:CC:DD:EE:FF", device.Identity.DeviceId.Value);
    }

    internal static byte[] CreateResponse(string model, string serial, byte[] mac)
    {
        var payload = Encoding
            .UTF8.GetBytes(model)
            .Concat(new byte[] { 0 })
            .Concat(Encoding.UTF8.GetBytes(serial))
            .Concat(new byte[] { 0 })
            .Concat(mac)
            .ToArray();
        return new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()).Encode(
            new WireMessage(1, (byte)EggtCsCommandCode.DiscoverDeviceResponse, payload)
        );
    }

    private static async Task<IReadOnlyList<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private sealed class StubDiscoveryTransport(IReadOnlyList<DiscoveryPacket> packets)
        : IDiscoveryTransport
    {
        public byte[] Probe { get; private set; } = [];

        public async IAsyncEnumerable<DiscoveryPacket> DiscoverAsync(
            ReadOnlyMemory<byte> probe,
            TimeSpan window,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Probe = probe.ToArray();
            await Task.Yield();
            foreach (var packet in packets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return packet;
            }
        }
    }
}
