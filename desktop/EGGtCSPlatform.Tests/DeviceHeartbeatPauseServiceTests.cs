using System.Threading.Tasks;
using EGGtCSPlatform.DeviceSdk;
using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class DeviceHeartbeatPauseServiceTests
{
    [Fact]
    public void NestedScopesResumeOnlyAfterLastScopeIsDisposed()
    {
        var service = new DeviceHeartbeatPauseService();
        var deviceId = new DeviceId("DEVICE-A");

        var outer = service.Pause(deviceId);
        var inner = service.Pause(deviceId);
        Assert.True(service.IsPaused(deviceId));

        outer.Dispose();
        Assert.True(service.IsPaused(deviceId));

        inner.Dispose();
        Assert.False(service.IsPaused(deviceId));
        inner.Dispose();
        Assert.False(service.IsPaused(deviceId));
    }

    [Fact]
    public async Task ConcurrentScopesAreCountedIndependentlyPerDevice()
    {
        var service = new DeviceHeartbeatPauseService();
        var firstId = new DeviceId("DEVICE-A");
        var secondId = new DeviceId("DEVICE-B");
        var scopes = await Task.WhenAll(
            Task.Run(() => service.Pause(firstId)),
            Task.Run(() => service.Pause(firstId)),
            Task.Run(() => service.Pause(secondId))
        );

        Assert.True(service.IsPaused(firstId));
        Assert.True(service.IsPaused(secondId));

        scopes[0].Dispose();
        scopes[2].Dispose();
        Assert.True(service.IsPaused(firstId));
        Assert.False(service.IsPaused(secondId));

        scopes[1].Dispose();
        Assert.False(service.IsPaused(firstId));
    }
}
