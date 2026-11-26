using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using EGGtCSPlatform.Audio.Core.Buffers;
using EGGtCSPlatform.Audio.Dsp.Envelope;
using EGGtCSPlatform.Audio.Dsp.Internal;

namespace EGGtCSPlatform.Audio.Dsp.Reverb;

public readonly record struct RoomPoint(double X, double Y, double Z);

public sealed record RirSettings(
    RoomPoint Room,
    RoomPoint Source,
    RoomPoint Listener,
    double T60 = .6,
    double LengthSeconds = .6,
    double EarlyLateMilliseconds = 50,
    bool Early = true,
    bool Late = true
)
{
    public static RirSettings Default { get; } = new(new(5, 4, 6), new(2, 3.5, 2), new(2, 1.5, 2));
    public const int SampleRate = 16000;
    public const double SoundSpeed = 340;
    public const string AlgorithmVersion = "image-source-1";
    public int Frames =>
        checked(
            (int)
                Math.Round(Math.Max(T60, LengthSeconds) * SampleRate, MidpointRounding.AwayFromZero)
        );
    public int DivisionFrames =>
        checked(
            (int)
                Math.Round(EarlyLateMilliseconds * SampleRate / 1000, MidpointRounding.AwayFromZero)
        );
    public double ReflectionCoefficient
    {
        get
        {
            if (T60 == 0)
                return 0;
            var area = 2 * (Room.X * Room.Y + Room.X * Room.Z + Room.Y * Room.Z);
            var absorption =
                24 * Math.Log(10) * Room.X * Room.Y * Room.Z / (SoundSpeed * area * T60);
            if (!double.IsFinite(absorption) || absorption > 1 || absorption <= 0)
                throw new ArgumentException("此房间的 T60 无法由均匀墙面吸声实现。");
            return Math.Sqrt(1 - absorption);
        }
    }

    public void Validate()
    {
        var values = new[]
        {
            Room.X,
            Room.Y,
            Room.Z,
            Source.X,
            Source.Y,
            Source.Z,
            Listener.X,
            Listener.Y,
            Listener.Z,
            T60,
            LengthSeconds,
            EarlyLateMilliseconds,
        };
        if (
            values.Any(v => !double.IsFinite(v))
            || Room.X <= 0
            || Room.Y <= 0
            || Room.Z <= 0
            || T60 < 0
            || LengthSeconds <= 0
            || EarlyLateMilliseconds < 0
        )
            throw new ArgumentException("RIR 参数必须是有效有限数值，房间尺寸和响应长度必须为正。");
        bool Inside(RoomPoint p) =>
            p.X > 0 && p.X < Room.X && p.Y > 0 && p.Y < Room.Y && p.Z > 0 && p.Z < Room.Z;
        if (!Inside(Source) || !Inside(Listener) || Source == Listener)
            throw new ArgumentException("声源和听者必须位于房间内部，且不能重合。");
        var length = Math.Max(T60, LengthSeconds) * SampleRate;
        if (length > 160000 || length < 1)
            throw new ArgumentException("RIR 计算预算为 1～160000 个采样点。");
        if (
            EarlyLateMilliseconds * SampleRate / 1000 < 11
            || EarlyLateMilliseconds * SampleRate / 1000 >= Frames
        )
            throw new ArgumentException("早晚分界必须在直达段之后且位于 RIR 内。");
        _ = ReflectionCoefficient;
        var distance = Frames * SoundSpeed / SampleRate;
        var candidates =
            8d
            * (2 * Math.Ceiling(distance / (2 * Room.X)) + 1)
            * (2 * Math.Ceiling(distance / (2 * Room.Y)) + 1)
            * (2 * Math.Ceiling(distance / (2 * Room.Z)) + 1);
        if (!double.IsFinite(candidates) || candidates > 20000000)
            throw new ArgumentException("RIR 候选镜像超过 2000 万计算预算，请缩短响应或增大房间。");
    }
}

public sealed record RirResponse(
    RirSettings Settings,
    ImmutableArray<double> Full,
    ImmutableArray<double> Effective,
    ImmutableArray<double> EarlyReference,
    int DirectFrame,
    int DivisionFrame,
    double EarlyCoefficientRms,
    string FullHash,
    string EffectiveHash
);

/// <summary>
/// Rectangular image-source formulation (Allen/Berkley, Peterson). Renders fractional
/// arrivals with an 8 ms Hann-sinc and a 100 Hz two-pole high-pass; no native runtime.
/// </summary>
public interface IRirGenerator
{
    RirResponse Generate(RirSettings settings, CancellationToken token = default);
}

