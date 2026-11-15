using System.Diagnostics.Metrics;

namespace Watchpoint.Registry.Internal;

internal static class RegistryMetrics
{
    private static readonly Meter _meter = new("Watchpoint.Registry", "1.0.0");

    public static readonly Counter<long> ChangesApplied =
        _meter.CreateCounter<long>("wp_registry_changes_applied_total", "records", "Number of registry changes applied");

    public static readonly Counter<long> BatchesCommitted =
        _meter.CreateCounter<long>("wp_registry_batches_committed_total", "batches", "Number of registry batches committed");

    public static readonly Counter<long> OptimisticConflicts =
        _meter.CreateCounter<long>("wp_registry_optimistic_conflicts_total", "conflicts", "Number of optimistic concurrency conflicts");

    public static readonly Histogram<double> BatchApplyDuration =
        _meter.CreateHistogram<double>("wp_registry_batch_apply_duration_seconds", "s", "Duration of registry batch apply in seconds");

    public static readonly Histogram<double> SnapshotSwapDuration =
        _meter.CreateHistogram<double>("wp_registry_snapshot_swap_duration_seconds", "s", "Duration of snapshot swap in seconds");

    private static long _revision;

    /// <summary><c>watchpoint.registry.revision</c> (spec REG §12): the last revision committed by this node.</summary>
    public static readonly ObservableGauge<long> Revision =
        _meter.CreateObservableGauge("watchpoint.registry.revision", () => Volatile.Read(ref _revision), "{revision}", "The registry revision last committed by this node");

    public static void RevisionCommitted(long revision) => Volatile.Write(ref _revision, revision);
}
