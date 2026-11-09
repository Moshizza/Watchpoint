using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Testing.Logging;

/// <summary>
/// Simple <see cref="ILoggerFactory"/> implementation wrapping providers for conformance tests.
/// </summary>
public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers = [];

    /// <summary>Creates a new instance of <see cref="TestLoggerFactory"/> with the given provider.</summary>
    public TestLoggerFactory(ILoggerProvider provider)
    {
        _providers.Add(provider);
    }

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        _providers.Add(provider);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return _providers[0].CreateLogger(categoryName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var p in _providers)
        {
            p.Dispose();
        }
    }
}