public sealed class RirGenerator : IRirGenerator
{
    private readonly object _gate = new();
    private RirResponse? _cached;

    public RirResponse Generate(RirSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_cached?.Settings == settings)
                return _cached;
        const double fs = RirSettings.SampleRate,
            c = RirSettings.SoundSpeed;
        var h = new double[settings.Frames];
        var beta = settings.ReflectionCoefficient;
        var reach = h.Length * c / fs;
        var limits = new[] { settings.Room.X, settings.Room.Y, settings.Room.Z }
            .Select(v => (int)Math.Ceiling(reach / (2 * v)))
            .ToArray();
        for (var x = -limits[0]; x <= limits[0]; x++)
        for (var y = -limits[1]; y <= limits[1]; y++)
        for (var z = -limits[2]; z <= limits[2]; z++)
        {
            token.ThrowIfCancellationRequested();
            for (var corner = 0; corner < 8; corner++)
            {
                int px = corner & 1,
                    py = (corner >> 1) & 1,
                    pz = corner >> 2;
                var reflections =
                    Math.Abs(x - px)
                    + Math.Abs(x)
                    + Math.Abs(y - py)
                    + Math.Abs(y)
                    + Math.Abs(z - pz)
                    + Math.Abs(z);
                var reflectionGain = Math.Pow(beta, reflections);
                if (reflectionGain == 0)
                    continue;
                var dx =
                    2 * x * settings.Room.X
                    + (1 - 2 * px) * settings.Source.X
                    - settings.Listener.X;
                var dy =
                    2 * y * settings.Room.Y
                    + (1 - 2 * py) * settings.Source.Y
                    - settings.Listener.Y;
                var dz =
                    2 * z * settings.Room.Z
                    + (1 - 2 * pz) * settings.Source.Z
                    - settings.Listener.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                var arrival = distance / c * fs;
                if (arrival >= h.Length)
                    continue;
                var amplitude = reflectionGain / (4 * Math.PI * distance);
                var first = Math.Max(0, (int)Math.Floor(arrival) - 63);
                var last = Math.Min(h.Length - 1, (int)Math.Floor(arrival) + 64);
                for (var frame = first; frame <= last; frame++)
                {
                    var delta = frame - arrival;
                    var sinc =
                        Math.Abs(delta) < 1e-12 ? 1 : Math.Sin(Math.PI * delta) / (Math.PI * delta);
                    h[frame] += amplitude * sinc * .5 * (1 + Math.Cos(Math.PI * delta / 64));
                }
            }
        }
        var radius = Math.Exp(-2 * Math.PI * 100 / fs);
        var pole = 2 * radius * Math.Cos(2 * Math.PI * 100 / fs);
        double x1 = 0,
            x2 = 0,
            y1 = 0,
            y2 = 0;
        for (var i = 0; i < h.Length; i++)
        {
            var v = h[i];
            var filtered = v - (1 + radius) * x1 + radius * x2 + pole * y1 - radius * radius * y2;
            x2 = x1;
            x1 = v;
            y2 = y1;
            y1 = filtered;
            h[i] = filtered;
        }
        var peak = h.Max();
        if (!double.IsFinite(peak) || peak <= 0)
            throw new InvalidDataException("RIR 没有有效能量。");
        var direct = Array.IndexOf(h, peak);
        var division = checked(direct + settings.DivisionFrames);
        if (direct + 10 >= h.Length || division >= h.Length || division <= direct + 10)
            throw new ArgumentException("直达声或早晚分界超出 RIR 长度。");
        for (var i = 0; i < h.Length; i++)
            h[i] /= peak;
        var earlyRms = Math.Sqrt(h.Take(division + 1).Sum(v => v * v) / (division + 1));
        var effective = h.Take(settings.Late ? h.Length : division + 1).ToArray();
        if (!settings.Early)
            Array.Clear(effective, direct + 11, division - direct - 10);
        var early = effective.Take(division + 1).ToArray();
        var response = new RirResponse(
            settings,
            [.. h],
            [.. effective],
            [.. early],
            direct,
            division,
            earlyRms,
            Hash(h),
            Hash(effective)
        );
        token.ThrowIfCancellationRequested();
        lock (_gate)
            _cached = response;
        return response;
    }

    internal static string Hash(IEnumerable<double> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[8];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
