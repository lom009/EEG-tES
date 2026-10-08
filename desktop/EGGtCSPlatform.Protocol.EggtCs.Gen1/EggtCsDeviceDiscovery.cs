using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsDeviceDiscovery(
    IDiscoveryTransport transport,
    IChecksum checksum,
    int callbackPort = 30302,
    string? revision = null
) : IDeviceDiscovery
{
    private readonly IDiscoveryTransport _transport = transport;
    private readonly IChecksum _checksum = checksum;
    private readonly int _callbackPort = callbackPort;
    private readonly string _revision = ValidateRevision(
        revision ?? EggtCsRevisions.DefaultRevision
    );

    private static string ValidateRevision(string revision) =>
        EggtCsRevisions.Supported.Contains(revision)
            ? revision
            : throw new NotSupportedException($"Unsupported EggtCs revision {revision}.");

    public async IAsyncEnumerable<DeviceCandidate> DiscoverAsync(
        TimeSpan window,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        if (
            _callbackPort is < 0 or > ushort.MaxValue
            || (_callbackPort == 0 && _transport is not IBoundDiscoveryTransport)
        )
            throw new InvalidOperationException("The Gen1 callback port must fit in two bytes.");
        var payload = new[]
        {
            unchecked((byte)_callbackPort),
            unchecked((byte)(_callbackPort >> 8)),
        };
        var encoder = new EggtCsFrameCodec(_checksum);
        var probe = encoder.Encode(
            new WireMessage(1, (byte)EggtCsCommandCode.DiscoverDeviceRequest, payload)
        );
        var seen = new HashSet<DeviceId>();
        var decoders = new Dictionary<string, EggtCsFrameCodec>(StringComparer.Ordinal);

        var packets = _transport is IBoundDiscoveryTransport bound
            ? bound.DiscoverAsync(
                port =>
                    encoder.Encode(
                        new WireMessage(
                            1,
                            (byte)EggtCsCommandCode.DiscoverDeviceRequest,
                            new byte[] { unchecked((byte)port), unchecked((byte)(port >> 8)) }
                        )
                    ),
                window,
                cancellationToken
            )
            : _transport.DiscoverAsync(probe, window, cancellationToken);
        await foreach (var packet in packets.ConfigureAwait(false))
        {
            IReadOnlyList<WireFrameParseResult> frames;
            try
            {
                if (!decoders.TryGetValue(packet.RemoteAddress, out var decoder))
                {
                    decoder = new EggtCsFrameCodec(_checksum);
                    decoders.Add(packet.RemoteAddress, decoder);
                }
                frames = decoder.FeedResults(packet.Data.Span, endOfPacket: true);
            }
            catch (CommunicationException)
            {
                continue;
            }
            foreach (var result in frames)
            {
                if (result.Frame is not { } frame)
                    continue;
                if (
                    frame.Command != (byte)EggtCsCommandCode.DiscoverDeviceResponse
                    || !TryParseIdentity(frame.Payload.Span, packet.RemoteAddress, out var identity)
                )
                    continue;
                if (!seen.Add(identity.DeviceId))
                    continue;
                var endpoint = ParseEndpoint(packet.RemoteAddress);
                yield return new DeviceCandidate(identity, endpoint, _revision)
                {
                    ProtocolId = EggtCsProtocolModule.Id,
                    RevisionSource = ProtocolRevisionSource.SoftwareDefault,
                };
            }
        }
    }

    private static bool TryParseIdentity(
        ReadOnlySpan<byte> payload,
        string remoteAddress,
        out DeviceIdentity identity
    )
    {
        identity = default!;
        var firstTerminator = payload.IndexOf((byte)0);
        if (firstTerminator <= 0)
            return false;
        var remainder = payload[(firstTerminator + 1)..];
        var secondTerminator = remainder.IndexOf((byte)0);
        if (secondTerminator < 0)
            return false;

        var model = Encoding.UTF8.GetString(payload[..firstTerminator]);
        var serial = Encoding.UTF8.GetString(remainder[..secondTerminator]);
        var macBytes = remainder[(secondTerminator + 1)..];
        if (macBytes.Length != 6)
            return false;
        var mac = string.Join(":", macBytes.ToArray().Select(value => value.ToString("X2")));
        var stableValue =
            !string.IsNullOrWhiteSpace(serial) ? serial
            : !string.IsNullOrWhiteSpace(mac) ? mac
            : remoteAddress;
        identity = new DeviceIdentity(new DeviceId(stableValue), model, serial, mac);
        return true;
    }

    private static TransportEndpoint ParseEndpoint(string remoteAddress)
    {
        if (IPEndPoint.TryParse(remoteAddress, out var endpoint))
            return new TransportEndpoint("udp", endpoint.Address.ToString(), endpoint.Port);
        var separator = remoteAddress.LastIndexOf(':');
        return separator > 0 && int.TryParse(remoteAddress[(separator + 1)..], out var port)
            ? new TransportEndpoint("udp", remoteAddress[..separator], port)
            : new TransportEndpoint("udp", remoteAddress, 30307);
    }
}
