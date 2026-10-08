using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.MainApp;

namespace EGGtCSPlatform.Services;

/// <summary>Uses the existing SDK's envelope capability; never substitutes a simulated device.</summary>
public sealed class EnvelopeTrainingExecutionService(IDeviceManager manager, Func<byte[], CancellationToken, Task>? playAudio = null) : IEnvelopeTrainingExecutionService, IAsyncDisposable
{
    private readonly CancellationTokenSource _applicationLifetime = new();
    private Task? _active;
    private bool _disposed;

    public Task ExecuteAsync(ExperimentRunRouteData route, EnvelopeTrainingAudio audio, bool stimulate,
        IProgress<EnvelopeTrainingProgress> progress, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active is { IsCompleted: false }) throw new InvalidOperationException("已有包络音频执行任务。");
        _active = ExecuteCoreAsync(route, audio, stimulate, progress, token);
        return _active;
    }

    private async Task ExecuteCoreAsync(ExperimentRunRouteData route, EnvelopeTrainingAudio audio, bool stimulate,
        IProgress<EnvelopeTrainingProgress> progress, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _applicationLifetime.Token);
        token = lifetime.Token;
        IEggtCsDevice? device = null;
        DeviceEventSubscription? subscription = null;
        var attemptedStart = false;
        Task? playback = null, completion = null;
        try
        {
            if (stimulate)
            {
                if (!manager.TryGet(new(route.DeviceId), out device) || device is null
                    || device.State.Connection != DeviceConnectionState.Connected)
                    throw new InvalidOperationException("配置中的设备未连接，请返回检查设备与阻抗。");
                var capability = device.EnvelopeStimulation
                    ?? throw new InvalidOperationException("当前设备不支持包络刺激。");
                if (device.CapabilitySource(DeviceCapabilityKind.Stimulation) == DeviceCapabilitySource.Real)
                    throw new InvalidOperationException("正式音频包络算法与同步规则尚未确认，当前训练执行仅支持已配置的模拟设备。");
                var mask = CreateChannelMask(route);
                var delay = route.StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0;
                if (!double.IsFinite(delay) || delay < 0 || delay > 300)
                    throw new InvalidOperationException("刺激启动延时须在 0–300 ms 内。");
                var configuration = new EnvelopeStimulationConfiguration((ushort)delay, mask,
                    (ushort)audio.EnvelopeSampleRateHz, 0, audio.EnvelopeSamples);
                subscription = device.SubscribeEvents();
                await subscription.Ready.WaitAsync(token);
                EnsureSuccess(await capability.ConfigureAsync(configuration, token), "配置包络");
                completion = WaitForDeviceCompletionAsync(subscription,
                    audio.Duration + TimeSpan.FromMilliseconds(delay) + TimeSpan.FromSeconds(10), lifetime.Token);
                attemptedStart = true; // Even a lost START acknowledgement requires a STOP attempt.
                EnsureSuccess(await capability.StartAsync(token), "启动刺激");
            }

            // Audio and device share samples and duration. Host launch latency is not hardware sync telemetry.
            var clock = Stopwatch.StartNew();
            playback = (playAudio ?? PlayAudioAsync)(audio.WavBytes, lifetime.Token);
            var audioDone = false;
            var stimulusDuration = stimulate
                ? (double)audio.EnvelopeSamples.Length / audio.EnvelopeSampleRateHz
                  + (route.StimulusConfiguration.Envelope?.DelayMilliseconds ?? 0) / 1000 : 0;
            while (!audioDone || (completion is not null && !completion.IsCompleted))
            {
                token.ThrowIfCancellationRequested();
                if (playback.IsCompleted) { await playback; audioDone = true; }
                if (completion is { IsCompleted: true }) await completion;
                if (device is not null && device.State.Connection != DeviceConnectionState.Connected)
                    throw new InvalidOperationException("刺激期间设备连接中断。");
                progress.Report(new(Math.Min(clock.Elapsed.TotalSeconds, audio.Duration.TotalSeconds),
                    stimulate ? Math.Min(clock.Elapsed.TotalSeconds, stimulusDuration) : 0));
                if (!audioDone || (completion is not null && !completion.IsCompleted))
                    await Task.Delay(30, token);
            }
            await playback;
            if (completion is not null) await completion;
            progress.Report(new(audio.Duration.TotalSeconds, stimulusDuration));
        }
        finally
        {
            lifetime.Cancel();
            // Use an independent timeout: cancellation of playback must not cancel emergency STOP.
            Exception? stopFailure = null;
            if (attemptedStart && device?.EnvelopeStimulation is { } capability)
            {
                try
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var result = await capability.StopAsync(stopTimeout.Token);
                    if (!result.IsSuccess && result.Status != DeviceCommandStatus.AlreadyStopped)
                        throw new InvalidOperationException($"停止刺激未确认：{result.Message ?? result.Status.ToString()}");
                }
                catch (Exception exception) { stopFailure = exception; }
            }
            if (playback is not null) try { await playback; } catch { /* reported by primary operation */ }
            if (subscription is not null) await subscription.DisposeAsync();
            if (completion is not null) try { await completion; } catch { /* observed */ }
            if (stopFailure is not null)
                throw new EnvelopeTrainingStopUnconfirmedException(stopFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _applicationLifetime.Cancel();
        // DI disposal awaits this before disposing the manager captured by this singleton.
        try
        {
            if (_active is not null) await _active;
        }
        catch (EnvelopeTrainingStopUnconfirmedException) { throw; }
        catch (Exception) { /* The executing page already reports playback/configuration errors. */ }
        finally { _applicationLifetime.Dispose(); }
    }

    public async Task ConfirmStoppedAsync(ExperimentRunRouteData route, CancellationToken token)
    {
        if (!manager.TryGet(new(route.DeviceId), out var device) || device?.EnvelopeStimulation is null)
            throw new InvalidOperationException("设备不可用，无法确认停止。");
        var result = await device.EnvelopeStimulation.StopAsync(token);
        if (!result.IsSuccess && result.Status != DeviceCommandStatus.AlreadyStopped)
            throw new InvalidOperationException($"停止未确认：{result.Message ?? result.Status.ToString()}");
    }

    public static byte CreateChannelMask(ExperimentRunRouteData route)
    {
        var assignments = route.StimulusElectrodes;
        if (assignments.Count != 2 || assignments[0].PhysicalChannelId == assignments[1].PhysicalChannelId
            || assignments[0].SiteId == assignments[1].SiteId)
            throw new InvalidOperationException("包络刺激需要两个独立点位与物理通道。");
        byte mask = 0;
        foreach (var assignment in assignments)
        {
            if (assignment.PhysicalChannelId is < 1 or > 8)
                throw new InvalidOperationException("包络刺激通道必须在 CH1–CH8 内。");
            mask |= (byte)(1 << (assignment.PhysicalChannelId - 1));
        }
        return mask;
    }

    private static void EnsureSuccess(DeviceCommandResult result, string action)
    {
        if (!result.IsSuccess) throw new InvalidOperationException($"{action}失败：{result.Message ?? result.Status.ToString()}");
    }

    private static async Task WaitForDeviceCompletionAsync(DeviceEventSubscription subscription, TimeSpan timeout,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            await foreach (var envelope in subscription.ReadEventsAsync(deadline.Token))
            {
                if (envelope.Event is StimulationCompletedEvent) return;
                if (envelope.Event is DeviceStateChangedEvent changed
                    && changed.State.Connection != DeviceConnectionState.Connected)
                    throw new InvalidOperationException("刺激期间设备断开。");
            }
            throw new InvalidOperationException("设备事件流已关闭，未收到刺激完成确认。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("设备未在预期时间内确认刺激完成，已请求停止。"); }
    }

    private static async Task PlayAudioAsync(byte[] wav, CancellationToken token)
    {
        var file = Path.Combine(Path.GetTempPath(), $"eggtcs-audio-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(file, wav, token);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var alias = $"eggtcs{Guid.NewGuid():N}";
                if (MciSendString($"open \"{file}\" type waveaudio alias {alias}", null, 0, IntPtr.Zero) != 0)
                    throw new InvalidOperationException("无法打开训练音频。");
                using var registration = token.Register(() => MciSendString($"stop {alias}", null, 0, IntPtr.Zero));
                try
                {
                    var code = await Task.Run(() => MciSendString($"play {alias} wait", null, 0, IntPtr.Zero));
                    token.ThrowIfCancellationRequested();
                    if (code != 0) throw new InvalidOperationException("训练音频播放失败。");
                }
                finally { MciSendString($"close {alias}", null, 0, IntPtr.Zero); }
            }
            else
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/afplay" : "aplay")
                    { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true }
                };
                process.StartInfo.ArgumentList.Add(file);
                process.Start();
                using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
                await process.WaitForExitAsync(CancellationToken.None);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"音频播放失败：{await process.StandardError.ReadToEndAsync()}");
            }
        }
        finally { File.Delete(file); }
    }

    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int MciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr window);
}
