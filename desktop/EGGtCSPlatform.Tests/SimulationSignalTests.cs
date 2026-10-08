using System;
using System.Diagnostics;
using System.Linq;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.DeviceSdk.Simulation;
using EGGtCSPlatform.Services;
using Xunit;
using Xunit.Abstractions;

namespace EGGtCSPlatform.Tests;

public sealed class SimulationSignalTests(ITestOutputHelper output)
{
    private static SimulationProvenance Source(int seed = 42) =>
        new(Guid.Empty, seed, 1, "", DateTimeOffset.UnixEpoch);

    private static double[] Samples(
        SimulationProvenance source,
        int channel,
        int count = 8000,
        int rate = 250
    ) =>
        Enumerable
            .Range(0, count)
            .Select(i => SimulationSignal.Eeg(source, channel, i, rate))
            .ToArray();

    [Theory]
    [InlineData(42)]
    [InlineData(20260910)]
    [InlineData(-17)]
    public void SignalHasBroadbandBackgroundRhythmPeakAndPartialChannelCorrelation(int seed)
    {
        var source = Source(seed);
        var a = Samples(source, 1);
        var b = Samples(source, 2);
        var rms = Math.Sqrt(a.Average(x => x * x));
        var correlation = Correlation(a, b);
        var low = BandPower(a, 2, 5);
        var high = BandPower(a, 25, 35);
        var alpha = BandPower(a, 8, 12);
        var beta = BandPower(a, 15, 23);
        output.WriteLine(
            $"seed={seed}: RMS={rms:F2} uV, correlation={correlation:F3}, low/high={low / high:F2}, alpha/beta={alpha / beta:F2}"
        );
        Assert.All(a, x => Assert.True(double.IsFinite(x)));
        Assert.InRange(rms, 5, 50);
        Assert.InRange(correlation, 0.05, 0.95);
        Assert.True(low > 3 * high, "Broadband power should fall with frequency.");
        Assert.True(
            alpha > 2 * beta,
            "The default dominant rhythm should form an alpha-band peak."
        );
        var windows = a.Chunk(1000).Select(w => Math.Sqrt(w.Average(x => x * x))).ToArray();
        Assert.True(windows.Max() / windows.Min() > 1.1, "Amplitude should vary over time.");
    }

