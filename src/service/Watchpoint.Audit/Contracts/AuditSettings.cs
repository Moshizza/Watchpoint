namespace Watchpoint.Audit.Contracts;

/// <summary>Settings of the audit module (<c>Audit</c> section, spec AUD §10).</summary>
public sealed record AuditSettings
{
    /// <summary>Name of the configuration section.</summary>
    public const string SectionName = "Audit";

    /// <summary>Days after which whole segments up to a checkpoint are purged (at least 365).</summary>
    public int RetentionDays { get; init; } = 1825;

    /// <summary>Capacity of the writer channel.</summary>
    public int ChannelCapacity { get; init; } = 10_000;

    /// <summary>Most records per batch.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>Longest wait for more records before a batch is written.</summary>
    public TimeSpan BatchDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a caller waits on a full channel before its record goes to the fallback file.</summary>
    public TimeSpan ChannelFullWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Time between checkpoints.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Time between verifications of new segments.</summary>
    public TimeSpan VerifyInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Time between verifications of the whole chain.</summary>
    public TimeSpan FullVerifyInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Time allowed to drain the channel on shutdown.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Records per batch of the verifier.</summary>
    public int VerifyBatchSize { get; init; } = 5_000;

    /// <summary>Largest details payload in bytes; larger payloads are replaced by their hash.</summary>
    public int MaxDetailsBytes { get; init; } = 64 * 1024;

    /// <summary>The chain this node writes (its node id).</summary>
    public string ChainId { get; init; } = "node";

    /// <summary>Directory of the fallback files (<c>&lt;data&gt;/audit</c>).</summary>
    public string FallbackDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "audit");
}
