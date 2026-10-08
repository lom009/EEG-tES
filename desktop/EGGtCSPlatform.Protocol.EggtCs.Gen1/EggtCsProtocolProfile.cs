using System.Buffers.Binary;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsProtocolProfile(EggtCsProtocolOptions options) : IDeviceProtocolProfile
{
    public string Version => options.Revision;

    public CommandDescriptor Describe(IDeviceCommand command)
    {
        var type = command.GetType();
        if (!options.SupportsCommand(type))
            throw new NotSupportedException($"{type.Name} is not supported by {Version}.");
        return command switch
        {
            ReadDeviceStatusRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                typeof(DeviceStatusResponse),
                true
            ),
            ConfigureEegImpedanceRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse,
                typeof(DeviceCommandResult),
                false
            ),
            ControlAcquisitionRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ControlAcquisitionResponse,
                typeof(DeviceCommandResult),
                false
            ),
            ControlStimulationRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ControlStimulationResponse,
                typeof(DeviceCommandResult),
                false
            ),
            ConfigureEnvelopeStimulationRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse,
                typeof(DeviceCommandResult),
                false
            ),
            ControlEnvelopeStimulationRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse,
                typeof(DeviceCommandResult),
                false
            ),
            AdjustCurrentRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.AdjustCurrentResponse,
                typeof(CurrentAdjustmentResponse),
                false
            ),
            ConfigureStimulationImpedanceRequest => Descriptor(
                type,
                (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse,
                typeof(DeviceCommandResult),
                false
            ),
            _ => throw new NotSupportedException($"{type.Name} is not mapped by EggtCs {Version}."),
        };
    }

    public WireMessage Encode(IDeviceCommand command, byte index)
    {
        if (!options.SupportsCommand(command.GetType()))
            throw new NotSupportedException(
                $"{command.GetType().Name} is not supported by {Version}."
            );
        return command switch
        {
            ConfigureEnvelopeStimulationRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest,
                EncodeEnvelope(request.Configuration),
                WireFrameKind.Data
            ),
            ControlEnvelopeStimulationRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest,
                new byte[]
                {
                    request.Control switch
                    {
                        RunControl.Start => 1,
                        RunControl.Stop => 2,
                        _ => throw new ArgumentOutOfRangeException(nameof(request)),
                    },
                }
            ),
            ReadDeviceStatusRequest => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
                ReadOnlyMemory<byte>.Empty
            ),
            ConfigureEegImpedanceRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ConfigureEegImpedanceRequest,
                EncodeEegImpedance(request)
            ),
            ControlAcquisitionRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ControlAcquisitionRequest,
                EncodeRunControl(request.Control, request.Duration)
            ),
            ControlStimulationRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ControlStimulationRequest,
                EncodeRunControl(request.Control, request.Duration)
            ),
            AdjustCurrentRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.AdjustCurrentRequest,
                new[] { request.Adjustment == CurrentAdjustment.Increase ? (byte)0xAA : (byte)0x55 }
            ),
            ConfigureStimulationImpedanceRequest request => new WireMessage(
                index,
                (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
                EncodeStimulationImpedance(request)
            ),
            _ => throw new NotSupportedException(
                $"{command.GetType().Name} is not mapped by EggtCs {Version}."
            ),
        };
    }

    public DecodedProtocolMessage Decode(WireMessage message) =>
        message.Command switch
        {
            (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse => Response(
                message,
                DecodeEnvelopeResult(message.Payload.Span, false)
            ),
            (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse => Response(
                message,
                DecodeEnvelopeResult(message.Payload.Span, true)
            ),
            (byte)EggtCsCommandCode.ReadDeviceStatusResponse => Response(
                message,
                DecodeStatus(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse => Response(
                message,
                DecodeCommonResult(message.Payload.Span, CommonResultKind.Configuration)
            ),
            (byte)EggtCsCommandCode.ControlAcquisitionResponse => Response(
                message,
                DecodeAcquisitionResult(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.ControlStimulationResponse => Response(
                message,
                DecodeStimulationResult(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.AdjustCurrentResponse => Response(
                message,
                DecodeCurrentAdjustment(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse => Response(
                message,
                DecodeCommonResult(message.Payload.Span, CommonResultKind.Configuration)
            ),
            (byte)EggtCsCommandCode.EegImpedanceData => DeviceEventMessage(
                message,
                DecodeEegImpedance(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.StimulationImpedanceData => DeviceEventMessage(
                message,
                DecodeStimulationImpedance(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.AcquisitionCompleted => DeviceEventMessage(
                message,
                DecodeAcquisitionCompleted(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.StimulationProgress => DeviceEventMessage(
                message,
                DecodeStimulationProgress(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.StimulationCompleted => DeviceEventMessage(
                message,
                DecodeStimulationCompleted(message.Payload.Span)
            ),
            (byte)EggtCsCommandCode.EegData => DeviceEventMessage(
                message,
                DecodeEegData(message.Index, message.Payload.Span),
                EventDeliveryClass.Data
            ),
            _ => throw new ProtocolDecodingException(
                $"EggtCs {Version} command 0x{message.Command:X2} is unknown or ambiguous."
            ),
        };

    private CommandDescriptor Descriptor(
        Type type,
        byte responseCommand,
        Type responseType,
        bool idempotent,
        ResponseCorrelationMode responseCorrelation = ResponseCorrelationMode.IndexAndCommand
    ) =>
        new(
            type,
            CommunicationPattern.RequestResponse,
            options.GetTimeout(type),
            idempotent,
            responseCommand,
            responseType,
            options.RequireMatchingResponseIndex
                ? responseCorrelation
                : ResponseCorrelationMode.CommandOnly
        );

    private static byte[] EncodeEegImpedance(ConfigureEegImpedanceRequest request)
    {
        if (
            request.EnabledPhysicalChannels.Count == 0
            || request.EnabledPhysicalChannels.Any(channel => channel is < 1 or > 32)
        )
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "EEG physical channels must be between 1 and 32."
            );
        var payload = new byte[]
        {
            request.Control == MeasurementControl.Start ? (byte)0x01 : (byte)0x02,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
        };
        foreach (var channel in request.EnabledPhysicalChannels)
        {
            var zeroBased = channel - 1;
            payload[1 + zeroBased / 8] &= unchecked((byte)~(1 << (zeroBased % 8)));
        }
        return payload;
    }

    private static byte[] EncodeEnvelope(EnvelopeStimulationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var data = new byte[9 + configuration.Samples.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, configuration.DelayMilliseconds);
        data[2] = configuration.ChannelMask;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), configuration.SampleRateHz);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), configuration.DebugFrequencyHz);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(7),
            checked((ushort)configuration.Samples.Length)
        );
        configuration.Samples.Span.CopyTo(data.AsSpan(9));
        return data;
    }

    private static DeviceCommandResult DecodeEnvelopeResult(
        ReadOnlySpan<byte> payload,
        bool control
    )
    {
        RequireLength(
            payload,
            1,
            control
                ? (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse
                : (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse
        );
        var code = payload[0];
        var status = code switch
        {
            0 => DeviceCommandStatus.Success,
            1 when control => DeviceCommandStatus.NotConfigured,
            2 when control => DeviceCommandStatus.AlreadyRunning,
            3 when control => DeviceCommandStatus.AlreadyStopped,
            _ => DeviceCommandStatus.Failed,
        };
        return new DeviceCommandResult(
            status,
            status == DeviceCommandStatus.Failed ? $"Envelope command returned 0x{code:X2}." : null
        )
        {
            RawResultCode = code,
        };
    }

    private byte[] EncodeStimulationImpedance(ConfigureStimulationImpedanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Configuration);
        var configuration = request.Configuration;
        if (configuration.TargetGroups.Count != 1)
            throw new NotSupportedException(
                $"EggtCs {Version} stimulation impedance supports exactly one target group."
            );

        var group = configuration.TargetGroups[0];
        if (
            group.Channels.Count(channel =>
                channel.Role == DeviceStimulationChannelRole.FixedActive
            ) != 1
        )
            throw new ArgumentException(
                "Stimulation impedance requires exactly one fixed stimulation channel.",
                nameof(request)
            );
        var allPhysicalChannels = group
            .Channels.Select(channel => channel.PhysicalChannel)
            .ToArray();
        if (
            allPhysicalChannels.Any(channel => channel is < 1 or > 8)
            || allPhysicalChannels.Distinct().Count() != allPhysicalChannels.Length
        )
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Stimulation physical channels must be unique and between 1 and 8."
            );

        var selectableChannels = group
            .Channels.Where(channel => channel.Role == DeviceStimulationChannelRole.Selectable)
            .ToArray();
        if (
            selectableChannels.Length is < 1 or > 4
            || (!configuration.HighDefinition && selectableChannels.Length != 1)
            || (configuration.HighDefinition && selectableChannels.Length < 2)
        )
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Dual-channel impedance requires one selectable channel; HD requires two to four selectable channels."
            );

        var payload = new byte[8 + selectableChannels.Length * 2];
        payload[0] = configuration.HighDefinition ? (byte)0x02 : (byte)0x01;
        payload[1] = checked((byte)selectableChannels.Length);
        var offset = 2;
        foreach (var channel in selectableChannels)
        {
            payload[offset++] = checked((byte)channel.PhysicalChannel);
            payload[offset++] = EncodeCurrent(channel.CurrentMilliAmps);
        }

        var direction = configuration.Direction switch
        {
            StimulationDirection.Positive => 0,
            StimulationDirection.Negative => 1,
            StimulationDirection.Bidirectional => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        var waveform = configuration.Waveform switch
        {
            StimulationWaveform.TDcs => 1,
            StimulationWaveform.TAcs => 2,
            StimulationWaveform.ShamDirect => 3,
            StimulationWaveform.TRns => 4,
            StimulationWaveform.TPcs => 5,
            StimulationWaveform.ShamAlternating => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        payload[offset++] = checked((byte)((direction << 6) | waveform));

        var scaledFrequency = configuration.FrequencyHz * 10m;
        if (
            scaledFrequency is < 1m or > 25000m
            || scaledFrequency != decimal.Truncate(scaledFrequency)
        )
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Stimulation frequency must be 0.1-2500 Hz in 0.1 Hz steps."
            );
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(offset, 2),
            checked((ushort)scaledFrequency)
        );
        offset += 2;

        if (configuration.DutyPercent is < 1 or > 99)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Duty percent must be between 1 and 99."
            );
        payload[offset++] = checked((byte)configuration.DutyPercent);
        if (
            configuration.RampDuration < TimeSpan.Zero
            || configuration.RampDuration.TotalSeconds > 30
            || configuration.RampDuration.TotalSeconds
                != Math.Truncate(configuration.RampDuration.TotalSeconds)
        )
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Ramp duration must be a whole number of seconds from 0 to 30."
            );
        payload[offset++] = checked((byte)configuration.RampDuration.TotalSeconds);
        payload[offset] = request.Control switch
        {
            MeasurementControl.Start => 0x01,
            MeasurementControl.Stop => 0x02,
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return payload;
    }

    private static byte EncodeCurrent(decimal currentMilliAmps)
    {
        var scaled = currentMilliAmps * 100m;
        if (scaled is < 4m or > 200m || scaled != decimal.Truncate(scaled) || (int)scaled % 4 != 0)
            throw new ArgumentOutOfRangeException(
                nameof(currentMilliAmps),
                "Stimulation current must be 0.04-2.00 mA in 0.04 mA steps."
            );
        return checked((byte)scaled);
    }

    private byte[] EncodeRunControl(RunControl control, TimeSpan duration)
    {
        var seconds =
            control == RunControl.Start ? ToWholeSeconds(duration, nameof(duration)) : (ushort)0;
        var payload = new byte[3];
        payload[0] = control == RunControl.Start ? (byte)0x01 : (byte)0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), seconds);
        return payload;
    }

    private ushort ToWholeSeconds(TimeSpan value, string parameterName)
    {
        if (
            value <= TimeSpan.Zero
            || value.TotalSeconds > ushort.MaxValue
            || value.TotalSeconds != Math.Truncate(value.TotalSeconds)
        )
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"EggtCs {Version} durations must be whole seconds from 1 to 65535."
            );
        return checked((ushort)value.TotalSeconds);
    }

    private static DeviceStatusResponse DecodeStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 1)
        {
            return new DeviceStatusResponse(
                DeviceCommandStatus.Success,
                DeviceOperationState.Unknown,
                Math.Clamp(payload[0], (byte)0, (byte)100)
            );
        }
        RequireLength(payload, 2, (byte)EggtCsCommandCode.ReadDeviceStatusResponse);
        var operation = payload[0] switch
        {
            0x01 => DeviceOperationState.Ready,
            0x02 => DeviceOperationState.Acquiring,
            0x03 => DeviceOperationState.Stimulating,
            _ => DeviceOperationState.Unknown,
        };
        return new DeviceStatusResponse(
            DeviceCommandStatus.Success,
            operation,
            Math.Clamp(payload[1], (byte)0, (byte)100)
        );
    }

    private enum CommonResultKind
    {
        General,
        Configuration,
    }

    private static DeviceCommandResult DecodeCommonResult(
        ReadOnlySpan<byte> payload,
        CommonResultKind kind
    )
    {
        RequireLength(payload, 1, 0);
        var status = payload[0] switch
        {
            0x00 => DeviceCommandStatus.Success,
            0x01 when kind == CommonResultKind.Configuration =>
                DeviceCommandStatus.InvalidParameter,
            0x02 => DeviceCommandStatus.DeviceBusy,
            _ => DeviceCommandStatus.Failed,
        };
        return new DeviceCommandResult(status);
    }

    private static DeviceCommandResult DecodeAcquisitionResult(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 1, (byte)EggtCsCommandCode.ControlAcquisitionResponse);
        return new DeviceCommandResult(
            payload[0] switch
            {
                0x00 => DeviceCommandStatus.Success,
                0x02 => DeviceCommandStatus.DeviceBusy,
                0x03 => DeviceCommandStatus.NotConfigured,
                0x04 => DeviceCommandStatus.AlreadyRunning,
                _ => DeviceCommandStatus.Failed,
            }
        );
    }

    private static DeviceCommandResult DecodeStimulationResult(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 1, (byte)EggtCsCommandCode.ControlStimulationResponse);
        return new DeviceCommandResult(
            payload[0] switch
            {
                0x00 => DeviceCommandStatus.Success,
                0x01 => DeviceCommandStatus.NotConfigured,
                0x02 => DeviceCommandStatus.AlreadyRunning,
                0x03 => DeviceCommandStatus.AlreadyStopped,
                0x04 => DeviceCommandStatus.CurrentRampingDown,
                _ => DeviceCommandStatus.Failed,
            }
        );
    }

    private static CurrentAdjustmentResponse DecodeCurrentAdjustment(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 2, (byte)EggtCsCommandCode.AdjustCurrentResponse);
        var status = payload[0] switch
        {
            0x00 => DeviceCommandStatus.Success,
            0x01 => DeviceCommandStatus.InvalidParameter,
            0x02 => DeviceCommandStatus.MaximumReached,
            0x03 => DeviceCommandStatus.MinimumReached,
            0x04 => DeviceCommandStatus.NotConfigured,
            _ => DeviceCommandStatus.Failed,
        };
        return new CurrentAdjustmentResponse(status, payload[1] / 100m);
    }

    private static EegImpedanceReceivedEvent DecodeEegImpedance(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 33)
            throw new ProtocolDecodingException(
                "EEG impedance payload must contain exactly 33 channel status bytes."
            );
        var readings = payload
            .ToArray()
            .Select(
                (value, index) =>
                    new ImpedanceReading(
                        index + 1,
                        value switch
                        {
                            0x00 => ImpedanceBand.Disabled,
                            0x01 => ImpedanceBand.UpTo10KOhms,
                            0x02 => ImpedanceBand.UpTo20KOhms,
                            0x03 => ImpedanceBand.UpTo30KOhms,
                            0x04 => ImpedanceBand.UpTo40KOhms,
                            0x05 => ImpedanceBand.Above40KOhms,
                            _ => throw new ProtocolDecodingException(
                                $"Unknown EEG impedance status 0x{value:X2}."
                            ),
                        }
                    )
            )
            .ToArray();
        return new EegImpedanceReceivedEvent(readings);
    }

    private EegDataPacketReceivedEvent DecodeEegData(byte packetIndex, ReadOnlySpan<byte> payload)
    {
        const int channelCount = 32;
        const int bytesPerSample = 3;
        const int headerLength = 3;
        const int batteryLength = 1;
        const double referenceVoltage = 4.5d;
        const int gain = 12;
        const double rawToMicrovolts = 2d * referenceVoltage / (gain * (1 << 24)) * 1_000_000d;

        if (payload.Length < headerLength + batteryLength + channelCount * bytesPerSample)
            throw new ProtocolDecodingException(
                "EEG data payload is too short for one 32-channel sample."
            );
        if (payload[2] != channelCount)
            throw new ProtocolDecodingException(
                $"EEG data payload declared {payload[2]} channels; EggtCs {Version} requires 32."
            );
        var sampleBytes = payload.Length - headerLength - batteryLength;
        var bytesPerFrame = channelCount * bytesPerSample;
        if (sampleBytes <= 0 || sampleBytes % bytesPerFrame != 0)
            throw new ProtocolDecodingException(
                "EEG data payload length is not an exact multiple of a 32-channel sample frame."
            );

        var sampleCount = sampleBytes / bytesPerFrame;
        if (sampleCount > 8)
            throw new ProtocolDecodingException(
                "EEG data payload cannot contain more than 8 sample frames."
            );
        var payloadBytes = payload.ToArray();
        var channels = Enumerable
            .Range(0, channelCount)
            .Select(channel => new EegPacketChannelSamples(
                channel + 1,
                Enumerable
                    .Range(0, sampleCount)
                    .Select(sample =>
                        DecodeEegSample(
                            payloadBytes.AsSpan(
                                headerLength + (sample * channelCount + channel) * bytesPerSample,
                                bytesPerSample
                            )
                        ) * rawToMicrovolts
                    )
                    .ToArray()
            ))
            .ToArray();
        return new EegDataPacketReceivedEvent(
            TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt16LittleEndian(payload)),
            Math.Clamp(payload[^1], (byte)0, (byte)100),
            sampleCount,
            channels,
            packetIndex
        );
    }

    private static int DecodeEegSample(ReadOnlySpan<byte> sample)
    {
        var raw = sample[0] << 16 | sample[1] << 8 | sample[2];
        return (raw & 0x800000) == 0 ? raw : raw - 0x1000000;
    }

    private static AcquisitionCompletedEvent DecodeAcquisitionCompleted(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 0, (byte)EggtCsCommandCode.AcquisitionCompleted);
        return new AcquisitionCompletedEvent();
    }

    private static StimulationCompletedEvent DecodeStimulationCompleted(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 0, (byte)EggtCsCommandCode.StimulationCompleted);
        return new StimulationCompletedEvent();
    }

    private static StimulationImpedanceReceivedEvent DecodeStimulationImpedance(
        ReadOnlySpan<byte> payload
    )
    {
        if (payload.Length < 1 || payload.Length != 1 + payload[0] * 2)
            throw new ProtocolDecodingException(
                "Stimulation impedance payload does not match its channel count."
            );
        var readings = new ImpedanceReading[payload[0]];
        for (var index = 0; index < readings.Length; index++)
        {
            var offset = 1 + index * 2;
            readings[index] = new ImpedanceReading(
                payload[offset],
                payload[offset + 1] == 1 ? ImpedanceBand.Normal : ImpedanceBand.Abnormal
            );
        }
        return new StimulationImpedanceReceivedEvent(readings);
    }

    private static DeviceProgressEvent DecodeStimulationProgress(ReadOnlySpan<byte> payload)
    {
        RequireLength(payload, 3, (byte)EggtCsCommandCode.StimulationProgress);
        var remaining = TimeSpan.FromSeconds(BinaryPrimitives.ReadUInt16LittleEndian(payload));
        return new DeviceProgressEvent(
            DeviceOperationState.Stimulating,
            TimeSpan.Zero,
            remaining,
            0d,
            BatteryPercent: Math.Clamp(payload[2], (byte)0, (byte)100)
        );
    }

    private static DecodedProtocolMessage Response(WireMessage wire, object response) =>
        new(ProtocolMessageKind.Response, wire.Index, wire.Command, response);

    private static DecodedProtocolMessage DeviceEventMessage(
        WireMessage wire,
        DeviceEvent deviceEvent,
        EventDeliveryClass deliveryClass = EventDeliveryClass.Control
    ) => new(ProtocolMessageKind.DeviceEvent, wire.Index, wire.Command, deviceEvent, deliveryClass);

    private static void RequireLength(ReadOnlySpan<byte> payload, int expected, byte command)
    {
        if (payload.Length != expected)
            throw new ProtocolDecodingException(
                $"Command 0x{command:X2} payload length was {payload.Length}, expected {expected}."
            );
    }
}
