using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using EGGtCSPlatform.Services;
using EGGtCSPlatform.ViewModels;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class CommunicationDebugAssistantTests
{
    [Theory]
    [InlineData(
        "AABB0115004A6400036400F40104000A141E28FFFF",
        ByteTrafficDirection.Transmit,
        EggtCsCommandCode.ConfigureEnvelopeStimulationRequest,
        "包络刺激参数配置"
    )]
    [InlineData(
        "AABB0108CA00FFFF",
        ByteTrafficDirection.Receive,
        EggtCsCommandCode.ConfigureEnvelopeStimulationResponse,
        "包络刺激参数配置响应"
    )]
    [InlineData(
        "AACC01084D01FFFF",
        ByteTrafficDirection.Transmit,
        EggtCsCommandCode.ControlEnvelopeStimulationRequest,
        "包络刺激控制"
    )]
    [InlineData(
        "AACC0108CD00FFFF",
        ByteTrafficDirection.Receive,
        EggtCsCommandCode.ControlEnvelopeStimulationResponse,
        "包络刺激控制响应"
    )]
    public void InspectsEnvelopeFrames(
        string hex,
        ByteTrafficDirection direction,
        EggtCsCommandCode command,
        string name
    )
    {
        var result = V101UdpPacketInspector.Inspect(Convert.FromHexString(hex), direction);
        Assert.True(result.IsValidFrame);
        Assert.Equal((byte)command, result.Command);
        Assert.Equal(name, result.CommandName);
    }

    [Theory]
    [InlineData(
        ByteTrafficDirection.Transmit,
        (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
        "设备状态读取/心跳"
    )]
    [InlineData(
        ByteTrafficDirection.Receive,
        (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
        "设备状态/心跳应答"
    )]
    [InlineData(
        ByteTrafficDirection.Transmit,
        (byte)EggtCsCommandCode.ConfigureStimulationImpedanceRequest,
        "电刺激参数配置及阻抗检测控制"
    )]
    [InlineData(
        ByteTrafficDirection.Receive,
        (byte)EggtCsCommandCode.StimulationImpedanceData,
        "tES 电极连接质量周期数据"
    )]
    [InlineData(
        ByteTrafficDirection.Receive,
        (byte)EggtCsCommandCode.AcquisitionCompleted,
        "EEG 数据采集完成事件"
    )]
    public void InspectsControlFrameCommandsWithChineseNames(
        ByteTrafficDirection direction,
        byte command,
        string expectedName
    )
    {
        byte[] packet = [0xAA, 0xCC, 0x01, 0x07, command, 0xFF, 0xFF];

        var result = V101UdpPacketInspector.Inspect(packet, direction);

        Assert.True(result.IsValidFrame);
        Assert.Equal(command, result.Command);
        Assert.Equal(expectedName, result.CommandName);
    }

    [Fact]
    public void InspectsDataFramesAndKeepsUnknownOrMalformedPacketsVisible()
    {
        byte[] dataPacket = [0xAA, 0xBB, 0x01, 0x08, 0x00, 0x97, 0xFF, 0xFF];
        byte[] unknownPacket = [0xAA, 0xCC, 0x01, 0x07, 0x66, 0xFF, 0xFF];
        byte[] malformedPacket = [0xAA, 0xCC, 0x01, 0x09, 0x04, 0xFF, 0xFF];

        var data = V101UdpPacketInspector.Inspect(dataPacket, ByteTrafficDirection.Receive);
        var unknown = V101UdpPacketInspector.Inspect(unknownPacket, ByteTrafficDirection.Receive);
        var malformed = V101UdpPacketInspector.Inspect(
            malformedPacket,
            ByteTrafficDirection.Receive
        );

        Assert.Equal("EEG 数据包", data.CommandName);
        Assert.Equal("未知指令 0x66", unknown.CommandName);
        Assert.False(malformed.IsValidFrame);
        Assert.Null(malformed.Command);
        Assert.Equal("无法解析的 UDP 数据包", malformed.CommandName);
    }

    [Fact]
    public async Task DispatcherOnlyDuplicatesTrafficWhileObserverIsAttached()
    {
        var primary = new CapturingLogger();
        var observer = new CapturingLogger();
        var dispatcher = new ByteTrafficLogDispatcher(primary);
        var bytes = new byte[]
        {
            0xAA,
            0xCC,
            0x01,
            0x07,
            (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
            0xFF,
            0xFF,
        };

        dispatcher.Log(Entry(ByteTrafficDirection.Transmit, bytes));
        var subscription = dispatcher.Attach(observer);
        dispatcher.Log(Entry(ByteTrafficDirection.Receive, bytes));
        bytes[0] = 0x00;

        subscription.Dispose();
        dispatcher.Log(Entry(ByteTrafficDirection.Transmit, bytes));
        await dispatcher.DisposeAsync();

        Assert.Equal(3, primary.Entries.Count);
        Assert.Single(observer.Entries);
        Assert.Equal(0xAA, observer.Entries[0].Data.Span[0]);
    }

    [Fact]
    public void CaptureSessionKeepsLatestFiveHundredUdpPacketsAndIgnoresOtherTransports()
    {
        using var session = new UdpTrafficCaptureSession();
        for (var index = 0; index < 501; index++)
        {
            session.Log(
                Entry(
                    ByteTrafficDirection.Transmit,
                    [
                        0xAA,
                        0xCC,
                        0x01,
                        0x07,
                        (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
                        0xFF,
                        0xFF,
                    ],
                    timestamp: DateTimeOffset.UnixEpoch.AddMilliseconds(index)
                )
            );
        }
        session.Log(Entry(ByteTrafficDirection.Receive, [0x01, 0x02], transport: "TCP"));

        var batch = session.Drain();

        Assert.Equal(UdpTrafficCaptureSession.MaximumEntries, batch.Entries.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(1), batch.Entries[0].Timestamp);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(500), batch.Entries[^1].Timestamp);
        Assert.False(batch.Entries[0].IsHexTextCreated);
        Assert.Equal("AA CC 01 07 04 FF FF", batch.Entries[0].HexText);
        Assert.True(batch.Entries[0].IsHexTextCreated);
        Assert.Contains(
            "TX 设备状态读取/心跳 0x04 UDP 192.168.1.102:30307 · 7B",
            batch.Entries[0].CopyText
        );
    }

    [Fact]
    public void ViewModelFiltersDirectionsAndClearStartsAnEmptyGeneration()
    {
        using var session = new UdpTrafficCaptureSession();
        using var viewModel = new CommunicationDebugAssistantViewModel(session, startTimer: false);
        session.Log(
            Entry(
                ByteTrafficDirection.Transmit,
                [
                    0xAA,
                    0xCC,
                    0x01,
                    0x07,
                    (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
                    0xFF,
                    0xFF,
                ]
            )
        );
        session.Log(
            Entry(
                ByteTrafficDirection.Receive,
                [
                    0xAA,
                    0xCC,
                    0x01,
                    0x07,
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    0xFF,
                    0xFF,
                ]
            )
        );

        viewModel.FlushPending();
        Assert.Equal(2, viewModel.PacketCount);
        Assert.Equal(2, viewModel.VisibleEntries.Count);

        viewModel.ShowTransmit = false;
        Assert.Single(viewModel.VisibleEntries);
        Assert.Equal(ByteTrafficDirection.Receive, viewModel.VisibleEntries[0].Direction);

        viewModel.ClearCommand.Execute(null);
        Assert.Equal(0, viewModel.PacketCount);
        Assert.Empty(viewModel.VisibleEntries);
        Assert.False(viewModel.HasVisibleEntries);
    }

    [Fact]
    public void PausingCaptureDropsNewPacketsUntilCaptureResumes()
    {
        using var session = new UdpTrafficCaptureSession();
        using var viewModel = new CommunicationDebugAssistantViewModel(session, startTimer: false);

        session.Log(
            Entry(
                ByteTrafficDirection.Transmit,
                [
                    0xAA,
                    0xCC,
                    0x01,
                    0x07,
                    (byte)EggtCsCommandCode.ReadDeviceStatusRequest,
                    0xFF,
                    0xFF,
                ]
            )
        );
        viewModel.FlushPending();
        Assert.Single(viewModel.VisibleEntries);

        viewModel.ToggleCaptureCommand.Execute(null);
        Assert.True(viewModel.IsCapturePaused);
        Assert.True(session.IsPaused);
        Assert.Equal("继续记录", viewModel.CaptureButtonText);
        session.Log(
            Entry(
                ByteTrafficDirection.Receive,
                [
                    0xAA,
                    0xCC,
                    0x01,
                    0x07,
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    0xFF,
                    0xFF,
                ]
            )
        );
        viewModel.FlushPending();
        Assert.Single(viewModel.VisibleEntries);

        viewModel.ToggleCaptureCommand.Execute(null);
        Assert.False(viewModel.IsCapturePaused);
        Assert.False(session.IsPaused);
        Assert.Equal("暂停记录", viewModel.CaptureButtonText);
        session.Log(
            Entry(
                ByteTrafficDirection.Receive,
                [
                    0xAA,
                    0xCC,
                    0x01,
                    0x07,
                    (byte)EggtCsCommandCode.ReadDeviceStatusResponse,
                    0xFF,
                    0xFF,
                ]
            )
        );
        viewModel.FlushPending();
        Assert.Equal(2, viewModel.VisibleEntries.Count);
    }

    [Theory]
    [InlineData(Key.D, KeyModifiers.Control | KeyModifiers.Shift, true)]
    [InlineData(Key.D, KeyModifiers.Control, false)]
    [InlineData(Key.D, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt, false)]
    [InlineData(Key.F, KeyModifiers.Control | KeyModifiers.Shift, false)]
    public void ShortcutRequiresExactControlShiftD(
        Key key,
        KeyModifiers modifiers,
        bool expected
    ) =>
        Assert.Equal(expected, CommunicationDebugAssistantController.IsOpenGesture(key, modifiers));

    private static ByteTrafficLogEntry Entry(
        ByteTrafficDirection direction,
        byte[] bytes,
        string transport = "UDP",
        DateTimeOffset? timestamp = null
    ) =>
        new(
            timestamp ?? new DateTimeOffset(2026, 8, 25, 17, 30, 12, 345, TimeSpan.FromHours(8)),
            direction,
            transport,
            "192.168.1.102:30307",
            bytes
        );

    private sealed class CapturingLogger : IByteTrafficLogger
    {
        public List<ByteTrafficLogEntry> Entries { get; } = [];

        public void Log(ByteTrafficLogEntry entry) => Entries.Add(entry);
    }
}
