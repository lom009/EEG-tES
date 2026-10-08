using System.Reflection;

namespace EGGtCSPlatform.Services;

public interface IApplicationVersionProvider
{
    string Version { get; }

    string DisplayVersion { get; }

    string CreateWindowTitle(string title);
}

public sealed class ApplicationVersionProvider : IApplicationVersionProvider
{
    private readonly Assembly _applicationAssembly;

    public ApplicationVersionProvider()
        : this(typeof(App).Assembly) { }

    internal ApplicationVersionProvider(Assembly applicationAssembly)
    {
        _applicationAssembly = applicationAssembly;
    }

    public string Version =>
        _applicationAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', 2)[0]
        ?? _applicationAssembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public string DisplayVersion => $"v{Version}";

    public string CreateWindowTitle(string title) => $"{title} · {DisplayVersion}";
}
