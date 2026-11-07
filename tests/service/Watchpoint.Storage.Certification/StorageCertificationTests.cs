using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.Storage.Certification;

/// <summary>
/// Behaviour every storage provider must have (spec STO §12, STO-I-007…STO-I-014). A provider's integration tests
/// derive from this class and supply a node on a fresh database.
/// </summary>
public abstract class StorageCertificationTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    /// <summary>Gets the cancellation token of the running test.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts the storage of a node on a fresh database: settings for every store, <see cref="CertificationContext"/> registered.</summary>
    /// <param name="testName">Name of the test (database naming).</param>
    /// <returns>The services and what to dispose when the test ends.</returns>
    protected abstract Task<(IServiceProvider Services, IAsyncDisposable Lifetime)> StartNodeAsync(string testName);

    /// <summary>Whether a table exists.</summary>
    /// <param name="services">The node.</param>
    /// <param name="schema">The schema (or prefix).</param>
    /// <param name="table">The table.</param>
    /// <returns>Whether it exists.</returns>
    protected abstract Task<bool> TableExistsAsync(IServiceProvider services, string schema, string table);

    /// <summary>A raw state store of the provider that runs <paramref name="beforeCommit"/> after its writes, before committing.</summary>
    /// <param name="services">The node.</param>
    /// <param name="beforeCommit">The fault to inject.</param>
    /// <returns>The store.</returns>
    protected abstract IStateStore StateStoreWithFault(IServiceProvider services, Func<CancellationToken, Task> beforeCommit);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task STO_I_007_State_tables_bootstrapped()
    {
        var node = await NodeAsync(nameof(STO_I_007_State_tables_bootstrapped));

        (await TableExistsAsync(node, "wp_state", "items")).ShouldBeTrue();
        (await TableExistsAsync(node, "wp_state", "checkpoints")).ShouldBeTrue();
    }

    [Fact]
    public async Task STO_I_008_Snapshot_upsert_and_read_back()
    {
        var store = (await NodeAsync(nameof(STO_I_008_Snapshot_upsert_and_read_back))).GetRequiredService<IStateStore>();
        var items = Enumerable.Range(0, 3).Select(i => Item(partition: 4, value: i)).ToList();

        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 4, 10, items, []), Ct);

        var read = await ReadAsync(store, "device", 4);
        read.Select(i => i.Key).ShouldBe(items.Select(i => i.Key), ignoreOrder: true);
        read.ShouldAllBe(i => i.Partition == 4 && i.Sequence == 10);
        Value(read.Single(i => i.Key == items[2].Key)).ShouldBe(2);
        (await store.ReadCheckpointsAsync("state", Ct)).ShouldBe(new Dictionary<int, long> { [4] = 10 });
    }

    [Fact]
    public async Task STO_I_009_Snapshot_update_and_delete()
    {
        var store = (await NodeAsync(nameof(STO_I_009_Snapshot_update_and_delete))).GetRequiredService<IStateStore>();
        var items = Enumerable.Range(0, 3).Select(i => Item(partition: 1, value: i)).ToList();
        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 10, items, []), Ct);

        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 11,
            [items[0] with { PayloadJson = Payload(100), Sequence = 11 }], [new StateItemKey("device", items[1].Key)]), Ct);

        var read = await ReadAsync(store, "device", 1);
        read.Count.ShouldBe(2);
        Value(read.Single(i => i.Key == items[0].Key)).ShouldBe(100);
        read.ShouldNotContain(i => i.Key == items[1].Key);
        (await store.ReadCheckpointsAsync("state", Ct))[1].ShouldBe(11);
    }

    [Fact]
    public async Task STO_I_010_Snapshot_atomicity()
    {
        var node = await NodeAsync(nameof(STO_I_010_Snapshot_atomicity));
        var store = node.GetRequiredService<IStateStore>();
        var original = Item(partition: 2, value: 1);
        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 2, 5, [original], []), Ct);
        var faulty = StateStoreWithFault(node, _ => throw new InvalidOperationException("injected fault"));

        await Should.ThrowAsync<InvalidOperationException>(() => faulty.WriteSnapshotAsync(
            new StateSnapshotBatch("state", 2, 6, [original with { PayloadJson = Payload(2) }, Item(partition: 2, value: 3)], []), Ct));

        var read = await ReadAsync(store, "device", 2);
        Value(read.ShouldHaveSingleItem()).ShouldBe(1);
        (await store.ReadCheckpointsAsync("state", Ct))[2].ShouldBe(5);
    }

    [Fact]
    public async Task STO_I_011_Large_snapshot_performance()
    {
        var store = (await NodeAsync(nameof(STO_I_011_Large_snapshot_performance))).GetRequiredService<IStateStore>();
        var durations = new List<TimeSpan>();
        var keys = Enumerable.Range(0, IStateStore.MaxBatchItems).Select(_ => Guid.CreateVersion7()).ToList();
        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 3, 0, [.. keys.Select(k => Item(3, 0) with { Key = k })], []), Ct); // warm-up

        // p95 of 20 runs: one slow run (other tests share the database server in CI) does not fail the test.
        for (var run = 1; run <= 20; run++)
        {
            var batch = new StateSnapshotBatch("state", 3, run, [.. keys.Select(k => Item(3, run) with { Key = k, Sequence = run })], []);
            var start = Stopwatch.GetTimestamp();
            await store.WriteSnapshotAsync(batch, Ct);
            durations.Add(Stopwatch.GetElapsedTime(start));
        }

        var p95 = durations.Order().ElementAt(18);
        p95.ShouldBeLessThan(TimeSpan.FromMilliseconds(500), $"runs: {string.Join(", ", durations.Select(d => (int)d.TotalMilliseconds))} ms");
        (await ReadAsync(store, "device", 3)).Count.ShouldBe(IStateStore.MaxBatchItems);
    }

    [Fact]
    public async Task STO_I_012_Recovery_stream_returns_requested_partitions_only()
    {
        var store = (await NodeAsync(nameof(STO_I_012_Recovery_stream_returns_requested_partitions_only))).GetRequiredService<IStateStore>();
        foreach (var partition in new[] { 1, 2, 3 })
        {
            await store.WriteSnapshotAsync(new StateSnapshotBatch("state", partition, 1, [Item(partition, partition), Item(partition, partition)], []), Ct);
        }

        var read = await ReadAsync(store, "device", 1, 3);

        read.Count.ShouldBe(4);
        read.Select(i => i.Partition).Distinct().Order().ShouldBe([1, 3]);
        (await ReadAsync(store, "alarm", 1, 2, 3)).ShouldBeEmpty();
    }

    [Fact]
    public async Task STO_I_013_Concurrency_token_enforced_in_database_round_trip()
    {
        var factory = (await NodeAsync(nameof(STO_I_013_Concurrency_token_enforced_in_database_round_trip)))
            .GetRequiredService<IDbContextFactory<CertificationContext>>();
        var id = Guid.CreateVersion7();
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            context.Items.Add(new CertItem { Id = id, Name = "a" });
            await context.SaveChangesAsync(Ct);
        }

        await using var first = await factory.CreateDbContextAsync(Ct);
        await using var second = await factory.CreateDbContextAsync(Ct);
        var a = await first.Items.AsTracking().SingleAsync(i => i.Id == id, Ct);
        var b = await second.Items.AsTracking().SingleAsync(i => i.Id == id, Ct);
        a.Name = "first";
        await first.SaveChangesAsync(Ct);
        b.Name = "second";

        var ex = await Should.ThrowAsync<StorageException>(() => second.SaveChangesAsync(Ct));

        ex.Error.Code.Value.ShouldBe(StorageErrors.ConcurrencyConflictCode);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var stored = await check.Items.SingleAsync(i => i.Id == id, Ct);
        (stored.Name, stored.RowVersion).ShouldBe(("first", 1L));
    }

    [Fact]
    public async Task STO_I_014_UTC_millisecond_precision()
    {
        var factory = (await NodeAsync(nameof(STO_I_014_UTC_millisecond_precision))).GetRequiredService<IDbContextFactory<CertificationContext>>();
        var id = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 3, 1, 10, 15, 30, TimeSpan.Zero).AddTicks(1_234_567);
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            context.Items.Add(new CertItem { Id = id, Name = "t", At = at });
            await context.SaveChangesAsync(Ct);
        }

        await using var read = await factory.CreateDbContextAsync(Ct);
        var stored = (await read.Items.SingleAsync(i => i.Id == id, Ct)).At;

        stored.ShouldBe(new DateTimeOffset(2026, 3, 1, 10, 15, 30, 123, TimeSpan.Zero));
        stored.Offset.ShouldBe(TimeSpan.Zero);
    }

    private async Task<IServiceProvider> NodeAsync(string testName)
    {
        var (services, lifetime) = await StartNodeAsync(GetType().Name + "." + testName);
        _owned.Add(lifetime);
        var startup = services.GetRequiredService<IStorageStartup>();
        await startup.WaitForDatabasesAsync(Ct);
        await startup.MigrateAsync(Ct);
        return services;
    }

    private static async Task<List<StateItem>> ReadAsync(IStateStore store, string kind, params int[] partitions)
    {
        var items = new List<StateItem>();
        await foreach (var item in store.ReadItemsAsync(kind, partitions, Ct))
        {
            items.Add(item);
        }

        return items;
    }

    private static StateItem Item(int partition, int value) => new("device", Guid.CreateVersion7(), partition, Payload(value), 10);

    private static byte[] Payload(int value) => Encoding.UTF8.GetBytes($$"""{"value":{{value}},"state":"normal"}""");

    private static int Value(StateItem item)
    {
        using var json = JsonDocument.Parse(item.PayloadJson);
        return json.RootElement.GetProperty("value").GetInt32();
    }
}
