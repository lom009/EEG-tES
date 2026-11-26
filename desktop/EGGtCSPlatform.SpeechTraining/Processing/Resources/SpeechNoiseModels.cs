using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EGGtCSPlatform.SpeechTraining;

public enum SpeechNoiseKind
{
    Ssn,
    Babble,
}

public sealed record SpeechNoiseMapping(
    string? Ssn = null,
    string? Babble = null,
    string? SsnError = null,
    string? BabbleError = null
)
{
    public string? File(SpeechNoiseKind kind) => kind == SpeechNoiseKind.Ssn ? Ssn : Babble;

    public string? Error(SpeechNoiseKind kind) =>
        kind == SpeechNoiseKind.Ssn ? SsnError : BabbleError;
}

public sealed record SpeechNoiseSettings(
    SpeechNoiseKind Kind,
    string FileName,
    string Sha256,
    double SnrDb,
    int Seed,
    double CalibrationRms
)
{
    public void Validate()
    {
        if (
            !Enum.IsDefined(Kind)
            || !double.IsFinite(SnrDb)
            || !double.IsFinite(CalibrationRms)
            || CalibrationRms <= 0
            || string.IsNullOrWhiteSpace(Sha256)
        )
            throw new ArgumentException("噪声参数无效。");
        FileSpeechNoiseProvider.ValidateFileName(FileName);
    }
}

public sealed record SpeechNoiseMetadata(
    SpeechNoiseSettings Settings,
    int OffsetFrames,
    int SampleRate,
    int Frames,
    double NormalizationGain,
    double PeakGain,
    double OutputRms
);

public sealed record SpeechNoiseResource(
    EGGtCSPlatform.Audio.Core.Buffers.AudioBuffer Audio,
    string Sha256
);

public interface ISpeechNoiseProvider
{
    SpeechNoiseResource Read(string fileName);
    double CalibrationRms { get; }
    TrainingResourceSnapshot? CalibrationResource => null;
}
