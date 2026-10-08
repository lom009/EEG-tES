using System.Runtime.CompilerServices;
using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;
using Xunit;

namespace EGGtCSPlatform.Communication.Tests;

public sealed class ClockAndMemoryTests
{
    [Fact]
    public async Task RequestTimeoutUsesInjectedClockWithoutWaitingForWallTime()
    {
        var clock = new ManualClock();
        var transport = new FakeTransport();
        await using var session = new DeviceSession(
            transport,
            new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()),
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateUniform(TimeSpan.FromHours(1))),
            options: new() { TimeProvider = clock }
        );
        await session.StartAsync();
        var response = session.SendAsync(new ReadDeviceStatusRequest());
        Assert.Single(transport.SentPackets);
        Assert.False(response.IsCompleted);
        clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<RequestTimeoutException>(() =>
            response.WaitAsync(TimeSpan.FromSeconds(2))
        );
    }

    [Fact]
    public async Task HeartbeatIntervalUsesInjectedClock()
    {
        var clock = new ManualClock();
        var transport = new RuntimeTests.AutoTransport(true);
        var received = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var session = new DeviceSession(
            transport,
            new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum()),
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults()),
            options: new()
            {
                TimeProvider = clock,
                HeartbeatPolicy = new EggtCsHeartbeatPolicy(
                    TimeSpan.FromHours(1),
                    TimeSpan.FromHours(2),
                    statusReceived: status => received.TrySetResult(status.BatteryPercent)
                ),
            }
        );
        await session.StartAsync();
        Assert.Empty(transport.Inner.SentPackets);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(73, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EventsOwnTheirRawMemoryAfterTransportReusesItsBuffer()
    {
        var codec = new EggtCsFrameCodec(new EggtCsUnverifiedFixedChecksum());
        var frame = codec.Encode(
            new(1, (byte)EggtCsCommandCode.AcquisitionCompleted, ReadOnlyMemory<byte>.Empty)
        );
        var original = frame.Concat(frame).ToArray();
        var transport = new ReusingTransport(original.ToArray());
        await using var session = new DeviceSession(
            transport,
            codec,
            new EggtCsProtocolProfile(EggtCsProtocolOptions.CreateTestDefaults())
        );
        await session.StartAsync();
        await transport.Reused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = session.ReadControlEventsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var first = reader.Current;
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first.TransportReceiveId, reader.Current.TransportReceiveId);
        Assert.Equal(original, first.RawPacketData.ToArray());
        Assert.Equal(frame, first.RawFrameData.ToArray());
        Assert.Equal(frame, reader.Current.RawFrameData.ToArray());
    }

    private sealed class ReusingTransport(byte[] buffer) : ITransport
    {
        public TaskCompletionSource Reused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransportConnectionState State { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = TransportConnectionState.Connected;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = TransportConnectionState.Disconnected;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => DisconnectAsync();

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<TransportPacket> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            yield return TransportPacket.Received(buffer, null, true);
            Array.Fill(buffer, (byte)0);
            Reused.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = new Timer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Change(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in due)
                    timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state)
            : ITimer
        {
            public long Due = long.MaxValue;
            public long Period;
            private bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    if (_disposed)
                        return false;
                    Due =
                        dueTime == Timeout.InfiniteTimeSpan
                            ? long.MaxValue
                            : clock._ticks + dueTime.Ticks;
                    Period = period.Ticks;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    _disposed = true;
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