    [Fact]
    public void SamplesAreIndependentOfReadOrderBlocksAndGroupLabels()
    {
        var source = Source();
        var continuous = Samples(source, 1, 1300);
        var chunked = Enumerable
            .Range(0, 1300)
            .Chunk(137)
            .SelectMany(chunk => chunk.Select(i => SimulationSignal.Eeg(source, 1, i, 250)))
            .ToArray();
        Assert.Equal(continuous, chunked);
        var reordered = source with
        {
            BatchId = Guid.NewGuid(),
            Group = "另一组",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
        foreach (var index in new[] { 1299, 0, 250, 249, 1000 })
            Assert.Equal(continuous[index], SimulationSignal.Eeg(reordered, 1, index, 250));
        Assert.NotEqual(
            continuous[200],
            SimulationSignal.Eeg(source with { SubjectIndex = 2 }, 1, 200, 250)
        );
        Assert.NotEqual(
            continuous[200],
            SimulationSignal.Eeg(source with { Seed = 43 }, 1, 200, 250)
        );
    }

    [Fact]
    public void NoiseAndAmplitudeControlsScaleTheirOwnComponents()
    {
        var source = Source() with { NoiseMicrovolts = 0 };
        foreach (var sample in new long[] { 0, 100, 5000, 10000000 })
        {
            var clean = SimulationSignal.Eeg(source, 1, sample, 500);
            Assert.Equal(
                2 * clean,
                SimulationSignal.Eeg(source with { EegAmplitudeMicrovolts = 50 }, 1, sample, 500),
                10
            );
            var noisy = SimulationSignal.Eeg(source with { NoiseMicrovolts = 5 }, 1, sample, 500);
            var noisier = SimulationSignal.Eeg(
                source with
                {
                    NoiseMicrovolts = 10,
                },
                1,
                sample,
                500
            );
            Assert.Equal(2 * (noisy - clean), noisier - clean, 10);
            Assert.Equal(clean, SimulationSignal.Eeg(source, 1, sample * 2, 1000), 10);
        }
    }

    [Theory]
    [InlineData("signal-v1", 0, -15.525793775854083)]
    [InlineData("signal-v1", 137, 10.245156012101539)]
    [InlineData("signal-v1", 5000, -7.8688861689259655)]
    [InlineData("signal-v1", 43200000, -13.438117754740595)]
    [InlineData("synthetic-v1", 0, -15.525793775854083)]
    [InlineData("synthetic-v1", 137, 10.245156012101539)]
    [InlineData("synthetic-v1", 5000, -7.8688861689259655)]
    [InlineData("synthetic-v1", 43200000, -13.438117754740595)]
    [InlineData("signal-v2", 0, 13.448719347701617)]
    [InlineData("signal-v2", 137, -13.40618747454949)]
    [InlineData("signal-v2", 5000, -15.387117569501864)]
    [InlineData("signal-v2", 43200000, 15.837698839078492)]
    public void ExplicitLegacyVersionsKeepTheirReferenceSamples(
        string version,
        long sample,
        double expected
    )
    {
        Assert.Equal(
            expected,
            SimulationSignal.Eeg(Source() with { AlgorithmVersion = version }, 3, sample, 500),
            8
        );
    }

    [Theory]
    [InlineData(250)]
    [InlineData(500)]
    public void LiveAndOfflineSamplesAgreeAcrossPacketSizesAndChannelSelections(int rate)
    {
        var source = Source();
        Assert.Equal("signal-v3", source.AlgorithmVersion);
        const int count = 1300;
        const long start = 1234567;
        var complete = SimulatedEggtCsDevice.CreateEegSampleBatches(
            new[] { 1, 3, 8 },
            rate,
            count,
            start
        );
        var channel = complete.Single(c => c.PhysicalChannel == 3);
        var reordered = SimulatedEggtCsDevice.CreateEegSampleBatches(
            new[] { 8, 3 },
            rate,
            count,
            start
        );
        Assert.Equal(channel.Samples, reordered.Single(c => c.PhysicalChannel == 3).Samples);
        var chunks = Enumerable
            .Range(0, count)
            .Chunk(37)
            .SelectMany(chunk =>
                SimulatedEggtCsDevice
                    .CreateEegSampleBatches(new[] { 3 }, rate, chunk.Length, start + chunk[0])[0]
                    .Samples
            );
        Assert.Equal(channel.Samples, chunks);
        Assert.Equal(start / (double)rate, channel.StartTimeSeconds);
        Assert.Equal(1d / rate, channel.SampleIntervalSeconds);
        for (var i = 0; i < count; i++)
            Assert.Equal(SimulationSignal.Eeg(source, 3, start + i, rate), channel.Samples[i]);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(20260910)]
    [InlineData(-17)]
    public void ContaminationHasOccasionalSmoothExcursionsAndHighFrequencyEnergy(int seed)
    {
        const int rate = 250;
        var source = Source(seed);
        var clean = Samples(source with { NoiseMicrovolts = 0 }, 3, 30000, rate);
        var noisy = Samples(source, 3, clean.Length, rate);
        var residual = noisy.Zip(clean, (a, b) => a - b).ToArray();
        var windows = residual.Chunk(rate).Select(w => Math.Sqrt(w.Average(x => x * x))).ToArray();
        output.WriteLine(
            $"Contamination seed={seed}: maximum/median window RMS={windows.Max() / windows.Order().ElementAt(windows.Length / 2):F2}"
        );
        Assert.True(windows.Max() > 2 * windows.Order().ElementAt(windows.Length / 2));
        Assert.True(BandPower(noisy, 45, 55) > BandPower(clean, 45, 55));
        Assert.True(BandPower(clean, 25, 35) > 0);
        // Look for deterministic packet/event-grid jumps in the smooth neural component.
        var differences = clean.Zip(clean.Skip(1), (a, b) => Math.Abs(b - a)).ToArray();
        var rmsDifference = Math.Sqrt(differences.Average(x => x * x));
        Assert.InRange(differences.Max() / rmsDifference, 1, 7);
        // The added transients must not introduce large one-sample steps, including
        // across the 6/9-second event grid; white measurement noise remains present.
        Assert.True(
            residual.Zip(residual.Skip(1), (a, b) => Math.Abs(b - a)).Max()
                < 5 * source.NoiseMicrovolts
        );
        Assert.All(noisy, value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void OutOfBandRhythmIsSuppressedRatherThanAliased()
    {
        // Both requested rhythms exceed the guard band at 100 Hz sampling and must
        // contribute exactly zero rather than fold into a spurious low-frequency peak.
        var source = Source() with
        {
            RhythmHz = 70,
            NoiseMicrovolts = 0,
        };
        var a = Samples(source, 1, 1000, 100);
        var b = Samples(source with { RhythmHz = 90 }, 1, 1000, 100);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Samples(source with { RhythmHz = 10 }, 1, 1000, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatedEegSignal.Sample(42, 1, 1, 0, 0));
    }

    [Fact]
    public void RhythmTapersSmoothlyNearTheSamplingLimit()
    {
        // Subtract an excluded rhythm to isolate the requested main rhythm from the
        // background and secondary bands without depending on private implementation.
        var source = Source() with
        {
            NoiseMicrovolts = 0,
            RhythmHz = 100,
        };
        var baseline = Samples(source, 1, 1000, 100);
        double Energy(double hz) =>
            Samples(source with { RhythmHz = hz }, 1, 1000, 100)
                .Zip(baseline, (a, b) => (a - b) * (a - b))
                .Average();
        var low = Energy(27);
        var middle = Energy(36);
        var high = Energy(42);
        Assert.True(low > middle && middle > high && high > 0);
        Assert.True(high < low * 0.01);
    }

    [Fact]
    public void GeneratorThroughputAndAllocationAtMaximumDeviceConfiguration()
    {
        var rate = DeviceCapabilities.Simulator.SupportedSampleRatesHz.Max();
        var channels = DeviceCapabilities.Simulator.MaximumEegChannels;
        // Warm up JIT before measuring. Report timing, but do not impose a flaky CI deadline.
        double checksum = 0;
        for (var i = 0; i < 2000; i++)
            checksum += SimulatedEegSignal.Sample(42, 1, 1, i, rate);
        var timer = new Stopwatch();
        var before = GC.GetAllocatedBytesForCurrentThread();
        timer.Start();
        for (var channel = 1; channel <= channels; channel++)
        for (var i = 0; i < rate * 5; i++)
            checksum += SimulatedEegSignal.Sample(42, 1, channel, i, rate);
        timer.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine(
            $"{channels} channels x {rate} Hz x 5 seconds: {timer.Elapsed.TotalSeconds:F3}s, {allocated} allocated bytes, checksum={checksum:R}"
        );
        Assert.True(double.IsFinite(checksum));
        Assert.Equal(0, allocated);
        // A seek to the end of the maximum 24-hour record does not replay or allocate history.
        before = GC.GetAllocatedBytesForCurrentThread();
        var last = SimulatedEegSignal.Sample(42, 1, channels, 86400L * rate - 1, rate);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(double.IsFinite(last));
        Assert.Equal(0, allocated);
    }

    private static double Correlation(double[] a, double[] b)
    {
        var ma = a.Average();
        var mb = b.Average();
        double covariance = 0,
            va = 0,
            vb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            covariance += (a[i] - ma) * (b[i] - mb);
            va += (a[i] - ma) * (a[i] - ma);
            vb += (b[i] - mb) * (b[i] - mb);
        }
        return covariance / Math.Sqrt(va * vb);
    }

    // Averaged, Hann-windowed periodograms; no new production DSP dependency.
    private static double BandPower(double[] samples, int startHz, int endHz)
    {
        const int window = 1000,
            rate = 250;
        double total = 0;
        var bins = 0;
        for (var offset = 0; offset + window <= samples.Length; offset += window / 2)
        for (var bin = startHz * window / rate; bin <= endHz * window / rate; bin++)
        {
            double real = 0,
                imaginary = 0;
            for (var i = 0; i < window; i++)
            {
                var value =
                    samples[offset + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (window - 1)));
                var angle = 2 * Math.PI * bin * i / window;
                real += value * Math.Cos(angle);
                imaginary += value * Math.Sin(angle);
            }
            total += real * real + imaginary * imaginary;
            bins++;
        }
        return total / bins;
    }
}
