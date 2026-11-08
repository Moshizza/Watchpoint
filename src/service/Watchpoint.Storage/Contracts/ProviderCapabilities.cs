namespace Watchpoint.Storage.Contracts;

/// <summary>How a provider inserts many rows.</summary>
public enum BulkInsertMethod
{
    /// <summary>PostgreSQL binary COPY.</summary>
    BinaryCopy,

    /// <summary>SQL Server bulk copy.</summary>
    BulkCopy,

    /// <summary>Multi-row INSERT statements.</summary>
    MultiRowInsert,
}

/// <summary>What a provider (and, for probed values, a database) supports (spec STO §5).</summary>
/// <param name="Schemas">Database schemas exist (else table prefixes are used).</param>
/// <param name="JsonDocuments">Indexed JSON documents (PostgreSQL <c>jsonb</c>).</param>
/// <param name="TimeSeriesExtension">TimescaleDB is installed (probed).</param>
/// <param name="NativeCompression">Native compression of history data.</param>
/// <param name="ContinuousAggregates">Continuous aggregates.</param>
/// <param name="Partitioning">Declarative partitioning.</param>
/// <param name="BulkInsert">Bulk insert method.</param>
/// <param name="MaxDatabaseSizeBytes">Maximum database size; <see langword="null"/> = unlimited.</param>
public sealed record ProviderCapabilities(
    bool Schemas,
    bool JsonDocuments,
    bool TimeSeriesExtension,
    bool NativeCompression,
    bool ContinuousAggregates,
    bool Partitioning,
    BulkInsertMethod BulkInsert,
    long? MaxDatabaseSizeBytes);
