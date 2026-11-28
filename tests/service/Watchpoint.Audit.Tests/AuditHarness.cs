using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Audit.Tests;

/// <summary>A writer on an in-memory store with a test clock, a temporary fallback directory and a capturing log.</summary>
internal sealed class AuditHarness : IAsyncDisposable
{
    public static readonly Guid Alice = new("00000000-0000-7000-8000-0000000000a1");

    public static readonly Guid Door = new("00000000-0000-7000-8000-0000000000d1");

    private readonly ILoggerFactory _loggers;

    public AuditHarness(Func<AuditSettings, AuditSettings>? configure = null)
    {
        FallbackDirectory = Path.Combine(Path.GetTempPath(), "wp-audit-tests-" + Guid.NewGuid().ToString("N"));
        Settings = (configure ?? (s => s))(new AuditSettings { ChainId = "node-1", FallbackDirectory = FallbackDirectory });
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Metrics = new AuditMetrics();
        Writer = new AuditWriter(Store, Settings, Clock, _loggers.CreateLogger<AuditWriter>(), Metrics);
    }

    public TestClock Clock { get; } = new();

    public InMemoryAuditStore Store { get; } = new();

    public LogCollector Logs { get; } = new();

    public AuditSettings Settings { get; }

    public string FallbackDirectory { get; }

    public AuditMetrics Metrics { get; }

    public AuditWriter Writer { get; }

    public TestSigner Signer { get; } = new();

    public AuditVerifier Verifier => new(Store, Signer, Settings, Clock, Metrics);

    public CheckpointService Checkpoints => new(Store, Signer, Writer, Settings, Clock, _loggers.CreateLogger<CheckpointService>());

    public static AuditRecord Record(string action = "identity.signedIn", AuditSeverity severity = AuditSeverity.Normal, Guid? correlation = null, JsonElement? details = null,
        string? reason = null, AuditOutcome outcome = AuditOutcome.Success, IReadOnlyList<AuditObjectRef>? objects = null, DateTimeOffset? at = null) =>
        new(new AuditAction(action), outcome, AuditActor.User(Alice, "Alice", AuditClientKind.Desktop, "WS-1", "10.0.0.5"), objects ?? [new AuditObjectRef("device", Door, "Lobby door")],
            reason, details, severity, correlation, at);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public ValueTask WriteAsync(AuditRecord record) => Writer.WriteAsync(record, TestContext.Current.CancellationToken);

    /// <summary>Starts the writer and waits for it to be ready.</summary>
    public async Task StartAsync()
    {
        await Writer.StartAsync(CancellationToken.None);
        await Writer.Started.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Writes <paramref name="count"/> critical records, each stored before it returns.</summary>
    public async Task WriteStoredAsync(int count, string action = "identity.signedIn")
    {
        for (var i = 0; i < count; i++)
        {
            await WriteAsync(Record(action, AuditSeverity.Critical));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Writer.StopAsync(CancellationToken.None);
        await Writer.DisposeAsync();
        Signer.Dispose();
        Metrics.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(FallbackDirectory))
        {
            Directory.Delete(FallbackDirectory, recursive: true);
        }
    }
}
