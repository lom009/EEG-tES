using System.Buffers.Binary;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class EnvelopeProtocolTests
{
    private static EggtCsFrameCodec Codec() => new(new EggtCsUnverifiedFixedChecksum());

    private static EggtCsProtocolProfile Profile() =>
        new(EggtCsProtocolOptions.CreateTestDefaults());

    private static EnvelopeStimulationConfiguration Configuration(ushort delay = 100) =>
        new(delay, 3, 100, 500, new byte[] { 10, 20, 30, 40 });

    [Fact]
    public void ConfigurationMatchesCorrectedLongFrameAndPreservesSampleBytes()
    {
        var request = new ConfigureEnvelopeStimulationRequest(Configuration());
        var bytes = Codec().Encode(Profile().Encode(request, 1));
        Assert.Equal(Convert.FromHexString("AABB0115004A6400036400F40104000A141E28FFFF"), bytes);
        Assert.Equal(0.33m, EnvelopeStimulationConfiguration.ToMilliAmps(10));
        Assert.Equal(8.415m, EnvelopeStimulationConfiguration.ToMilliAmps(255));
        Assert.Equal(TimeSpan.FromMilliseconds(40), request.Configuration.Duration);
        Assert.False(Profile().Describe(request).IsIdempotent);
    }

    [Theory]
    [InlineData(RunControl.Start, "AACC01084D01FFFF")]
    [InlineData(RunControl.Stop, "AACC01084D02FFFF")]
    public void ControlUsesIndependentCommand(RunControl control, string expected)
    {
        var request = new ControlEnvelopeStimulationRequest(control);
        Assert.Equal(Convert.FromHexString(expected), Codec().Encode(Profile().Encode(request, 1)));
        Assert.Equal(
            (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse,
            Profile().Describe(request).ExpectedResponseCommand
        );
        Assert.False(Profile().Describe(request).IsIdempotent);
    }

    [Fact]
    public void ShortAckAndLongDataAndControlCanArriveInSingleByteFragments()
    {
        var longFrame = Codec()
            .Encode(Profile().Encode(new ConfigureEnvelopeStimulationRequest(Configuration()), 2));
        var bytes = Convert
            .FromHexString("AABB0108CA00FFFF")
            .Concat(longFrame)
            .Concat(Convert.FromHexString("AACC0308CD00FFFF"))
            .ToArray();
        var codec = Codec();
        var frames = new List<WireMessage>();
        foreach (var value in bytes)
            frames.AddRange(codec.Feed(new byte[] { value }, false));
        Assert.Equal(
            new byte[]
            {
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse,
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse,
            },
            frames.Select(f => f.Command)
        );
        Assert.True(((DeviceCommandResult)Profile().Decode(frames[0]).Value).IsSuccess);
        Assert.True(((DeviceCommandResult)Profile().Decode(frames[2]).Value).IsSuccess);
    }

    [Theory]
    [InlineData(0, DeviceCommandStatus.Success)]
    [InlineData(1, DeviceCommandStatus.NotConfigured)]
    [InlineData(2, DeviceCommandStatus.AlreadyRunning)]
    [InlineData(3, DeviceCommandStatus.AlreadyStopped)]
    [InlineData(255, DeviceCommandStatus.Failed)]
    public void ControlPreservesRawResult(byte code, DeviceCommandStatus expected)
    {
        var result = (DeviceCommandResult)
            Profile()
                .Decode(
                    new(
                        1,
                        (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse,
                        new byte[] { code }
                    )
                )
                .Value;
        Assert.Equal(expected, result.Status);
        Assert.Equal(code, result.RawResultCode);
        Assert.Throws<ProtocolDecodingException>(() =>
            Profile()
                .Decode(
                    new(1, (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse, new byte[2])
                )
        );
    }

    [Fact]
    public void LongFramesAndStructuralLimitsAreEnforced()
    {
        var samples = Enumerable.Repeat((byte)32, 1000).ToArray();
        var configuration = new EnvelopeStimulationConfiguration(300, 3, 100, 500, samples);
        samples[0] = 1;
        Assert.Equal(32, configuration.Samples.Span[0]);
        var bytes = Codec()
            .Encode(Profile().Encode(new ConfigureEnvelopeStimulationRequest(configuration), 1));
        Assert.Equal(1017, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(3)));
        Assert.Single(Codec().Feed(bytes, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnvelopeStimulationConfiguration(0, 0, 100, 0, new byte[1])
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnvelopeStimulationConfiguration(0, 3, 0, 0, new byte[1])
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnvelopeStimulationConfiguration(0, 3, 100, 0, new byte[ushort.MaxValue])
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnvelopeStimulationConfiguration(0, 3, 100, 0, Array.Empty<byte>())
        );
        var corrupt = Convert.FromHexString("AABB0108CA00FF00");
        Assert.NotNull(Assert.Single(Codec().FeedResults(corrupt, true)).Error);
    }

    [Fact]
    public void LongLengthWhoseHighByteMatchesCaIsNotMistakenForAck()
    {
        var configuration = new EnvelopeStimulationConfiguration(
            40,
            3,
            100,
            500,
            new byte[0xCA08 - 17]
        );
        var bytes = Codec()
            .Encode(Profile().Encode(new ConfigureEnvelopeStimulationRequest(configuration), 1));
        var codec = Codec();
        Assert.Empty(codec.Feed(bytes.AsSpan(0, 8), false));
        var frame = Assert.Single(codec.Feed(bytes.AsSpan(8), true));
        Assert.Equal((byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest, frame.Command);
        Assert.Equal(configuration.Samples.Length + 9, frame.Payload.Length);
    }

    [Fact]
    public async Task SimulationCancelsDelayedStartAndAllowsIndependentRestart()
    {
        await using var device = new SimulatedEggtCsDevice();
        var capability = device.EnvelopeStimulation;
        Assert.Equal(DeviceCommandStatus.NotConfigured, (await capability.StartAsync()).Status);
        Assert.True((await capability.ConfigureAsync(Configuration(300))).IsSuccess);
        Assert.True((await capability.StartAsync()).IsSuccess);
        Assert.Equal(DeviceCommandStatus.AlreadyRunning, (await capability.StartAsync()).Status);
        Assert.True((await capability.StopAsync()).IsSuccess);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        Assert.Equal(DeviceCommandStatus.AlreadyStopped, (await capability.StopAsync()).Status);
        await Task.Delay(350);
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        await capability.ConfigureAsync(Configuration(0));
        await using var events = device.SubscribeEvents();
        await events.Ready;
        Assert.True((await capability.StartAsync()).IsSuccess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = events.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.IsType<StimulationCompletedEvent>(reader.Current.Event);
    }

    [Fact]
    public async Task DisconnectCancelsPendingSimulation()
    {
        await using var device = new SimulatedEggtCsDevice();
        await device.EnvelopeStimulation.ConfigureAsync(Configuration(300));
        await device.EnvelopeStimulation.StartAsync();
        await device.DisconnectAsync();
        await Task.Delay(350);
        Assert.Equal(DeviceConnectionState.Disconnected, device.State.Connection);
        Assert.Equal(DeviceOperationState.Unknown, device.State.Operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.EnvelopeStimulation.StartAsync()
        );
    }

    [Fact]
    public async Task RunningSimulationStopsWithoutNaturalCompletionEvent()
    {
        await using var device = new SimulatedEggtCsDevice();
        await device.EnvelopeStimulation.ConfigureAsync(new(0, 3, 10, 500, new byte[10]));
        await using var events = device.SubscribeEvents();
        await events.Ready;
        await device.EnvelopeStimulation.StartAsync();
        await Task.Delay(20);
        Assert.Equal(DeviceOperationState.Stimulating, device.State.Operation);
        await device.EnvelopeStimulation.StopAsync();
        Assert.Equal(DeviceOperationState.Ready, device.State.Operation);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var reader = events.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.MoveNextAsync().AsTask()
        );
    }

    [Theory]
    [InlineData(DeviceCapabilitySource.Simulated)]
    [InlineData(DeviceCapabilitySource.Disabled)]
    public async Task MixedSourcesRespectStimulationSetting(DeviceCapabilitySource source)
    {
        var profile = DeviceBackendProfile.Simulation();
        profile.Sources[DeviceCapabilityKind.Stimulation] = source;
        await using var device = new MixedSourceDevice(
            new(new("mixed"), "test", null, null),
            profile
        );
        Assert.NotNull(device.Impedance);
        if (source == DeviceCapabilitySource.Disabled)
            Assert.Null(device.EnvelopeStimulation);
        else
        {
            Assert.True(
                (await device.EnvelopeStimulation!.ConfigureAsync(Configuration())).IsSuccess
            );
            Assert.True((await device.EnvelopeStimulation.StartAsync()).IsSuccess);
            Assert.True((await device.EnvelopeStimulation.StopAsync()).IsSuccess);
        }
    }
}
