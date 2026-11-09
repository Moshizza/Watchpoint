using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Testing.Logging;

/// <summary>A log entry captured by <see cref="TestLogCollector"/>.</summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The level.</param>
/// <param name="EventId">The event id.</param>
/// <param name="MessageTemplate">The message template (<c>{OriginalFormat}</c>), or the rendered message when absent.</param>
/// <param name="Properties">The structured properties, without <c>{OriginalFormat}</c>.</param>
/// <param name="RenderedMessage">The formatted message.</param>
/// <param name="Exception">The exception, if any.</param>
public sealed record TestCapturedLog(
    string Category,
    LogLevel Level,
    int EventId,
    string MessageTemplate,
    IReadOnlyDictionary<string, object?> Properties,
    string RenderedMessage,
    Exception? Exception);

/// <summary><see cref="ILoggerProvider"/> capturing structured log entries for assertions. Thread-safe.</summary>
public sealed class TestLogCollector : ILoggerProvider
{
    private const string OriginalFormat = "{OriginalFormat}";
    private readonly List<TestCapturedLog> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Gets a snapshot of the captured entries, in logging order.</summary>
    public IReadOnlyList<TestCapturedLog> Entries
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
    public IEnumerable<TestCapturedLog> WithEventId(int id) => Entries.Where(e => e.EventId == id);

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

    private void Add(TestCapturedLog entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CollectingLogger(TestLogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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

            owner.Add(new TestCapturedLog(category, logLevel, eventId.Id, template ?? rendered, properties, rendered, exception));
        }
    }
}

/// <summary>
/// Fails a test when a registered secret appears in captured logs: in the rendered message, the template,
/// a property value or the exception text; plain, base64 or URL-encoded; compared case-insensitively.
/// </summary>
public sealed class TestSecretLeakDetector
{
    private readonly List<string> _secrets = [];

    /// <summary>Registers a secret value used by the test (for example a password).</summary>
    /// <param name="secretValue">The secret; must not be empty.</param>
    public void Register(string secretValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretValue);
        _secrets.Add(secretValue);
    }

    /// <summary>Asserts that no registered secret appears in <paramref name="logs"/>.</summary>
    /// <param name="logs">The captured logs.</param>
    public void AssertNoLeak(TestLogCollector logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var findings = new List<string>();
        var entries = logs.Entries;
        for (var s = 0; s < _secrets.Count; s++)
        {
            var forms = Forms(_secrets[s]);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                foreach (var (field, text) in Fields(entry))
                {
                    foreach (var (formName, form) in forms)
                    {
                        if (text.Contains(form, StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add($"secret #{s + 1} ({formName}) in entry #{i} [{entry.Category}, event {entry.EventId}] {field}");
                        }
                    }
                }
            }
        }

        if (findings.Count > 0)
        {
            throw new InvalidOperationException("Secret leaked into logs:" + Environment.NewLine + string.Join(Environment.NewLine, findings.Distinct()));
        }
    }

    private static List<(string Name, string Form)> Forms(string secret)
    {
        var forms = new List<(string, string)> { ("plain", secret) };
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        forms.Add(("base64", base64));
        var trimmed = base64.TrimEnd('=');
        if (trimmed != base64)
        {
            forms.Add(("base64", trimmed));
        }

        foreach (var url in new[] { Uri.EscapeDataString(secret), WebUtility.UrlEncode(secret) })
        {
            if (url != secret)
            {
                forms.Add(("url-encoded", url));
            }
        }

        return forms;
    }

    private static IEnumerable<(string Field, string Text)> Fields(TestCapturedLog entry)
    {
        yield return ("message", entry.RenderedMessage);
        yield return ("template", entry.MessageTemplate);
        foreach (var (key, value) in entry.Properties)
        {
            if (value?.ToString() is { } text)
            {
                yield return ($"property '{key}'", text);
            }
        }

        if (entry.Exception is not null)
        {
            yield return ("exception", entry.Exception.ToString());
        }
    }
}
