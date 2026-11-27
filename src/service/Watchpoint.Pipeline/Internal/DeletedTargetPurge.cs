using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Pipeline.Internal.Partitions;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// Purges the last value cache entries of deleted devices and servers (spec PIP §7.2; PIP owns it, STA §6.3). On the
/// registry's <see cref="IRegistryReader.Changed"/>, a delete posts work to the target's partition worker that
/// removes every cache entry (properties and health) of a target the registry no longer has. A delete that can remove
/// unnamed targets (a device with its children, a server with its devices) sweeps every partition instead. The
/// in-process event carries the request batch, not the resolved deletions (REG §6), so a target is purged only once
/// the registry no longer finds it; a record of a deleted target still in flight is rejected at validation
/// (<c>pipeline.target.unknown</c>), so no entry comes back.
/// </summary>
internal sealed class DeletedTargetPurge(IRegistryReader? registry, PartitionRouter router, IIngestDirectory directory, ILogger<DeletedTargetPurge> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Without a registry (pipeline-only compositions in tests) nothing is ever deleted.
        if (registry is not null)
        {
            registry.Changed += OnChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (registry is not null)
        {
            registry.Changed -= OnChanged;
        }

        return Task.CompletedTask;
    }

    /// <summary>Handles one applied batch; returns the purge of each partition concerned (tests await it).</summary>
    internal Task Handle(RegistryChangeBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var named = new HashSet<Guid>();
        var sweep = false;
        foreach (var change in batch.Changes)
        {
            switch (change)
            {
                case RegistryChange.DeleteDevice device:
                    named.Add(device.Id.Value);
                    sweep |= device.DeleteChildren;
                    break;
                case RegistryChange.DeleteServer server:
                    named.Add(server.Id.Value);
                    sweep |= server.DeleteDevices;
                    break;
            }
        }

        if (named.Count == 0)
        {
            return Task.CompletedTask;
        }

        var work = sweep
            ? Enumerable.Range(0, Partitioning.PartitionCount).Select(p => PurgeAsync(p, null, ct))
            : named.GroupBy(Partitioning.Of).Select(g => PurgeAsync(g.Key, [.. g], ct));
        return Task.WhenAll(work);
    }

    private void OnChanged(object? sender, RegistryChangedEventArgs e) => _ = Handle(e.Batch);

    private async Task PurgeAsync(int partition, IReadOnlyCollection<Guid>? targets, CancellationToken ct)
    {
        try
        {
            await router.PurgeAsync(partition, targets, Exists, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PipelineException or OperationCanceledException)
        {
            // The pipeline is stopping: the cache goes with it (an unrestored cache is empty at the next start).
            PipelineLog.PurgeSkipped(logger, partition);
        }
    }

    private bool Exists(Guid target) =>
        directory.FindDevice(DeviceId.From(target)) is not null || directory.FindServer(ServerId.From(target)) is not null;
}
