using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Sdk.Logging;
using Watchpoint.Integration.Sdk.Net;
using Watchpoint.Integration.Sdk.Polling;
using Watchpoint.Kernel.Ids;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>Secret values a connection read, masked in its logs (with the declared secret field names).</summary>
internal sealed class SecretRedaction(IReadOnlyCollection<string> fieldNames)
{
    private readonly ConcurrentDictionary<string, byte> _values = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> FieldNames { get; } = fieldNames;

    public void Remember(string value)
    {
        // Values of 1–3 characters would mask ordinary text; they are not credentials worth the noise.
        if (value.Length >= 4)
        {
            _values.TryAdd(value, 0);
        }
    }

    public string Mask(string text)
    {
        var masked = RedactionExtensions.MaskSecrets(text, FieldNames);
        foreach (var value in _values.Keys)
        {
            masked = masked.Replace(value, "***", StringComparison.Ordinal);
        }

        return masked;
    }
}

/// <summary>
/// Reads the connection's secrets from the vault as the owner <c>("Integrations", connectionId)</c> with purpose
/// <c>connector</c> (spec IRT §8). Values given for a test (unsaved settings) take precedence; nothing is logged but the code.
/// </summary>
internal sealed class SecretAccessor(ConnectionId connection, IReadOnlyDictionary<string, SecretId> stored, ISecretVault vault,
    SecretRedaction redaction, ILogger logger, IReadOnlyDictionary<string, string>? plain = null) : ISecretAccessor
{
    public const string Module = "Integrations";
    public const string Purpose = "connector";

    public static SecretOwner OwnerOf(ConnectionId connection) => new(Module, connection.Value);

    public async ValueTask<ConnectorSecret?> GetAsync(string fieldId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldId);
        if (plain is not null && plain.TryGetValue(fieldId, out var text))
        {
            redaction.Remember(text);
            return new ConnectorSecret(Encoding.UTF8.GetBytes(text));
        }

        if (!stored.TryGetValue(fieldId, out var id))
        {
            return null;
        }

        var read = await vault.ReadAsync(id, OwnerOf(connection), Purpose, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var value, out var error))
        {
            IntegrationLog.SecretUnavailable(logger, connection.Value, fieldId, error.Code.Value);
            return null;
        }

        using (value)
        {
            value.UseAsString(s =>
            {
                redaction.Remember(s);
                return 0;
            });
            return value.Use(bytes => new ConnectorSecret(bytes));
        }
    }
}

