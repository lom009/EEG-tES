using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Interfaces;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.SpeechTraining;
using EGGtCSPlatform.TestSupport;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class RuntimeEnvelopeTrainingTests
{
    [Theory]
    [InlineData(false, EggtCsRevisions.V101, false)]
    [InlineData(true, EggtCsRevisions.V101, false)]
    [InlineData(true, EggtCsRevisions.V102, false)]
    [InlineData(true, EggtCsRevisions.V101, true)]
    [InlineData(true, EggtCsRevisions.V102, true)]
    public async Task SimulatedStimulationCompletesTrainingThroughRuntime(
        bool mixed,
        string revision,
        bool batteryOnly
    )
    {
        await using var host = await Host.Create(mixed, revision, batteryOnly: batteryOnly);
        var binding = Assert.IsAssignableFrom<IDeviceProtocolBinding>(host.Device);
        Assert.Equal(
            mixed && revision == EggtCsRevisions.V102,
            binding.SupportsCommand(typeof(ConfigureEnvelopeStimulationRequest))
        );
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player);
        var channelMask = DeviceEnvelopeOutput
            .Settings(EnvelopeConfigurationTests.EnvelopeRoute())
            .ChannelMask;
        await session.ConfigureStimulationAsync(new(2, 300, 200, channelMask));

        async Task Finish(Task playback, int count, int frequency)
        {
            await Until(() => player.PlayCount == count || playback.IsCompleted);
            Assert.Null(session.Error);
            Assert.Equal(count, player.PlayCount);
            Assert.Equal(frequency, session.StimulusPlan!.Settings.DebugFrequencyHz);
            Assert.Equal(0x10, session.StimulusPlan.Settings.ChannelMask);
            Assert.False(session.CanAdjustStimulation);
            Assert.False(session.CanReplay);
            Assert.Equal(0, host.Pause.Calls);
            if (mixed)
                Assert.Equal(
                    DeviceCommandStatus.Success,
                    (await host.Device.ReadStatusAsync()).Status
                );
            session.RateAll(true);
            Assert.True(session.CanScore);
            Assert.False(session.CanSaveNext);
            var frozen = session.StimulusPlan;
            await session.ConfigureStimulationAsync(new(2, 0, 999, channelMask));
            Assert.Same(frozen, session.StimulusPlan);
            player.Complete();
            await playback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(session.Error);
            Assert.True(session.CanSaveNext);
            Assert.True(session.CanReplay);
            Assert.False(session.CanAdjustStimulation);
            Assert.Equal(DeviceOperationState.Ready, host.Device.State.Operation);
        }

        await Finish(session.StartAsync(), 1, 200);
        var waveform = session.Waveform;
        var samples = session.StimulusPlan!.Samples;
        await session.ConfigureStimulationAsync(new(2, 125, 500, channelMask));
        Assert.Same(waveform, session.Waveform);
        Assert.Equal(samples, session.StimulusPlan!.Samples);
        Assert.Equal(300, session.StimulusPlan.Settings.DelayMilliseconds);
        Assert.Equal(samples.Length / 100d + .3, session.StimulusPlan.DisplayDurationSeconds, 10);
        await Finish(session.ReplayAsync(), 2, 200);

        await session.ConfigureStimulationAsync(new(2, 0, 800, channelMask));
        await Finish(session.SaveNextAsync(), 3, 200);
        Assert.Equal(1, session.SentenceIndex);
        await session.ConfigureStimulationAsync(new(2, 300, 1000, channelMask));
        var interrupted = session.ReplayAsync();
        await Until(() => player.PlayCount == 4 || interrupted.IsCompleted);
        Assert.Equal(4, player.PlayCount);
        Assert.Equal(200, session.StimulusPlan!.Settings.DebugFrequencyHz);
        var stops = player.StopCount;
        await session.EndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await interrupted;
        Assert.True(player.StopCount > stops);
        Assert.True(session.Ended);
        Assert.False(session.IsOutputBusy);
        Assert.Single(session.Result!.Sentences);
        host.AssertSimulationIsolation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitCleanupStopsSimulationAndAllowsAnotherSession(bool mixed)
    {
        await using var host = await Host.Create(mixed, EggtCsRevisions.V101);
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player);
        var lifetime = new SpeechOutputLifetime();
        lifetime.Register(session);
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1 || playback.IsCompleted);
        Assert.Null(session.Error);
        Assert.Equal(1, player.PlayCount);
        var stops = player.StopCount;
        await lifetime.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await playback;
        Assert.True(player.StopCount > stops);
        Assert.False(session.IsOutputBusy);
        Assert.Equal(DeviceOperationState.Ready, host.Device.State.Operation);
        session.Dispose();

        var nextPlayer = new ControllableSpeechAudioPlayer();
        using var next = host.Session(nextPlayer);
        var restarted = next.StartAsync();
        await Until(() => nextPlayer.PlayCount == 1 || restarted.IsCompleted);
        Assert.Null(next.Error);
        Assert.Equal(1, nextPlayer.PlayCount);
        await next.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await restarted;
        host.AssertSimulationIsolation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledStimulationNeverFallsBackToAudio(bool mixed)
    {
        await using var host = await Host.Create(
            mixed,
            EggtCsRevisions.V102,
            DeviceCapabilitySource.Disabled
        );
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player);
        await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, player.PlayCount);
        Assert.Contains("未启用包络刺激", session.Error);
        Assert.False(session.CanSaveNext);
        host.AssertSimulationIsolation();
    }

    [Theory]
    [InlineData(EggtCsRevisions.V101)]
    [InlineData(EggtCsRevisions.V102)]
    public async Task RealStimulationStillRequiresEnvelopeProtocolSupport(string revision)
    {
        await using var host = await Host.Create(true, revision, DeviceCapabilitySource.Real);
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player);
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1 || playback.IsCompleted);
        if (revision == EggtCsRevisions.V101)
        {
            await playback;
            Assert.Equal(0, player.PlayCount);
            Assert.Contains("V102", session.Error);
            Assert.Empty(host.Transport.Frames.Where(IsEnvelope));
        }
        else
        {
            Assert.Null(session.Error);
            Assert.Equal(1, player.PlayCount);
            var configuration = Assert.Single(host.Transport.Frames.Where(f => f.Command == 0x4A));
            Assert.Equal(
                session.StimulusPlan!.Samples.ToArray(),
                configuration.Payload.Span[9..].ToArray()
            );
            player.Complete();
            await playback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(session.Error);
            Assert.Equal(
                new byte[] { 0x4A, 0x4D, 0x4D },
                host.Transport.Frames.Where(IsEnvelope).Select(f => f.Command)
            );
            Assert.True(host.Pause.Calls > 0);
        }
    }

    private static bool IsEnvelope(WireMessage frame) => frame.Command is 0x4A or 0x4D;

    [Theory]
    [InlineData(1, 5, 0x10)]
    [InlineData(5, 1, 0x01)]
    public async Task RouteMaskReachesActualRuntimeConfiguration(
        int fixedChannel,
        int selectableChannel,
        byte expected
    )
    {
        await using var host = await Host.Create(
            true,
            EggtCsRevisions.V102,
            DeviceCapabilitySource.Real
        );
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player, fixedChannel, selectableChannel);
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1 || playback.IsCompleted);
        Assert.Null(session.Error);
        Assert.Equal(1, player.PlayCount);
        var frame = Assert.Single(host.Transport.Frames.Where(f => f.Command == 0x4A));
        Assert.Equal(expected, session.StimulusPlan!.Settings.ChannelMask);
        Assert.Equal(expected, frame.Payload.Span[2]);
        Assert.Equal(0, frame.Payload.Span[2] & (1 << (fixedChannel - 1)));
        Assert.Equal(session.StimulusPlan.Samples.ToArray(), frame.Payload.Span[9..].ToArray());
        player.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task BatteryOnlyRealDeviceConfirmsStopThenConfiguresAndPlays(byte stopCode)
    {
        await using var host = await Host.Create(
            true,
            EggtCsRevisions.V102,
            DeviceCapabilitySource.Real,
            true,
            stopCode
        );
        Assert.Equal(DeviceOperationState.Unknown, host.Device.State.Operation);
        Assert.Equal(79, host.Device.State.BatteryPercent);
        var player = new ControllableSpeechAudioPlayer();
        using var session = host.Session(player);
        var playback = session.StartAsync();
        await Until(() => player.PlayCount == 1 || playback.IsCompleted);
        Assert.Null(session.Error);
        Assert.Equal(1, player.PlayCount);
        var frames = host.Transport.Frames.Where(IsEnvelope).ToArray();
        Assert.Equal(new byte[] { 0x4D, 0x4A, 0x4D }, frames.Select(f => f.Command));
        Assert.Equal(2, frames[0].Payload.Span[0]);
        Assert.Equal(1, frames[2].Payload.Span[0]);
        Assert.Equal(
            session.StimulusPlan!.Samples.ToArray(),
            frames[1].Payload.Span[9..].ToArray()
        );
        await host.Device.ReadStatusAsync();
        Assert.Equal(DeviceOperationState.Stimulating, host.Device.State.Operation);
        player.Complete();
        await playback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.Error);
        await host.Device.ReadStatusAsync();
        Assert.Equal(DeviceOperationState.Ready, host.Device.State.Operation);
        Assert.False(host.Pause.IsPaused(host.Device.Identity.DeviceId));
        var replay = session.ReplayAsync();
        await Until(() => player.PlayCount == 2 || replay.IsCompleted);
        Assert.Null(session.Error);
        Assert.Equal(2, player.PlayCount);
        player.Complete();
        await replay.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            new byte[] { 0x4D, 0x4A, 0x4D, 0x4D, 0x4A, 0x4D, 0x4D },
            host.Transport.Frames.Where(IsEnvelope).Select(f => f.Command)
        );
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public async Task BatteryOnlyRealDeviceRejectedStopNeverConfiguresOrPlays(byte stopCode)
    {
        await using var host = await Host.Create(
            true,
            EggtCsRevisions.V102,
            DeviceCapabilitySource.Real,
            true,
            stopCode
        );
        var player = new ControllableSpeechAudioPlayer();
        using (var session = host.Session(player))
        {
            await session.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("停止未确认", session.Error);
            Assert.Contains($"0x{stopCode:X2}", session.Error);
        }
        using (var recreated = host.Session(player))
        {
            await recreated.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("重新连接", recreated.Error);
        }
        Assert.Equal(0, player.PlayCount);
        Assert.Equal(0x4D, Assert.Single(host.Transport.Frames.Where(IsEnvelope)).Command);
        Assert.False(host.Pause.IsPaused(host.Device.Identity.DeviceId));
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Corpus : ISpeechCorpusProvider
    {
        public IReadOnlyList<SpeechCorpus> Corpora { get; } =
        [
            new(
                "fixture",
                Enumerable
                    .Range(1, 10)
                    .Select(i => new SpeechSentence($"s/{i}", i, "测试句", ""))
                    .ToArray()
            ),
        ];

        public AudioBuffer ReadAudio(SpeechSentence sentence)
        {
            var data = new byte[3200];
            for (var i = 0; i < 1600; i++)
                BinaryPrimitives.WriteInt16LittleEndian(
                    data.AsSpan(i * 2),
                    (short)(12000 * i / 1600d * Math.Sin(2 * Math.PI * 440 * i / 16000d))
                );
            return new(16000, 1, data);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public required IDeviceRuntime Runtime { get; init; }
        public required IEggtCsDevice Device { get; init; }
        public required RecordingTransport Transport { get; init; }
        public required TransportFactory Factory { get; init; }
        public required RecordingPause Pause { get; init; }
        public required bool Mixed { get; init; }

        public SpeechTrainingSession Session(
            ControllableSpeechAudioPlayer player,
            int fixedChannel = 1,
            int selectableChannel = 5
        )
        {
            var session = new SpeechTrainingSession(new Corpus(), player);
            session.AttachStimulation(
                new DeviceEnvelopeOutput(Runtime.Devices, Device.Identity.DeviceId.Value, Pause),
                DeviceEnvelopeOutput.Settings(
                    EnvelopeConfigurationTests.EnvelopeRoute(fixedChannel, selectableChannel)
                )
            );
            return session;
        }

        public static async Task<Host> Create(
            bool mixed,
            string revision,
            DeviceCapabilitySource source = DeviceCapabilitySource.Simulated,
            bool batteryOnly = false,
            byte stopCode = 0
        )
        {
            var profile = DeviceBackendProfile.Simulation();
            if (mixed)
            {
                profile.ConnectionSource = DeviceConnectionSource.Real;
                profile.Sources[DeviceCapabilityKind.Status] = DeviceCapabilitySource.Real;
            }
            profile.Sources[DeviceCapabilityKind.Stimulation] = source;
            var transport = new RecordingTransport
            {
                BatteryOnly = batteryOnly,
                StopCode = stopCode,
            };
            var factory = new TransportFactory(mixed, transport);
            var pause = new RecordingPause();
            var runtime = new DeviceRuntimeBuilder()
                .WithOptions(
                    new()
                    {
                        Backend = profile,
                        HeartbeatEnabled = false,
                        HeartbeatPause = pause,
                    }
                )
                .AddTransport(factory)
                .AddProtocol(
                    new EggtCsProtocolModule(
                        new()
                        {
                            AllowUnverifiedChecksum = true,
                            Protocol = EggtCsProtocolOptions.CreateTestDefaults(),
                        }
                    )
                )
                .Build();
            try
            {
                DeviceCandidate? candidate = null;
                if (mixed)
                    candidate = new(
                        new(new("physical"), "Fixture", null, null),
                        new("memory", "fixture", 1),
                        revision
                    )
                    {
                        ProtocolId = EggtCsProtocolModule.Id,
                        RevisionSource = ProtocolRevisionSource.Explicit,
                    };
                else
                    await foreach (var found in runtime.Discovery.DiscoverAsync(TimeSpan.Zero))
                        candidate = found;
                var device = await runtime.ConnectVerifiedAsync(candidate!);
                return new()
                {
                    Runtime = runtime,
                    Device = device,
                    Transport = transport,
                    Factory = factory,
                    Pause = pause,
                    Mixed = mixed,
                };
            }
            catch
            {
                await runtime.DisposeAsync();
                throw;
            }
        }

        public void AssertSimulationIsolation()
        {
            Assert.Equal(DeviceOperationState.Ready, Device.State.Operation);
            Assert.Equal(Mixed ? 1 : 0, Factory.Created);
            Assert.All(
                Transport.Frames,
                frame =>
                    Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, frame.Command)
            );
            if (Mixed)
                Assert.NotEmpty(Transport.Frames); // Physical status worked; stimulation stayed simulated.
            Assert.Equal(0, Pause.Calls);
            Assert.False(Pause.IsPaused(Device.Identity.DeviceId));
        }

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class RecordingPause : IDeviceHeartbeatPauseService
    {
        private readonly HeartbeatPauseService _inner = new();
        public int Calls;

        public IDisposable Pause(DeviceId id)
        {
            Interlocked.Increment(ref Calls);
            return _inner.Pause(id);
        }

        public bool IsPaused(DeviceId id) => _inner.IsPaused(id);
    }

    private sealed class TransportFactory(bool allowed, RecordingTransport transport)
        : ITransportFactory
    {
        public int Created;

        public bool CanCreate(ITransportEndpoint endpoint) =>
            !allowed || endpoint.Scheme == "memory";

        public ITransport Create(TransportEndpoint endpoint)
        {
            Interlocked.Increment(ref Created);
            if (!allowed)
                throw new InvalidOperationException("Pure simulation opened physical transport");
            return transport;
        }
    }

    // Actual Gen1 module/session/codec, in-memory wire only. No socket or device is accessed.
    private sealed class RecordingTransport : ITransport
    {
        public bool BatteryOnly { get; init; }
        public byte StopCode { get; init; }
        private readonly FakeTransport _inner = new();
        public IReadOnlyList<WireMessage> Frames =>
            _inner
                .SentPackets.SelectMany(bytes =>
                    new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()).Feed(bytes, true)
                )
                .ToArray();
        public TransportConnectionState State => _inner.State;

        public ValueTask ConnectAsync(CancellationToken token = default) =>
            _inner.ConnectAsync(token);

        public ValueTask DisconnectAsync(CancellationToken token = default) =>
            _inner.DisconnectAsync(token);

        public async ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken token = default
        )
        {
            await _inner.SendAsync(data, token);
            var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
            var request = Assert.Single(codec.Feed(data.Span, true));
            byte[] payload =
                request.Command == (byte)EggtCsCommandCode.ReadDeviceStatusRequest
                    ? (BatteryOnly ? [79] : [1, 100])
                : request.Command == 0x4D && request.Payload.Span[0] == 2 ? [StopCode]
                : [0];
            _inner.Inject(
                codec.Encode(new(request.Index, (byte)(request.Command | 0x80), payload))
            );
        }

        public IAsyncEnumerable<TransportPacket> ReceiveAsync(CancellationToken token = default) =>
            _inner.ReceiveAsync(token);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
