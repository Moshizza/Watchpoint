using Microsoft.Extensions.Logging;

namespace Watchpoint.Kernel.Tests;

/// <summary>Logger that records entries for assertions.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(int Id, LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
}