/// <summary>Polling loops of a connector (SDK <see cref="PollingLoop"/>), paused while the connection is not connected unless <c>RunWhenDisconnected</c>.</summary>
internal sealed class ConnectorScheduler(IObservationSink sink, TimeProvider time) : IConnectorScheduler, IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<(PollingLoop Loop, bool RunWhenDisconnected)> _loops = [];
    private bool _connected;
    private bool _disposed;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _loops.Count;
            }
        }
    }

    public IDisposable SchedulePolling(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions? options = null)
    {
        options ??= new PollingOptions();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var loop = new PollingLoop(name, interval, work, sink, time, options);
            if (!_connected && !options.RunWhenDisconnected)
            {
                loop.Pause();
            }

            var entry = (loop, options.RunWhenDisconnected);
            _loops.Add(entry);
            return new Registration(() =>
            {
                lock (_lock)
                {
                    _loops.Remove(entry);
                }

                loop.Dispose();
            });
        }
    }

    public bool IsPaused(string name)
    {
        lock (_lock)
        {
            return _loops.First(l => l.Loop.Name == name).Loop.IsPaused;
        }
    }

    /// <summary>Pauses or resumes the loops that do not run while disconnected.</summary>
    public void SetConnected(bool connected)
    {
        lock (_lock)
        {
            _connected = connected;
            foreach (var (loop, _) in _loops.Where(l => !l.RunWhenDisconnected))
            {
                if (connected)
                {
                    loop.Resume();
                }
                else
                {
                    loop.Pause();
                }
            }
        }
    }

    public void Dispose()
    {
        List<PollingLoop> loops;
        lock (_lock)
        {
            _disposed = true;
            loops = [.. _loops.Select(l => l.Loop)];
            _loops.Clear();
        }

        loops.ForEach(l => l.Dispose());
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>HTTP clients for the connector: the SDK's TLS policies (pin, custom CA, system trust); plain HTTP only when the settings field <c>allowInsecureHttp</c> is true.</summary>
internal sealed class ConnectorHttp(bool allowInsecureHttp) : ISecureHttp
{
    public bool AllowInsecureHttp { get; } = allowInsecureHttp;

    public HttpClient CreateClient(HttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SecureHttp.Instance.CreateClient(options with { AllowHttp = options.AllowHttp && AllowInsecureHttp });
    }
}

/// <summary>The connector instruments: meter <c>Watchpoint.Connector</c>, tags <c>connection</c> and <c>package</c>.</summary>
internal sealed partial class ConnectorMeter : IDisposable
{
    public const string MeterName = "Watchpoint.Connector";

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly ConcurrentDictionary<(string Connection, string Package, string Name), double> _gauges = new();

    public ConnectorMeter(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _requests = _meter.CreateCounter<long>("watchpoint.connector.requests");
        _duration = _meter.CreateHistogram<double>("watchpoint.connector.request.duration", "ms");
        _meter.CreateObservableGauge("watchpoint.connector.gauge", () => _gauges.Select(g =>
            new Measurement<double>(g.Value, new("connection", g.Key.Connection), new("package", g.Key.Package), new("name", g.Key.Name))));
    }

    public void CountRequest(string connection, string package, string operation, bool success, TimeSpan duration)
    {
        KeyValuePair<string, object?>[] tags = [new("connection", connection), new("package", package), new("operation", operation), new("success", success)];
        _requests.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    public void SetGauge(string connection, string package, string name, double value)
    {
        if (!GaugeName().IsMatch(name))
        {
            throw new ArgumentException($"Gauge name '{name}' does not match ^[a-z][a-z0-9_]{{1,40}}$.", nameof(name));
        }

        _gauges[(connection, package, name)] = value;
    }

    public void Remove(string connection)
    {
        foreach (var key in _gauges.Keys.Where(k => k.Connection == connection))
        {
            _gauges.TryRemove(key, out _);
        }
    }

    public void Dispose() => _meter.Dispose();

    [GeneratedRegex("^[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex GaugeName();
}

/// <summary>The metrics of one connection.</summary>
internal sealed class ConnectorMetrics(ConnectorMeter meter, string connection, string package) : IConnectorMetrics
{
    public void CountRequest(string operation, bool success, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        meter.CountRequest(connection, package, operation, success, duration);
    }

    public void SetGauge(string name, double value) => meter.SetGauge(connection, package, name, value);
}

/// <summary>
/// Loggers of a connector (spec IRT §8): category <c>Watchpoint.Connector.&lt;packageId&gt;</c>, scope
/// <c>ConnectionId</c> and <c>Package</c>, secret fields and the secret values the connection read masked, and the
/// trace level only while the protocol trace is on.
/// </summary>
internal sealed class ConnectorLoggerFactory(ILoggerFactory inner, Guid connectionId, string package, SecretRedaction redaction,
    Func<bool> protocolTrace) : ILoggerFactory
{
    private Guid ConnectionId => connectionId;

    private string Package => package;

    private SecretRedaction Redaction => redaction;

    private bool ProtocolTrace => protocolTrace();

    public string Category => "Watchpoint.Connector." + package;

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("Connectors cannot add logger providers.");

    public ILogger CreateLogger(string categoryName) => new ConnectorLogger(inner.CreateLogger(Category), this);

    public void Dispose()
    {
        // The host owns the inner factory.
    }

    private sealed class ConnectorLogger(ILogger inner, ConnectorLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && (logLevel > LogLevel.Trace || owner.ProtocolTrace) && inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // The structured state may hold secret values: only the masked text is passed on.
            var text = owner.Redaction.Mask(formatter(state, exception));
            var masked = exception is null ? null : new RedactedException(owner.Redaction.Mask(exception.GetType().FullName + ": " + exception.Message));
            using (inner.BeginScope(new Dictionary<string, object?> { ["ConnectionId"] = owner.ConnectionId, ["Package"] = owner.Package }))
            {
                inner.Log(logLevel, eventId, text, masked, static (s, _) => s);
            }
        }
    }
}

/// <summary>An exception of a connector, as logged: its type and masked message only.</summary>
internal sealed class RedactedException : Exception
{
    public RedactedException(string message)
        : base(message)
    {
    }

    public RedactedException()
    {
    }

    public RedactedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
