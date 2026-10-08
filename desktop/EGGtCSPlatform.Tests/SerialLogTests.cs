using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Crash;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class SerialLogTests
{
    [Fact]
    public async Task RoutesSixLevelsToOwnFileAndAllButKeepsBytesSeparate()
    {
        using var fixture = new Fixture();
        await using var log = fixture.Logger();
        foreach (var level in Enum.GetValues<ApplicationLogLevel>())
            log.Log(fixture.Entry(level));
        log.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
        log.Log(fixture.Traffic(ByteTrafficDirection.Receive));
        await log.FlushAsync();
        foreach (var level in Enum.GetValues<ApplicationLogLevel>())
            Assert.Single(File.ReadAllLines(fixture.PathFor(level.ToString().ToLowerInvariant())));
        Assert.Equal(6, File.ReadAllLines(fixture.PathFor("all")).Length);
        var bytes = File.ReadAllLines(fixture.PathFor("bytes"));
        Assert.Equal(2, bytes.Length);
        Assert.Contains(
            " | TX | UDP | remote=192.168.1.102:30307 | length=4 | AA CC 01 FF",
            bytes[0]
        );
        Assert.Contains(" | RX | UDP | ", bytes[1]);
        Assert.DoesNotContain("AA CC", File.ReadAllText(fixture.PathFor("all")));
    }

    [Fact]
    public async Task FilesAreLazyAndMinimumLevelDoesNotFilterBytes()
    {
        using var fixture = new Fixture();
        await using var log = fixture.Logger(new() { MinimumLevel = ApplicationLogLevel.Warning });
        await log.FlushAsync();
        Assert.False(Directory.Exists(fixture.DayPath));
        log.Log(fixture.Entry(ApplicationLogLevel.Info));
        log.Log(fixture.Entry(ApplicationLogLevel.Error));
        log.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
        await log.FlushAsync();
        Assert.False(File.Exists(fixture.PathFor("info")));
        Assert.Single(File.ReadAllLines(fixture.PathFor("all")));
        Assert.True(File.Exists(fixture.PathFor("bytes")));
        log.Configure(new() { Enabled = false });
        log.Log(fixture.Entry(ApplicationLogLevel.Fatal));
        log.Log(fixture.Traffic(ByteTrafficDirection.Receive));
        await log.FlushAsync();
        Assert.False(File.Exists(fixture.PathFor("fatal")));
        Assert.Single(File.ReadAllLines(fixture.PathFor("bytes")));
    }

    [Fact]
    public async Task RestartsAppendHighestSequenceAndRolloverNeverRenamesExistingFiles()
    {
        using var fixture = new Fixture();
        var options = new SerialLogOptions { MaxFileSizeBytes = 150 };
        await using (var log = fixture.Logger(options))
        {
            log.Log(fixture.Entry(ApplicationLogLevel.Info));
        }
        var original = File.ReadAllText(fixture.PathFor("info"));
        await using (var log = fixture.Logger(options))
        {
            log.Log(fixture.Entry(ApplicationLogLevel.Info));
            await log.FlushAsync();
            Assert.Equal(original + original, File.ReadAllText(fixture.PathFor("info")));
            for (var i = 0; i < 8; i++)
                log.Log(fixture.Entry(ApplicationLogLevel.Info));
            log.Log(fixture.Entry(ApplicationLogLevel.Error));
        }
        Assert.Equal(original + original, File.ReadAllText(fixture.PathFor("info")));
        Assert.True(File.Exists(fixture.PathFor("info", 5)));
        Assert.True(File.Exists(fixture.PathFor("error", 1)));
        Assert.False(File.Exists(fixture.PathFor("error", 2)));
    }

    [Fact]
    public async Task UsesNumericHighestIndexAndPreservesGaps()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.DayPath);
        File.WriteAllText(fixture.PathFor("info", 2), "old\n");
        File.WriteAllText(fixture.PathFor("info", 10), "latest\n");
        await using (var log = fixture.Logger())
            log.Log(fixture.Entry(ApplicationLogLevel.Info));
        Assert.StartsWith("latest\n", File.ReadAllText(fixture.PathFor("info", 10)));
        Assert.Equal("old\n", File.ReadAllText(fixture.PathFor("info", 2)));
        Assert.False(File.Exists(fixture.PathFor("info", 1)));
    }

    [Fact]
    public async Task OversizedEntryIsWholeAndDoesNotCreateEmptyFirstFile()
    {
        using var fixture = new Fixture();
        await using (var log = fixture.Logger(new() { MaxFileSizeBytes = 20 }))
        {
            log.Log(
                fixture.Entry(ApplicationLogLevel.Info) with
                {
                    Message = new string('字', 100),
                }
            );
            log.Log(fixture.Entry(ApplicationLogLevel.Info));
        }
        Assert.Contains(new string('字', 100), File.ReadAllText(fixture.PathFor("info")));
        Assert.True(new FileInfo(fixture.PathFor("info")).Length > 20);
        Assert.True(File.Exists(fixture.PathFor("info", 2)));
        Assert.False(File.Exists(fixture.PathFor("info", 3)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task LocalEventDateControlsFilesEvenWhenDequeuedAfterMidnight(int offsetHours)
    {
        using var fixture = new Fixture();
        fixture.Clock.OffsetHours = offsetHours;
        await using var log = fixture.Logger();
        var before = new DateTimeOffset(2026, 9, 21, 23, 59, 59, TimeSpan.FromHours(offsetHours));
        fixture.Clock.Now = before.AddSeconds(2);
        log.Log(fixture.Entry(ApplicationLogLevel.Info) with { Timestamp = before });
        log.Log(fixture.Entry(ApplicationLogLevel.Info) with { Timestamp = before.AddSeconds(2) });
        await log.FlushAsync();
        var previous = Path.Combine(fixture.Root, "2026-09-21", "info.1.log");
        var next = Path.Combine(fixture.Root, "2026-09-22", "info.1.log");
        Assert.Contains(
            before.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            File.ReadAllText(previous)
        );
        Assert.Contains(
            before
                .AddSeconds(2)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            File.ReadAllText(next)
        );
    }

    [Fact]
    public async Task RetentionKeepsThirtyDaysAndNeverDeletesLegacyOrUnknownFiles()
    {
        using var fixture = new Fixture();
        var old = Path.Combine(fixture.Root, "2026-08-22");
        var boundary = Path.Combine(fixture.Root, "2026-08-23");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(boundary);
        File.WriteAllText(Path.Combine(old, "info.1.log"), "expired");
        File.WriteAllText(Path.Combine(old, "other.1.log"), "unrelated");
        File.WriteAllText(Path.Combine(old, "info.notes.log"), "unrelated");
        File.WriteAllText(Path.Combine(old, "info.log"), "unrelated");
        File.WriteAllText(Path.Combine(boundary, "bytes.1.log"), "retained");
        File.WriteAllText(Path.Combine(fixture.Root, "BytesLog.log"), "legacy");
        await using var log = fixture.Logger();
        await log.FlushAsync();
        Assert.False(File.Exists(Path.Combine(old, "info.1.log")));
        Assert.True(File.Exists(Path.Combine(old, "other.1.log")));
        Assert.True(File.Exists(Path.Combine(old, "info.notes.log")));
        Assert.True(File.Exists(Path.Combine(old, "info.log")));
        Assert.True(File.Exists(Path.Combine(boundary, "bytes.1.log")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "BytesLog.log")));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        log.Log(fixture.Entry(ApplicationLogLevel.Info));
        await log.FlushAsync();
        Assert.False(Directory.Exists(boundary));
    }

    [Fact]
    public async Task FatalIncludesExceptionChainAndIsFlushedBeforeReturning()
    {
        using var fixture = new Fixture();
        await using var log = fixture.Logger();
        await log.LogAsync(
            new InvalidOperationException("outer", new ArgumentException("inner")),
            new GlobalExceptionContext("UI", fixture.Clock.Now, true)
        );
        var fatal = File.ReadAllText(fixture.PathFor("fatal"));
        Assert.Contains("GLOBAL_EXCEPTION", fatal);
        Assert.Contains("InvalidOperationException: outer", fatal);
        Assert.Contains("ArgumentException: inner", fatal);
        Assert.Equal(fatal, File.ReadAllText(fixture.PathFor("all")));
        Assert.False(File.Exists(fixture.PathFor("error")));
    }

    [Fact]
    public async Task SingleOutputFailureDoesNotDisableAllOrBytes()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.DayPath);
        Directory.CreateDirectory(fixture.PathFor("error"));
        await using var log = fixture.Logger();
        log.Log(fixture.Entry(ApplicationLogLevel.Error));
        log.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
        await log.FlushAsync();
        Assert.True(File.Exists(fixture.PathFor("all")));
        Assert.True(File.Exists(fixture.PathFor("bytes")));
    }

    [Fact]
    public async Task InvalidRootDoesNotThrowToCallersOrBlockFlush()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "occupied");
        File.WriteAllText(path, "file");
        await using var log = new SerialLog(new(), path, fixture.Clock);
        log.Log(fixture.Entry(ApplicationLogLevel.Error));
        log.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
        await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentOperationsAndTrafficAreDrainedOnDispose()
    {
        using var fixture = new Fixture();
        var log = fixture.Logger();
        await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        for (var i = 0; i < 50; i++)
                        {
                            log.Log(fixture.Entry(ApplicationLogLevel.Info));
                            log.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
                        }
                    })
                )
        );
        await log.DisposeAsync();
        await log.DisposeAsync();
        Assert.Equal(200, File.ReadAllLines(fixture.PathFor("all")).Length);
        Assert.Equal(200, File.ReadAllLines(fixture.PathFor("bytes")).Length);
    }

    [Fact]
    public async Task ExternallyOwnedLoggerSurvivesContainerDisposal()
    {
        using var fixture = new Fixture();
        await using var logger = fixture.Logger();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        EGGtCSPlatform.Bootstrap.Bootstrapper.RegisterCommonServices(
            services,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            logger
        );
        await using (
            var provider =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(
                    services
                )
        )
        {
            Assert.Same(logger, provider.GetService(typeof(IApplicationLogger)));
            _ = provider.GetService(
                typeof(System.Collections.Generic.IEnumerable<IGlobalExceptionLogger>)
            );
            var traffic = (IByteTrafficLogger)provider.GetService(typeof(IByteTrafficLogger))!;
            traffic.Log(fixture.Traffic(ByteTrafficDirection.Transmit));
        }
        logger.Log(fixture.Entry(ApplicationLogLevel.Info) with { EventName = "Application.Exit" });
        await logger.FlushAsync();
        Assert.Contains("Application.Exit", File.ReadAllText(fixture.PathFor("all")));
        Assert.True(File.Exists(fixture.PathFor("bytes")));
    }

    [Fact]
    public async Task StartupDoesNotDeleteHistoryBeforeReadingUserRetention()
    {
        using var fixture = new Fixture();
        var historical = Path.Combine(fixture.Root, "2026-08-01");
        Directory.CreateDirectory(historical);
        File.WriteAllText(Path.Combine(historical, "all.1.log"), "retain with user policy");
        await using var logger = new SerialLog(
            new(),
            fixture.Root,
            fixture.Clock,
            deferRetention: true
        );
        logger.Log(
            fixture.Entry(ApplicationLogLevel.Info) with
            {
                EventName = "Application.Starting",
            }
        );
        await logger.FlushAsync();
        Assert.True(Directory.Exists(historical));
        logger.Configure(new() { RetentionDays = 90 });
        await logger.FlushAsync();
        Assert.True(Directory.Exists(historical));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-5)]
    public async Task AllOutputsIncludeLocalOffsetAndGregorianTimestampRegardlessOfCulture(
        int offsetHours
    )
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            using var fixture = new Fixture();
            fixture.Clock.OffsetHours = offsetHours;
            await using var logger = fixture.Logger();
            var timestamp = fixture.Clock.Now.ToOffset(TimeSpan.FromHours(offsetHours));
            logger.Log(fixture.Entry(ApplicationLogLevel.Info) with { Timestamp = timestamp });
            logger.Log(
                fixture.Entry(ApplicationLogLevel.Info) with
                {
                    Timestamp = timestamp.ToUniversalTime(),
                }
            );
            logger.Log(
                fixture.Traffic(ByteTrafficDirection.Transmit) with
                {
                    Timestamp = timestamp,
                }
            );
            logger.Log(
                fixture.Traffic(ByteTrafficDirection.Receive) with
                {
                    Timestamp = timestamp,
                }
            );
            await logger.LogAsync(
                new InvalidOperationException("failure"),
                new GlobalExceptionContext("test", timestamp, true)
            );
            var expected = timestamp.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffzzz",
                CultureInfo.InvariantCulture
            );
            foreach (var stream in new[] { "info", "all", "bytes", "fatal" })
                Assert.StartsWith(expected, File.ReadAllText(fixture.PathFor(stream)));
            var info = File.ReadAllLines(fixture.PathFor("info"));
            Assert.Equal(info[0], info[1]);
            Assert.All(
                File.ReadAllLines(fixture.PathFor("bytes")),
                line => Assert.StartsWith(expected, line)
            );
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "EGGtCSPlatformTests", Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public string DayPath =>
            Path.Combine(
                Root,
                Clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            );

        public Fixture() => Directory.CreateDirectory(Root);

        public SerialLog Logger(SerialLogOptions? options = null) =>
            new(options ?? new(), Root, Clock);

        public string PathFor(string stream, int index = 1) =>
            Path.Combine(DayPath, $"{stream}.{index}.log");

        public ApplicationLogEntry Entry(ApplicationLogLevel level) =>
            new(Clock.Now, level, "test", "event", "message");

        public ByteTrafficLogEntry Traffic(ByteTrafficDirection direction) =>
            new(
                Clock.Now,
                direction,
                "UDP",
                "192.168.1.102:30307",
                new byte[] { 0xAA, 0xCC, 0x01, 0xFF }
            );

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Clock : TimeProvider
    {
        public int OffsetHours { get; set; } = 8;
        public DateTimeOffset Now { get; set; } = new(2026, 9, 21, 5, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone =>
            TimeZoneInfo.CreateCustomTimeZone(
                "Test",
                TimeSpan.FromHours(OffsetHours),
                "Test",
                "Test"
            );
    }
}
