using EGGtCSPlatform.Services;
using Xunit;

namespace EGGtCSPlatform.Tests;

public sealed class ApplicationVersionProviderTests
{
    [Fact]
    public void ReadsConfiguredApplicationVersionAndCreatesWindowTitle()
    {
        var provider = new ApplicationVersionProvider();

        Assert.Equal("1.0.0", provider.Version);
        Assert.Equal("v1.0.0", provider.DisplayVersion);
        Assert.Equal("EGGtCSPlatform · v1.0.0", provider.CreateWindowTitle("EGGtCSPlatform"));
    }
}
