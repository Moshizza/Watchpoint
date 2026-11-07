using System.Diagnostics;
using System.Globalization;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace Watchpoint.Spikes.NatsJetStream;

/// <summary>KV lease store measurements (CLU §5): CAS semantics, contention, latency.</summary>
internal static class KvScenarios
{
    public const string Bucket = "wp_leases";

    public static async Task<INatsKVStore> ResetBucketAsync(NatsConnection nats, int replicas)
    {
        var kv = new NatsKVContext(new NatsJSContext(nats));
        try
        {
            await kv.DeleteStoreAsync(Bucket).ConfigureAwait(false);
        }
        catch (NatsJSApiException)
        {
        }

        return await kv.CreateStoreAsync(new NatsKVConfig(Bucket)
        {
            History = 1,
            NumberOfReplicas = replicas,
            Storage = NatsKVStorageType.File,
        }).ConfigureAwait(false);
    }

    public static Task<INatsKVStore> OpenAsync(NatsConnection nats) =>
        new NatsKVContext(new NatsJSContext(nats)).GetStoreAsync(Bucket).AsTask();

    public static string Value(string owner, long attempt) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"owner":"{{owner}}","attempt":{{attempt}},"expiresAt":"{{DateTimeOffset.UtcNow.AddSeconds(10):O}}"}""");

    public static async Task SemanticsAsync(INatsKVStore kv, Report report)
    {
        const string key = "partition.17";
        var created = await kv.TryCreateAsync(key, Value("A", 0)).ConfigureAwait(false);
        var createAgain = await kv.TryCreateAsync(key, Value("B", 0)).ConfigureAwait(false);
        var renew = await kv.TryUpdateAsync(key, Value("A", 1), created.Value).ConfigureAwait(false);
        var staleUpdate = await kv.TryUpdateAsync(key, Value("B", 1), created.Value).ConfigureAwait(false);
        await kv.DeleteAsync(key, new NatsKVDeleteOpts { Revision = renew.Value }).ConfigureAwait(false);
        var recreate = await kv.TryCreateAsync(key, Value("B", 2)).ConfigureAwait(false);
        report.Row("KV `Create` on an existing key", createAgain.Success ? "SUCCEEDED (bad)" : "rejected: " + createAgain.Error.GetType().Name);
        report.Row("KV `Update` with a stale revision", staleUpdate.Success ? "SUCCEEDED (bad)" : "rejected: " + staleUpdate.Error.GetType().Name);
        report.Row("Fencing token monotonic per key",
            string.Create(CultureInfo.InvariantCulture,
                $"create rev {created.Value} → renew rev {renew.Value} → delete (CAS) → re-create rev {recreate.Value} (revisions are bucket stream sequences: strictly increasing, also across delete/re-create)"));
    }

    /// <summary>Many contenders race for the same key: exactly one CAS must win each round.</summary>
    public static async Task ContentionAsync(IReadOnlyList<INatsKVStore> contenders, Report report, int rounds)
    {
        var createViolations = 0;
        var updateViolations = 0;
        for (var r = 0; r < rounds; r++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"race.{r}");
            var creates = await Task.WhenAll(contenders.Select((c, i) =>
                c.TryCreateAsync(key, Value("n" + i.ToString(CultureInfo.InvariantCulture), 0)).AsTask())).ConfigureAwait(false);
            if (creates.Count(x => x.Success) != 1)
            {
                createViolations++;
            }

            // Take-over of an expired lease: every contender saw the same revision.
            var rev = creates.First(x => x.Success).Value;
            var updates = await Task.WhenAll(contenders.Select((c, i) =>
                c.TryUpdateAsync(key, Value("n" + i.ToString(CultureInfo.InvariantCulture), 1), rev).AsTask())).ConfigureAwait(false);
            if (updates.Count(x => x.Success) != 1)
            {
                updateViolations++;
            }
        }

        report.Row(string.Create(CultureInfo.InvariantCulture, $"CAS contention: {contenders.Count} clients on 3 nodes, {rounds} rounds"),
            string.Create(CultureInfo.InvariantCulture,
                $"rounds with ≠ 1 winner: create {createViolations}, take-over update {updateViolations}"));
    }

    public static async Task LatencyAsync(INatsKVStore kv, Report report, string label, int count)
    {
        var key = "latency." + label.Replace(' ', '_');
        var rev = (await kv.TryCreateAsync(key, Value("A", 0)).ConfigureAwait(false)).Value;
        var samples = new List<double>(count);
        for (var i = 1; i <= count; i++)
        {
            var sw = Stopwatch.StartNew();
            rev = await kv.UpdateAsync(key, Value("A", i), rev).ConfigureAwait(false);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        report.Row("Lease renew (CAS update) latency, " + label, Report.Latency(samples));
    }

    /// <summary>One renewal round of many leases (CLU-I-006: 5,000 leases every 2 s), pipelined CAS updates.</summary>
    public static async Task BulkRenewAsync(INatsKVStore kv, Report report, int leases, int window, int rounds)
    {
        var revs = new ulong[leases];
        var keys = Enumerable.Range(0, leases).Select(i => string.Create(CultureInfo.InvariantCulture, $"connection.{i}")).ToArray();
        await RunWindowedAsync(leases, window, async i => revs[i] = await kv.CreateAsync(keys[i], Value("A", 0)).ConfigureAwait(false))
            .ConfigureAwait(false);
        var samples = new List<double>();
        for (var r = 1; r <= rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            await RunWindowedAsync(leases, window, async i => revs[i] = await kv.UpdateAsync(keys[i], Value("A", r), revs[i]).ConfigureAwait(false))
                .ConfigureAwait(false);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        report.Row(string.Create(CultureInfo.InvariantCulture, $"Renewal round of {leases:N0} leases, {window} CAS in flight ({rounds} rounds)"),
            Report.Latency(samples));
    }

    private static async Task RunWindowedAsync(int count, int window, Func<int, Task> work)
    {
        using var gate = new SemaphoreSlim(window);
        var tasks = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await work(index).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
