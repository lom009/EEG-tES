using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EGGtCSPlatform.SpeechTraining;

public static class TrainingNoiseOffset
{
    public static int Calculate(int seed, string sentenceId, int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        var bytes = Encoding.UTF8.GetBytes(sentenceId);
        var key = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(key, seed);
        bytes.CopyTo(key, 4);
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(key)) % (uint)frames);
    }
}
