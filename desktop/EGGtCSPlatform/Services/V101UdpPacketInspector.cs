using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

namespace EGGtCSPlatform.Services;

public sealed record V101UdpPacketInspection(byte? Command, string CommandName, bool IsValidFrame)
{
    public string CommandText => Command is { } command ? $"0x{command:X2}" : "--";
}

public static class V101UdpPacketInspector
{
    private static readonly IReadOnlyDictionary<
        (ByteTrafficDirection Direction, byte Command),
        string
    > CommandNames = new Dictionary<(ByteTrafficDirection, byte), string>
    {
        [
            (
                ByteTrafficDirection.Transmit,
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationRequest
            )
        ] = "包络刺激参数配置",
        [
            (
                ByteTrafficDirection.Receive,
                (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse
            )
        ] = "包络刺激参数配置响应",
        [
            (
                ByteTrafficDirection.Transmit,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationRequest
            )
        ] = "包络刺激控制",
        [
            (
                ByteTrafficDirection.Receive,
                (byte)EggtCsCommandCode.ControlEnvelopeStimulationResponse
            )
        ] = "包络刺激控制响应",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.DiscoverDeviceRequest)] =
            "搜索设备",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.DiscoverDeviceResponse)] =
            "搜索设备应答",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.ReadDeviceStatusRequest)] =
            "设备状态读取/心跳",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.ReadDeviceStatusResponse)] =
            "设备状态/心跳应答",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.ConfigureEegImpedanceRequest)] =
            "EEG 通道选择及阻抗检测控制",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.ConfigureEegImpedanceResponse)] =
            "EEG 通道及阻抗配置响应",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.EegImpedanceData)] =
            "EEG 电极连接质量周期数据",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.ControlAcquisitionRequest)] =
            "EEG 数据采集控制",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.ControlAcquisitionResponse)] =
            "EEG 数据采集控制响应",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.EegData)] = "EEG 数据包",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.AcquisitionCompleted)] =
            "EEG 数据采集完成事件",
        [
            (
                ByteTrafficDirection.Transmit,
                (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest
            )
        ] = "电刺激参数配置及阻抗检测控制",
        [
            (
                ByteTrafficDirection.Receive,
                (byte)EggtCsCommandCode.ConfigureStimulationImpedanceResponse
            )
        ] = "电刺激参数配置响应",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.StimulationImpedanceData)] =
            "tES 电极连接质量周期数据",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.ControlStimulationRequest)] =
            "tES 刺激控制",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.ControlStimulationResponse)] =
            "tES 刺激控制响应",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.StimulationProgress)] =
            "tES 刺激进度周期数据",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.StimulationCompleted)] =
            "tES 刺激完成事件",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.AdjustCurrentRequest)] =
            "刺激电流调节",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.AdjustCurrentResponse)] =
            "刺激电流调节响应",
        [(ByteTrafficDirection.Transmit, (byte)EggtCsCommandCode.StartAutomaticExperimentRequest)] =
            "自动实验启动",
        [(ByteTrafficDirection.Receive, (byte)EggtCsCommandCode.StartAutomaticExperimentResponse)] =
            "自动实验启动响应",
    };

    public static V101UdpPacketInspection Inspect(
        ReadOnlySpan<byte> data,
        ByteTrafficDirection direction
    )
    {
        if (data.Length < 2 || data[0] != 0xAA)
            return Invalid();

        int commandOffset;
        int declaredLength;
        switch (data[1])
        {
            case 0xCC:
                if (data.Length < 7)
                    return Invalid();
                commandOffset = 4;
                declaredLength = data[3];
                break;
            case 0xBB:
                if (data.Length < 8)
                    return Invalid();
                if (
                    data.Length == 8
                    && data[3] == 8
                    && data[4] == (byte)EggtCsCommandCode.ConfigureEnvelopeStimulationResponse
                )
                {
                    commandOffset = 4;
                    declaredLength = data[3];
                    break;
                }
                commandOffset = 5;
                declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(data[3..5]);
                break;
            default:
                return Invalid();
        }

        if (declaredLength != data.Length || commandOffset >= data.Length - 2)
            return Invalid();
        var command = data[commandOffset];
        var name = CommandNames.TryGetValue((direction, command), out var knownName)
            ? knownName
            : $"未知指令 0x{command:X2}";
        return new V101UdpPacketInspection(command, name, true);
    }

    private static V101UdpPacketInspection Invalid() => new(null, "无法解析的 UDP 数据包", false);
}
