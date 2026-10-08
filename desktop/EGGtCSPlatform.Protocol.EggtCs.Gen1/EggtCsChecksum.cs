using EGGtCSPlatform.Communication.Core;

namespace EGGtCSPlatform.Protocol.EggtCs.Gen1;

public sealed class EggtCsUnverifiedFixedChecksum(byte first = 0xFF, byte second = 0xFF) : IChecksum
{
    public int Size => 2;

    public bool IsVerifiedForPhysicalDevices => false;

    public void Write(ReadOnlySpan<byte> content, Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Checksum destination is too small.", nameof(destination));
        destination[0] = first;
        destination[1] = second;
    }

    public bool Validate(ReadOnlySpan<byte> content, ReadOnlySpan<byte> checksum) =>
        checksum.Length == Size && checksum[0] == first && checksum[1] == second;
}
