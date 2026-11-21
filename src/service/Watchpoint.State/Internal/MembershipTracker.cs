using Microsoft.Extensions.Hosting;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Reacts to <see cref="IRegistryReader.Changed"/> (spec STA §6.3): initialises newly created devices and servers,
/// refreshes membership, flags and type on an update, and removes entities no longer in the registry. The in-process
/// event carries the request batch, not a resolved created/updated/deleted list (REG §6), so entities named by an
/// upsert or patch are looked up in the (already swapped) registry snapshot and classified as created when the state
/// module does not track them yet, updated otherwise. A batch that can delete entities without naming every one of
/// them (a cascading device, server or location delete) is followed by a sweep that removes every tracked entity no
/// longer in the registry, so nothing is leaked (spec STA §6.3: "STA discards entities no longer in REG only from
/// its own state").
/// </summary>
internal sealed class MembershipTracker : IHostedService
{
    private readonly IRegistryReader _registry;
    private readonly StateStore _store;
    private readonly IPartitionWork _work;
    private readonly TimeProvider _time;

    public MembershipTracker(IRegistryReader registry, StateStore store, IPartitionWork work, TimeProvider time)
    {
        _registry = registry;
        _store = store;
        _work = work;
        _time = time;
    }

    /// <summary>Subscribes to <see cref="IRegistryReader.Changed"/>; <see langword="internal"/> so tests can call it without an async wrapper.</summary>
    public void Subscribe() => _registry.Changed += OnChanged;

    /// <summary>Unsubscribes from <see cref="IRegistryReader.Changed"/>.</summary>
    public void Unsubscribe() => _registry.Changed -= OnChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    /// <summary>Handles one applied batch; <see langword="internal"/> so unit tests can drive it without a real event.</summary>
    internal void Handle(RegistryChangeBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var now = _time.GetUtcNow();
        var devices = new HashSet<DeviceId>();
        var servers = new HashSet<ServerId>();
        var needsSweep = false;

        foreach (var change in batch.Changes)
        {
            switch (change)
            {
                case RegistryChange.UpsertDevice upsert:
                    devices.Add(upsert.Device.Id);
                    break;
                case RegistryChange.PatchDevice patch:
                    devices.Add(patch.Id);
                    break;
                case RegistryChange.UpsertServer upsert:
                    servers.Add(upsert.Server.Id);
                    break;
                case RegistryChange.DeleteDevice:
                case RegistryChange.DeleteServer:
                    needsSweep = true;
                    break;
            }
        }

        foreach (var id in devices)
        {
            if (_registry.GetDevice(id).TryGetValue(out var device, out _))
            {
                Upsert(EntityInfo.Of(device), now, ct);
            }
            else
            {
                needsSweep = true;
            }
        }

        foreach (var id in servers)
        {
            if (_registry.GetServer(id).TryGetValue(out var server, out _))
            {
                Upsert(EntityInfo.Of(server), now, ct);
            }
            else
            {
                needsSweep = true;
            }
        }

        if (needsSweep)
        {
            Sweep(now, ct);
        }
    }

    private void OnChanged(object? sender, RegistryChangedEventArgs e) => Handle(e.Batch);

    private void Upsert(EntityInfo info, DateTimeOffset now, CancellationToken ct)
    {
        var partition = Partitioning.Of(info.Id);
        var existed = _store.HasEntity(info.Id);
        _ = _work.RunAsync(
            partition,
            () =>
            {
                if (existed)
                {
                    _store.UpdateEntity(info, now);
                }
                else
                {
                    _store.InitializeEntity(info, now);
                }
            },
            ct);
    }

    private void Sweep(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var id in _store.AllEntityIds().ToList())
        {
            if (!_store.Get(id).TryGetValue(out var state, out _))
            {
                continue;
            }

            var exists = state.Kind == EntityKind.Device
                ? _registry.GetDevice(DeviceId.From(id)).IsSuccess
                : _registry.GetServer(ServerId.From(id)).IsSuccess;
            if (exists)
            {
                continue;
            }

            var partition = Partitioning.Of(id);
            _ = _work.RunAsync(partition, () => _store.RemoveEntity(id, now), ct);
        }
    }
}
