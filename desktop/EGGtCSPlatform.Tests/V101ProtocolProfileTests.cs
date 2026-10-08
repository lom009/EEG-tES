using System;
using System.Collections.Generic;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class EggtCsProtocolProfileTests
{
    private readonly EggtCsProtocolProfile _profile = new(
        EggtCsProtocolOptions.CreateTestDefaults()
    );

    [Fact]
    public void MapsStatusCommandAndSemanticResponse()
    {
        var request = _profile.Encode(new ReadDeviceStatusRequest(), 7);
        var response = _profile.Decode(
            new WireMessage(
                7,
                (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                new byte[] { 0x02, 0x64 }
            )
        );

        Assert.Equal((byte)EggtCsCommandCode.ReadDeviceStatusRequest, request.Command);
        Assert.Equal(
            (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
            _profile.Describe(new ReadDeviceStatusRequest()).ExpectedResponseCommand
        );
        Assert.Equal(
            ResponseCorrelationMode.CommandOnly,
            _profile.Describe(new ReadDeviceStatusRequest()).ResponseCorrelation
        );
        var status = Assert.IsType<DeviceStatusResponse>(response.Value);
        Assert.Equal(DeviceOperationState.Acquiring, status.Operation);
        Assert.Equal(100, status.BatteryPercent);
    }

    [Fact]
    public void ResponseIndexComparisonDefaultsOffAndCanBeEnabled()
    {
        Assert.Equal(
            ResponseCorrelationMode.CommandOnly,
            _profile
                .Describe(new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(5)))
                .ResponseCorrelation
        );
        Assert.Equal(
            ResponseCorrelationMode.CommandOnly,
            _profile
                .Describe(new ControlStimulationRequest(RunControl.Start, TimeSpan.FromSeconds(5)))
                .ResponseCorrelation
        );

        var strict = new EggtCsProtocolProfile(
            EggtCsProtocolOptions.CreateTestDefaults(requireMatchingResponseIndex: true)
        );
        Assert.Equal(
            ResponseCorrelationMode.IndexAndCommand,
            strict
                .Describe(new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(5)))
                .ResponseCorrelation
        );
        Assert.Equal(
            ResponseCorrelationMode.IndexAndCommand,
            strict.Describe(new ReadDeviceStatusRequest()).ResponseCorrelation
        );
    }

    [Fact]
    public void EegChannelMaskUsesZeroForEnabledChannels()
    {
        var channels = new HashSet<int> { 1, 8, 9, 32 };
        var request = _profile.Encode(
            new ConfigureEegImpedanceRequest(MeasurementControl.Start, channels),
            1
        );
        var stop = _profile.Encode(
            new ConfigureEegImpedanceRequest(MeasurementControl.Stop, channels),
            2
        );

        Assert.Equal((byte)EggtCsCommandCode.ConfigureEegImpedanceRequest, request.Command);
        Assert.Equal(new byte[] { 0x01, 0x7E, 0xFE, 0xFF, 0x7F }, request.Payload.ToArray());
        Assert.Equal(new byte[] { 0x02, 0x7E, 0xFE, 0xFF, 0x7F }, stop.Payload.ToArray());
    }

    [Fact]
    public void EegImpedancePushRequiresExactlyThirtyThreeValidChannelStatuses()
    {
        var payload = new byte[33];
        payload[0] = 0x01;
        payload[8] = 0x02;
        payload[31] = 0x05;
        payload[32] = 0x04;

        var decoded = _profile.Decode(
            new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, payload)
        );

        var impedance = Assert.IsType<EegImpedanceReceivedEvent>(decoded.Value);
        Assert.Equal(33, impedance.Readings.Count);
        Assert.Equal(ImpedanceBand.UpTo10KOhms, impedance.Readings[0].Band);
        Assert.Equal(ImpedanceBand.UpTo20KOhms, impedance.Readings[8].Band);
        Assert.Equal(ImpedanceBand.Above40KOhms, impedance.Readings[31].Band);
        Assert.Equal(33, impedance.Readings[32].PhysicalChannel);
        Assert.Equal(ImpedanceBand.UpTo40KOhms, impedance.Readings[32].Band);

        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, new byte[32])
            )
        );
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, new byte[34])
            )
        );
        payload[12] = 0x06;
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(0x01, (byte)EggtCsCommandCode.EegImpedanceData, payload)
            )
        );
    }

    [Fact]
    public void DecodesInterleavedEegDataAsSignedBigEndianMicrovolts()
    {
        const int channelCount = 32;
        const int sampleCount = 2;
        var payload = new byte[3 + channelCount * sampleCount * 3 + 1];
        payload[0] = 0x0A;
        payload[1] = 0x00;
        payload[2] = channelCount;
        WriteSample(payload, sample: 0, channel: 0, 0x000001);
        WriteSample(payload, sample: 0, channel: 1, 0x7FFFFF);
        WriteSample(payload, sample: 1, channel: 0, 0xFFFFFF);
        WriteSample(payload, sample: 1, channel: 1, 0x800000);
        payload[^1] = 88;

        var decoded = _profile.Decode(
            new WireMessage(255, (byte)EggtCsCommandCode.EegData, payload, WireFrameKind.Data)
        );

        var eeg = Assert.IsType<EegDataPacketReceivedEvent>(decoded.Value);
        var scale = 2d * 4.5d / (12d * (1 << 24)) * 1_000_000d;
        Assert.Equal(EventDeliveryClass.Data, decoded.DeliveryClass);
        Assert.Equal(255, eeg.PacketIndex);
        Assert.Equal(TimeSpan.FromSeconds(10), eeg.ReportedRemaining);
        Assert.Equal(88, eeg.BatteryPercent);
        Assert.Equal(2, eeg.SampleCount);
        Assert.Equal(32, eeg.Channels.Count);
        Assert.Equal(scale, eeg.Channels[0].SamplesMicrovolts[0], 12);
        Assert.Equal(-scale, eeg.Channels[0].SamplesMicrovolts[1], 12);
        Assert.Equal(0x7FFFFF * scale, eeg.Channels[1].SamplesMicrovolts[0], 9);
        Assert.Equal(-0x800000 * scale, eeg.Channels[1].SamplesMicrovolts[1], 9);

        void WriteSample(byte[] destination, int sample, int channel, int raw)
        {
            var offset = 3 + (sample * channelCount + channel) * 3;
            destination[offset] = (byte)(raw >> 16);
            destination[offset + 1] = (byte)(raw >> 8);
            destination[offset + 2] = (byte)raw;
        }
    }

    [Fact]
    public void RejectsMalformedEegDataPayloads()
    {
        var wrongChannels = new byte[3 + 32 * 3 + 1];
        wrongChannels[2] = 31;
        var incompleteFrame = new byte[3 + 32 * 3 + 2];
        incompleteFrame[2] = 32;
        var tooManySamples = new byte[3 + 32 * 9 * 3 + 1];
        tooManySamples[2] = 32;

        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.EegData,
                    wrongChannels,
                    WireFrameKind.Data
                )
            )
        );
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.EegData,
                    incompleteFrame,
                    WireFrameKind.Data
                )
            )
        );
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.EegData,
                    tooManySamples,
                    WireFrameKind.Data
                )
            )
        );
    }

    [Fact]
    public void DecodesAcquisitionCompletedEvent()
    {
        var decoded = _profile.Decode(
            new WireMessage(
                1,
                (byte)EggtCsCommandCode.AcquisitionCompleted,
                ReadOnlyMemory<byte>.Empty
            )
        );

        Assert.IsType<AcquisitionCompletedEvent>(decoded.Value);
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    1,
                    (byte)EggtCsCommandCode.AcquisitionCompleted,
                    new byte[] { 0x00 }
                )
            )
        );
    }

    [Fact]
    public void DurationEncodingRequiresWholeSeconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _profile.Encode(
                new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromMilliseconds(1500)),
                1
            )
        );

        var request = _profile.Encode(
            new ControlAcquisitionRequest(RunControl.Start, TimeSpan.FromSeconds(60)),
            1
        );
        Assert.Equal(new byte[] { 0x01, 0x3C, 0x00 }, request.Payload.ToArray());
    }

    [Fact]
    public void EncodesManualStimulationStartAndStop()
    {
        var start = _profile.Encode(
            new ControlStimulationRequest(RunControl.Start, TimeSpan.FromSeconds(10)),
            1
        );
        var stop = _profile.Encode(
            new ControlStimulationRequest(RunControl.Stop, TimeSpan.FromSeconds(99)),
            2
        );

        Assert.Equal((byte)EggtCsCommandCode.ControlStimulationRequest, start.Command);
        Assert.Equal(new byte[] { 0x01, 0x0A, 0x00 }, start.Payload.ToArray());
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, stop.Payload.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _profile.Encode(
                new ControlStimulationRequest(RunControl.Start, TimeSpan.FromMilliseconds(1500)),
                3
            )
        );
    }

    [Theory]
    [InlineData(0x00, DeviceCommandStatus.Success)]
    [InlineData(0x01, DeviceCommandStatus.NotConfigured)]
    [InlineData(0x02, DeviceCommandStatus.AlreadyRunning)]
    [InlineData(0x03, DeviceCommandStatus.AlreadyStopped)]
    [InlineData(0x04, DeviceCommandStatus.CurrentRampingDown)]
    [InlineData(0x7F, DeviceCommandStatus.Failed)]
    public void DecodesEveryManualStimulationResponse(byte value, DeviceCommandStatus expected)
    {
        var decoded = _profile.Decode(
            new WireMessage(1, (byte)EggtCsCommandCode.ControlStimulationResponse, new[] { value })
        );

        Assert.Equal(expected, Assert.IsType<DeviceCommandResult>(decoded.Value).Status);
    }

    [Fact]
    public void DecodesStimulationProgressAndStrictEmptyCompletion()
    {
        var progressMessage = _profile.Decode(
            new WireMessage(
                1,
                (byte)EggtCsCommandCode.StimulationProgress,
                new byte[] { 0x0A, 0x00, 0x58 }
            )
        );
        var progress = Assert.IsType<DeviceProgressEvent>(progressMessage.Value);
        var completed = _profile.Decode(
            new WireMessage(
                2,
                (byte)EggtCsCommandCode.StimulationCompleted,
                ReadOnlyMemory<byte>.Empty
            )
        );

        Assert.Equal(DeviceOperationState.Stimulating, progress.Operation);
        Assert.Equal(TimeSpan.FromSeconds(10), progress.Remaining);
        Assert.Equal(88, progress.BatteryPercent);
        Assert.IsType<StimulationCompletedEvent>(completed.Value);
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    2,
                    (byte)EggtCsCommandCode.StimulationCompleted,
                    new byte[] { 0x00 }
                )
            )
        );
        Assert.Throws<ProtocolDecodingException>(() =>
            _profile.Decode(
                new WireMessage(
                    2,
                    (byte)EggtCsCommandCode.StimulationProgress,
                    new byte[] { 0x01, 0x00 }
                )
            )
        );
    }

    [Fact]
    public void EncodesDualChannelStimulationImpedanceExcludingFixedChannel()
    {
        var stimulation = new ConfigureStimulationImpedanceRequest(
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

        var request = _profile.Encode(stimulation, 9);

        Assert.Equal((byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest, request.Command);
        Assert.Equal(
            new byte[] { 0x01, 0x01, 0x01, 0x04, 0x01, 0x01, 0x00, 0x32, 0x00, 0x01 },
            request.Payload.ToArray()
        );
    }

    [Fact]
    public void EncodesHdStimulationImpedanceStopWithLittleEndianFrequency()
    {
        var request = _profile.Encode(
            new ConfigureStimulationImpedanceRequest(
                MeasurementControl.Stop,
                new StimulationConfiguration(
                    true,
                    [
                        new StimulationChannel(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                        new StimulationChannel(1, 0.04m),
                        new StimulationChannel(2, 0.08m),
                        new StimulationChannel(3, 0.12m),
                        new StimulationChannel(4, 0.16m),
                    ],
                    StimulationWaveform.TAcs,
                    StimulationDirection.Bidirectional,
                    10m,
                    50,
                    TimeSpan.Zero
                )
            ),
            3
        );

        Assert.Equal(
            new byte[]
            {
                0x02,
                0x04,
                0x01,
                0x04,
                0x02,
                0x08,
                0x03,
                0x0C,
                0x04,
                0x10,
                0x82,
                0x64,
                0x00,
                0x32,
                0x00,
                0x02,
            },
            request.Payload.ToArray()
        );
    }

    [Fact]
    public void EncodesCompleteTdcsConfigurationAndOnlyChangesControlWhenStopping()
    {
        var configuration = new StimulationConfiguration(
            false,
            [
                new StimulationChannel(7, 1.00m, DeviceStimulationChannelRole.FixedActive),
                new StimulationChannel(1, 1.00m),
            ],
            StimulationWaveform.TDcs,
            StimulationDirection.Negative,
            40m,
            79,
            TimeSpan.FromSeconds(7)
        );

        var start = _profile.Encode(
            new ConfigureStimulationImpedanceRequest(MeasurementControl.Start, configuration),
            1
        );
        var stop = _profile.Encode(
            new ConfigureStimulationImpedanceRequest(MeasurementControl.Stop, configuration),
            2
        );

        Assert.Equal(
            new byte[] { 0x01, 0x01, 0x01, 0x64, 0x41, 0x90, 0x01, 0x4F, 0x07, 0x01 },
            start.Payload.ToArray()
        );
        Assert.Equal(start.Payload.Span[..^1].ToArray(), stop.Payload.Span[..^1].ToArray());
        Assert.Equal((byte)0x02, stop.Payload.Span[^1]);
    }

    [Theory]
    [InlineData(StimulationWaveform.TDcs, StimulationDirection.Positive, 0x01)]
    [InlineData(StimulationWaveform.TAcs, StimulationDirection.Negative, 0x42)]
    [InlineData(StimulationWaveform.ShamDirect, StimulationDirection.Bidirectional, 0x83)]
    [InlineData(StimulationWaveform.TRns, StimulationDirection.Positive, 0x04)]
    [InlineData(StimulationWaveform.TPcs, StimulationDirection.Negative, 0x45)]
    [InlineData(StimulationWaveform.ShamAlternating, StimulationDirection.Bidirectional, 0x86)]
    public void EncodesEveryWaveformAndDirectionModeByte(
        StimulationWaveform waveform,
        StimulationDirection direction,
        byte expectedMode
    )
    {
        var request = _profile.Encode(
            new ConfigureStimulationImpedanceRequest(
                MeasurementControl.Start,
                CreateDualStimulationConfiguration(waveform, direction)
            ),
            1
        );

        Assert.Equal(expectedMode, request.Payload.Span[4]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void EncodesRampDurationBoundariesImmediatelyBeforeControl(int rampSeconds)
    {
        var configuration = CreateDualStimulationConfiguration(
            StimulationWaveform.TDcs,
            StimulationDirection.Positive
        ) with
        {
            RampDuration = TimeSpan.FromSeconds(rampSeconds),
        };

        var request = _profile.Encode(
            new ConfigureStimulationImpedanceRequest(MeasurementControl.Start, configuration),
            1
        );

        Assert.Equal((byte)rampSeconds, request.Payload.Span[^2]);
        Assert.Equal((byte)0x01, request.Payload.Span[^1]);
    }

    [Fact]
    public void RejectsInvalidFrequencyDutyAndRampFields()
    {
        var valid = CreateDualStimulationConfiguration(
            StimulationWaveform.TDcs,
            StimulationDirection.Positive
        );

        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(valid with { FrequencyHz = 0m }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Encode(valid with { FrequencyHz = 2500.1m })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(valid with { DutyPercent = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(valid with { DutyPercent = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Encode(valid with { RampDuration = TimeSpan.FromMilliseconds(500) })
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Encode(valid with { RampDuration = TimeSpan.FromSeconds(31) })
        );

        void Encode(StimulationConfiguration configuration) =>
            _profile.Encode(
                new ConfigureStimulationImpedanceRequest(MeasurementControl.Start, configuration),
                1
            );
    }

    [Fact]
    public void RejectsUnsupportedStimulationImpedanceShapes()
    {
        var invalidCurrent = new ConfigureStimulationImpedanceRequest(
            MeasurementControl.Start,
            new StimulationConfiguration(
                false,
                [
                    new StimulationChannel(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                    new StimulationChannel(1, 0.05m),
                ],
                StimulationWaveform.TDcs,
                StimulationDirection.Positive,
                0.1m,
                50,
                TimeSpan.Zero
            )
        );

        Assert.Throws<ArgumentOutOfRangeException>(() => _profile.Encode(invalidCurrent, 1));
    }

    [Fact]
    public void PhysicalCapabilitiesDoNotAdvertiseUnconfirmedFeatures()
    {
        Assert.True(EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.CanReadStatus);
        Assert.True(
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.CanCheckEegImpedance
        );
        Assert.True(
            EGGtCSPlatform
                .Protocol
                .EggtCs
                .Gen1
                .EggtCsCapabilities
                .Default
                .CanCheckStimulationImpedance
        );
        Assert.True(EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.CanAcquireEeg);
        Assert.Contains(
            250,
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.SupportedSampleRatesHz
        );
        Assert.Contains(
            500,
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.SupportedSampleRatesHz
        );
        Assert.False(
            EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.CanRunToleranceTest
        );
        Assert.True(EGGtCSPlatform.Protocol.EggtCs.Gen1.EggtCsCapabilities.Default.CanStimulate);
    }

    [Fact]
    public void MissingTimeoutOverridesUseCommandDefaults()
    {
        var options = new EggtCsProtocolOptions(
            new Dictionary<Type, TimeSpan>
            {
                [typeof(ReadDeviceStatusRequest)] = TimeSpan.FromSeconds(1),
            }
        );
        Assert.Equal(TimeSpan.FromSeconds(1), options.GetTimeout(typeof(ReadDeviceStatusRequest)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1500),
            options.GetTimeout(typeof(ControlAcquisitionRequest))
        );
        Assert.Throws<NotSupportedException>(() =>
            new EggtCsProtocolOptions(new Dictionary<Type, TimeSpan>(), revision: "1.0.2")
        );
    }

    private static StimulationConfiguration CreateDualStimulationConfiguration(
        StimulationWaveform waveform,
        StimulationDirection direction
    ) =>
        new(
            false,
            [
                new StimulationChannel(7, 0.04m, DeviceStimulationChannelRole.FixedActive),
                new StimulationChannel(1, 0.04m),
            ],
            waveform,
            direction,
            0.1m,
            50,
            TimeSpan.Zero
        );
}
