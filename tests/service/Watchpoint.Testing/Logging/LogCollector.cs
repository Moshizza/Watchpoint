using Microsoft.Extensions.Logging;

namespace Watchpoint.Testing.Logging;

/// <summary><see cref="ILoggerProvider"/> capturing structured log entries for assertions. Thread-safe.</summary>
public sealed class LogCollector : ILoggerProvider
{
    private const string OriginalFormat = "{OriginalFormat}";

    private readonly List<CapturedLog> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Gets a snapshot of the captured entries, in logging order.</summary>
    public IReadOnlyList<CapturedLog> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Returns the captured entries with the given event id.</summary>
    /// <param name="id">The event id.</param>
    /// <returns>The matching entries.</returns>
    public IEnumerable<CapturedLog> WithEventId(int id) => Entries.Where(e => e.EventId == id);

    /// <summary>Removes all captured entries.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Add(CapturedLog entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CollectingLogger(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var rendered = formatter(state, exception);
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            string? template = null;
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == OriginalFormat)
                    {
                        template = value?.ToString();
                    }
                    else
                    {
                        properties[key] = value;
                    }
                }
            }

            owner.Add(new CapturedLog(category, logLevel, eventId.Id, template ?? rendered, properties, rendered, exception));
        }
    }
}
