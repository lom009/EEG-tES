using System;

namespace EGGtCSPlatform.Services;

public sealed class EegAcquisitionOptions
{
    public const string SectionName = "EegAcquisition";

    public int SampleRateHz { get; set; } = 500;

    public TimeSpan DataPacketTimeout { get; set; } = TimeSpan.FromMilliseconds(2500);

    public bool EnablePacketReordering { get; set; } = true;

    public TimeSpan PacketReorderTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    public int PacketReorderWindowPackets { get; set; } = 16;

    public bool IsValid() =>
        SampleRateHz is 250 or 500
        && DataPacketTimeout > TimeSpan.Zero
        && PacketReorderTimeout > TimeSpan.Zero
        && PacketReorderWindowPackets is >= 1 and <= 127;
}
