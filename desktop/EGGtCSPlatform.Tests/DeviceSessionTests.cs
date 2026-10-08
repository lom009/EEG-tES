using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceSessionTests
{
    [Fact]
    public async Task DeviceEventPreservesTransportReceiveTimestamp()
    {
        var (session, transport, codec) = await CreateSessionAsync();
        await using (session)
        {
            var receivedAt = DateTimeOffset.UnixEpoch.AddSeconds(123);
            const long receivedTimestamp = 456789;
            var rawPacket = codec.Encode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.StimulationProgress,
                    new byte[] { 0x05, 0x00, 0x50 }
                )
            );
            transport.Inject(rawPacket, receivedAt, receivedTimestamp);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var deviceEvent = await FirstAsync(
                session.ReadControlEventsAsync(timeout.Token),
                timeout.Token
            );

            Assert.Equal(receivedAt, deviceEvent.Timestamp);
            Assert.Equal(receivedTimestamp, deviceEvent.ReceivedTimestamp);
            Assert.Equal(rawPacket, deviceEvent.RawPacketData.ToArray());
        }
    }

    [Fact]
    public async Task DevicePushBetweenRequestAndResponseDoesNotCompletePendingRequest()
    {
        var (session, transport, codec) = await CreateSessionAsync();
        await using (session)
        {
            var responseTask = session.SendAsync(new ReadDeviceStatusRequest());
            var sent = await WaitForSentPacketAsync(transport);
            var index = sent[2];
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        index,
                        (byte)EggtCsCommandCode.StimulationProgress,
                        new byte[] { 0x05, 0x00, 0x50 }
                    )
                )
            );
            Assert.False(responseTask.IsCompleted);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        index,
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x64 }
                    )
                )
            );

            var response = await responseTask;
            Assert.Equal(DeviceOperationState.Ready, response.Operation);
            using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var deviceEvent = await FirstAsync(
                session.ReadControlEventsAsync(eventTimeout.Token),
                eventTimeout.Token
            );
            Assert.IsType<DeviceProgressEvent>(deviceEvent.Message);
        }
    }

    [Fact]
    public async Task TimeoutCleansPendingAndAllowsNextRequest()
    {
        var (session, transport, codec) = await CreateSessionAsync(TimeSpan.FromMilliseconds(50));
        await using (session)
        {
            await Assert.ThrowsAsync<RequestTimeoutException>(() =>
                session.SendAsync(new ReadDeviceStatusRequest())
            );

            var second = session.SendAsync(new ReadDeviceStatusRequest());
            var sent = await WaitForSentPacketAsync(transport, expectedCount: 2);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        sent[2],
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x32 }
                    )
                )
            );
            var response = await second;
            Assert.Equal(50, response.BatteryPercent);
        }
    }

    [Fact]
    public async Task DisconnectFailsPendingRequestWithoutAffectingAnotherSession()
    {
        var first = await CreateSessionAsync(TimeSpan.FromSeconds(2));
        var second = await CreateSessionAsync(TimeSpan.FromSeconds(2));
        await using (first.Session)
        await using (second.Session)
        {
            var firstRequest = first.Session.SendAsync(new ReadDeviceStatusRequest());
            var secondRequest = second.Session.SendAsync(new ReadDeviceStatusRequest());
            await WaitForSentPacketAsync(first.Transport);
            var secondSent = await WaitForSentPacketAsync(second.Transport);

            await first.Session.StopAsync();
            await Assert.ThrowsAsync<DeviceDisconnectedException>(() => firstRequest);

            second.Transport.Inject(
                second.Codec.Encode(
                    new WireMessage(
                        secondSent[2],
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x4D }
                    )
                )
            );
            var secondResponse = await secondRequest;
            Assert.Equal(77, secondResponse.BatteryPercent);
        }
    }

    [Fact]
    public async Task SameIndexIsIsolatedBySession()
    {
        var first = await CreateSessionAsync();
        var second = await CreateSessionAsync();
        await using (first.Session)
        await using (second.Session)
        {
            var firstRequest = first.Session.SendAsync(new ReadDeviceStatusRequest());
            var secondRequest = second.Session.SendAsync(new ReadDeviceStatusRequest());
            var firstSent = await WaitForSentPacketAsync(first.Transport);
            var secondSent = await WaitForSentPacketAsync(second.Transport);
            Assert.Equal(firstSent[2], secondSent[2]);

            first.Transport.Inject(
                first.Codec.Encode(
                    new WireMessage(
                        firstSent[2],
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x0B }
                    )
                )
            );
            second.Transport.Inject(
                second.Codec.Encode(
                    new WireMessage(
                        secondSent[2],
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x16 }
                    )
                )
            );

            Assert.Equal(11, (await firstRequest).BatteryPercent);
            Assert.Equal(22, (await secondRequest).BatteryPercent);
        }
    }

    [Fact]
    public async Task StatusResponseAcceptsSingleBatteryByteAndFixedResponseIndex()
    {
        var (session, transport, codec) = await CreateSessionAsync();
        await using (session)
        {
            var first = session.SendAsync(new ReadDeviceStatusRequest());
            var firstSent = await WaitForSentPacketAsync(transport);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        firstSent[2],
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x50 }
                    )
                )
            );
            Assert.Equal(80, (await first).BatteryPercent);

            var second = session.SendAsync(new ReadDeviceStatusRequest());
            var secondSent = await WaitForSentPacketAsync(transport, expectedCount: 2);
            Assert.NotEqual((byte)0x01, secondSent[2]);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        0x01,
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x4D }
                    )
                )
            );

            var response = await second;
            Assert.Equal(DeviceOperationState.Unknown, response.Operation);
            Assert.Equal(77, response.BatteryPercent);
        }
    }

    [Fact]
    public async Task AcquisitionResponseCompletesWhenEmbeddedIndexDiffersByDefault()
    {
        var (session, transport, codec) = await CreateSessionAsync();
        await using (session)
        {
            var acquisition = session.SendAsync(
                new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(5))
            );
            var sent = await WaitForSentPacketAsync(transport);
            var embeddedIndex = unchecked((byte)(sent[2] + 0x22));
            Assert.NotEqual(sent[2], embeddedIndex);

            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        embeddedIndex,
                        (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                        new byte[] { 0x00 }
                    )
                )
            );

            Assert.True((await acquisition).IsSuccess);
        }
    }

    [Fact]
    public async Task AcquisitionResponseRequiresSameIndexWhenConfigured()
    {
        var (session, transport, codec) = await CreateSessionAsync(
            requireMatchingResponseIndex: true
        );
        await using (session)
        {
            var acquisition = session.SendAsync(
                new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(5))
            );
            var sent = await WaitForSentPacketAsync(transport);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        unchecked((byte)(sent[2] + 1)),
                        (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            await Task.Delay(20);
            Assert.False(acquisition.IsCompleted);

            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        sent[2],
                        (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            Assert.True((await acquisition).IsSuccess);
        }
    }

    [Fact]
    public async Task IndexCorrelatedRequestsSendConcurrentlyAndCompleteOutOfOrder()
    {
        var (session, transport, codec) = await CreateSessionAsync(
            requireMatchingResponseIndex: true
        );
        await using (session)
        {
            var eeg = session.SendAsync(
                new ConfigureEegImpedanceRequest(MeasurementControl.Start, new HashSet<int> { 1 })
            );
            var stimulation = session.SendAsync(CreateStimulationImpedanceRequest());

            await WaitForSentPacketAsync(transport, expectedCount: 2);
            var packets = transport.SentPackets;
            var eegPacket = Assert.Single(
                packets.Where(packet =>
                    packet[4] == (byte)EggtCsCommandCode.ConfigureEegImpedanceRequest
                )
            );
            var stimulationPacket = Assert.Single(
                packets.Where(packet =>
                    packet[4] == (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest
                )
            );
            Assert.False(eeg.IsCompleted);
            Assert.False(stimulation.IsCompleted);

            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        eegPacket[2],
                        (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            await Task.Delay(10);
            Assert.False(stimulation.IsCompleted);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        stimulationPacket[2],
                        (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            Assert.True((await stimulation).IsSuccess);
            Assert.False(eeg.IsCompleted);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        eegPacket[2],
                        (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            Assert.True((await eeg).IsSuccess);
        }
    }

    [Fact]
    public async Task OneCorrelatedRequestTimeoutDoesNotAffectAnotherRequest()
    {
        var (session, transport, codec) = await CreateSessionAsync(TimeSpan.FromMilliseconds(80));
        await using (session)
        {
            var eeg = session.SendAsync(
                new ConfigureEegImpedanceRequest(MeasurementControl.Start, new HashSet<int> { 1 })
            );
            var stimulation = session.SendAsync(CreateStimulationImpedanceRequest());

            await WaitForSentPacketAsync(transport, expectedCount: 2);
            var stimulationPacket = Assert.Single(
                transport.SentPackets.Where(packet =>
                    packet[4] == (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest
                )
            );
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        stimulationPacket[2],
                        (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                        new byte[] { 0x00 }
                    )
                )
            );

            Assert.True((await stimulation).IsSuccess);
            await Assert.ThrowsAsync<RequestTimeoutException>(() => eeg);
            Assert.Equal(DeviceSessionState.Connected, session.State);
        }
    }

    [Fact]
    public async Task CommandOnlyStatusSerializesOnlyItsOwnCommandFamily()
    {
        var (session, transport, codec) = await CreateSessionAsync();
        await using (session)
        {
            var firstStatus = session.SendAsync(new ReadDeviceStatusRequest());
            var secondStatus = session.SendAsync(new ReadDeviceStatusRequest());
            var stimulation = session.SendAsync(CreateStimulationImpedanceRequest());

            await WaitForSentPacketAsync(transport, expectedCount: 2);
            var firstPackets = transport.SentPackets;
            Assert.Single(
                firstPackets.Where(packet =>
                    packet[4] == (byte)EggtCsCommandCode.ReadDeviceStatusRequest
                )
            );
            var stimulationPacket = Assert.Single(
                firstPackets.Where(packet =>
                    packet[4] == (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest
                )
            );

            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        stimulationPacket[2],
                        (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                        new byte[] { 0x00 }
                    )
                )
            );
            Assert.True((await stimulation).IsSuccess);
            Assert.False(firstStatus.IsCompleted);
            Assert.False(secondStatus.IsCompleted);

            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        0x01,
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x50 }
                    )
                )
            );
            Assert.Equal(80, (await firstStatus).BatteryPercent);
            var secondPacket = await WaitForSentPacketAsync(transport, expectedCount: 3);
            Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, secondPacket[4]);
            transport.Inject(
                codec.Encode(
                    new WireMessage(
                        0x01,
                        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                        new byte[] { 0x01, 0x4F }
                    )
                )
            );
            Assert.Equal(79, (await secondStatus).BatteryPercent);
        }
    }

    [Fact]
    public async Task HeartbeatFaultsSessionAfterConfiguredFailureWindow()
    {
        var transport = new FakeTransport();
        var session = new DeviceSession(
            transport,
            new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()),
            new EggtCsProtocolProfile(
                EggtCsProtocolOptions.CreateUniform(TimeSpan.FromMilliseconds(15))
            ),
            new EggtCsResponseMatcher(),
            new SerialRequestScheduler(),
            new DeviceSessionOptions
            {
                HeartbeatPolicy = new EggtCsHeartbeatPolicy(
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromMilliseconds(70)
                ),
            }
        );
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        session.Faulted += (_, args) => faulted.TrySetResult(args.Exception);

        await using (session)
        {
            await session.StartAsync();
            var exception = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var heartbeat = Assert.IsType<HeartbeatTimeoutException>(exception);
            Assert.Equal(session.SessionId, heartbeat.SessionId);
            Assert.Equal(DeviceSessionState.Faulted, session.State);
            Assert.NotEmpty(transport.SentPackets);
        }
    }

    private static async Task<(
        DeviceSession Session,
        FakeTransport Transport,
        EggtCsFrameCodec Codec
    )> CreateSessionAsync(TimeSpan? timeout = null, bool requireMatchingResponseIndex = false)
    {
        var transport = new FakeTransport();
        var checksum = new EggtCsUnverifiedFixedChecksum();
        var codec = new EggtCsFrameCodec(checksum);
        var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(
                EggtCsProtocolOptions.CreateTestDefaults(timeout, requireMatchingResponseIndex)
            ),
            new EggtCsResponseMatcher(),
            options: new DeviceSessionOptions { ControlEventCapacity = 4, DataEventCapacity = 4 }
        );
        await session.StartAsync();
        return (session, transport, codec);
    }

    private static async Task<byte[]> WaitForSentPacketAsync(
        FakeTransport transport,
        int expectedCount = 1
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (transport.SentPackets.Count < expectedCount)
            await Task.Delay(1, timeout.Token);
        return transport.SentPackets[expectedCount - 1];
    }

    private static ConfigureStimulationImpedanceRequest CreateStimulationImpedanceRequest() =>
        new(
            MeasurementControl.Start,
            new StimulationConfiguration(
                false,
                [
                    new StimulationChannel(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                    new StimulationChannel(1, 0.04m),
                ],
                StimulationWaveform.TDcs,
                StimulationDirection.Positive,
                0.1m,
                50,
                TimeSpan.Zero
            )
        );

    private static async Task<T> FirstAsync<T>(
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken
    )
    {
        await foreach (var item in source.WithCancellation(cancellationToken))
            return item;
        throw new InvalidOperationException("The stream completed without an item.");
    }
}
