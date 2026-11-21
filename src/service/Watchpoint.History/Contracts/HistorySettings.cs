namespace Watchpoint.History.Contracts;

/// <summary>Settings of the history module (spec HIS §13, section <c>History</c>).</summary>
public sealed record HistorySettings
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "History";

    /// <summary><c>History:BatchSize</c>: the most records one batch writes (1 to 10,000).</summary>
    public int BatchSize { get; init; } = 5000;

    /// <summary><c>History:LagWarning</c>: the lag after which a warning is raised.</summary>
    public TimeSpan LagWarning { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary><c>History:QueryTimeout</c>: the statement timeout of queries.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets how long a batch waits for more records before it is written (1 s).</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the first delay after a failed write; doubled up to <see cref="RetryDelayMax"/>.</summary>
    public TimeSpan RetryDelayMin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the longest delay between two attempts.</summary>
    public TimeSpan RetryDelayMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets how long a policy override is cached.</summary>
    public TimeSpan PolicyOverrideRefresh { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the most (device, path) keys kept in the last-stored cache.</summary>
    public int LastStoredCacheSize { get; init; } = 500_000;

    /// <summary>Gets how often the partitions are topped up when the provider has no TimescaleDB.</summary>
    public TimeSpan SchemaMaintenanceInterval { get; init; } = TimeSpan.FromHours(6);
}
