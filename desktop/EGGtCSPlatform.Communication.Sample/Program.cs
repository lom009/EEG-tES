using EGGtCSPlatform.Communication.Core;
using EGGtCSPlatform.Communication.Udp;
using EGGtCSPlatform.DeviceRuntime;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Protocol.EggtCs.Gen1;

// No Avalonia, dependency injection, appsettings or environment-variable dependency.
// Default: two simulated devices. Physical mode only discovers/connects/reads status.
var real = args.Contains("--real");
string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
if (args.Contains("--help"))
{
    Console.WriteLine(
        "Default: simulator. --real [--address IP] [--device-port 30307] [--local-address 0.0.0.0] [--local-port 30302] [--shared] [--allow-unverified-checksum] [--reconnect-on-failure] [--reconnect]"
    );
    return;
}
var options = new DeviceRuntimeOptions
{
    Backend = real ? new DeviceBackendProfile() : DeviceBackendProfile.Simulation(),
    UdpMode = args.Contains("--shared") ? UdpConnectionMode.Shared : UdpConnectionMode.Dedicated,
    NetworkSettings = () =>
        new(
            "255.255.255.255",
            int.Parse(Argument("--device-port") ?? "30307"),
            Argument("--local-address") ?? "0.0.0.0",
            int.Parse(Argument("--local-port") ?? "30302")
        ),
};
var allowUnverifiedChecksum = args.Contains("--allow-unverified-checksum");
if (real && !allowUnverifiedChecksum)
    throw new InvalidOperationException(
        "This sample uses the repository's unverified checksum. Supply a verified IChecksum in your integration, or explicitly opt in with --allow-unverified-checksum."
    );
var builder = new DeviceRuntimeBuilder().WithOptions(options);
if (real)
    builder.AddProtocol(
        new EggtCsProtocolModule(new() { AllowUnverifiedChecksum = allowUnverifiedChecksum })
    );
await using var runtime = builder
    .OnFailure(
        (context, token) =>
        {
            token.ThrowIfCancellationRequested();
            Console.Error.WriteLine(
                $"[{context.DeviceId}/{context.ConnectionGeneration}; {context.ProtocolId}/{context.ProtocolRevision}] {context.Category}: {context.Exception.Message}; sent={context.WasSent}; ambiguous={context.ResponseMayBeAmbiguous}"
            );
            var decision =
                args.Contains("--reconnect-on-failure")
                && context.AllowedDecisions.Contains(CommunicationFailureDecision.ReconnectDevice)
                    ? CommunicationFailureDecision.ReconnectDevice
                    : CommunicationFailureDecision.UseDefault;
            return ValueTask.FromResult(decision);
        }
    )
    .Build();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
var candidates = new List<DeviceCandidate>();
if (real && Argument("--address") is { } address)
    candidates.Add(
        new(
            new(new($"manual:{address}"), "EGG/tCS Gen1", null, null),
            new("udp", address, options.NetworkSettings().DevicePort),
            EggtCsRevisions.DefaultRevision,
            IdentityIsProvisional: true
        )
        {
            ProtocolId = EggtCsProtocolModule.Id,
            RevisionSource = ProtocolRevisionSource.SoftwareDefault,
        }
    );
else if (real)
{
    await foreach (
        var candidate in runtime.Discovery.DiscoverAsync(
            TimeSpan.FromSeconds(1),
            cancellation.Token
        )
    )
        candidates.Add(candidate);
    if (options.UdpMode == UdpConnectionMode.Dedicated)
        candidates = candidates.Take(1).ToList();
}
else
{
    for (var i = 1; i <= 2; i++)
        candidates.Add(
            new(
                new(new($"sim-{i}"), "Simulator", $"SIM-{i}", null),
                new("simulator", "local", 0),
                "simulator"
            )
        );
}
foreach (var candidate in candidates)
{
    try
    {
        var device = await runtime.ConnectVerifiedAsync(candidate, cancellation.Token);
        if (args.Contains("--reconnect"))
        {
            var previous = device;
            device = await runtime.ReconnectAsync(device.Identity.DeviceId, cancellation.Token);
            Console.WriteLine(
                $"Reconnect: {device.State.Connection}, replaced={!ReferenceEquals(previous, device)}"
            );
        }
        var protocol = ((IDeviceProtocolBinding)device).ProtocolInfo;
        Console.WriteLine(
            $"Software protocol: {protocol.ProtocolId} · {protocol.ActiveRevision}; source={protocol.RevisionSource} (not a reported firmware version)"
        );
        await using var events = device.SubscribeEvents(
            new() { DataPolicy = DeviceDataDeliveryPolicy.Reliable }
        );
        await events.Ready.WaitAsync(cancellation.Token);
        Console.WriteLine(
            $"{device.Identity.DeviceId}: {device.State.Connection}, battery={device.State.BatteryPercent}, EEG={device.Capabilities.CanAcquireEeg}"
        );
    }
    catch (RequestTimeoutException error)
    {
        Console.Error.WriteLine(
            $"Request timed out: {error.Message}. No device operation is replayed."
        );
        Environment.ExitCode = 1;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.WriteLine("Canceled by Ctrl+C or the sample's 15-second limit.");
        break;
    }
    catch (CommunicationException error)
    {
        Console.Error.WriteLine($"Communication failed: {error.Message}");
        Environment.ExitCode = 1;
    }
}
Console.WriteLine(
    $"Connected devices: {runtime.Devices.Devices.Count}. Disposing runtime and all connections."
);
