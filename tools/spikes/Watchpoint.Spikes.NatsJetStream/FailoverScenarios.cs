using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;

namespace Watchpoint.Spikes.NatsJetStream;

/// <summary>Node loss on a 3-node R3 cluster: lease renewals and log appends while nodes are SIGKILLed (CLU §5, §7).</summary>
internal sealed record LeaderLossResult(TimeSpan LeaseOutage, TimeSpan LogOutage, int LogAnomalies, int LeasesLost, TimeSpan CatchUp);

internal static class FailoverScenarios
{
    private static readonly TimeSpan _opTimeout = TimeSpan.FromSeconds(1);
    private const string LeaseKey = "partition.17";

    private sealed class Timeline
    {
        public ConcurrentQueue<(TimeSpan At, bool Ok)> Ops { get; } = new();

        public int Ambiguous;
        public int Lost;
        public int Retries;

        public void Add(Stopwatch clock, bool ok) => Ops.Enqueue((clock.Elapsed, ok));

        /// <summary>Largest gap between two consecutive successes, for gaps ending after <paramref name="from"/>.</summary>
        public TimeSpan OutageAfter(TimeSpan from)
        {
            var ok = Ops.Where(o => o.Ok).Select(o => o.At).OrderBy(t => t).ToList();
            var max = TimeSpan.Zero;
            for (var i = 1; i < ok.Count; i++)
            {
                if (ok[i] > from && ok[i] - ok[i - 1] > max)
                {
                    max = ok[i] - ok[i - 1];
                }
            }

            return max;
        }

        public TimeSpan? FirstFailureAfter(TimeSpan from)
        {
            var f = Ops.Where(o => !o.Ok && o.At > from).Select(o => o.At).OrderBy(t => t).ToList();
            return f.Count == 0 ? null : f[0] - from;
        }

        public TimeSpan? FirstSuccessAfter(TimeSpan from)
        {
            var s = Ops.Where(o => o.Ok && o.At > from).Select(o => o.At).OrderBy(t => t).ToList();
            return s.Count == 0 ? null : s[0] - from;
        }

        public int SuccessesBetween(TimeSpan from, TimeSpan to) => Ops.Count(o => o.Ok && o.At > from && o.At < to);
    }

    /// <summary>Kills the leader of both streams; returns the outages and loss counters (rows only when <paramref name="detail"/>).</summary>
    public static async Task<LeaderLossResult> LeaderLossAsync(NatsCluster cluster, Report report, bool detail)
    {
        await using var admin = await ConnectAsync(cluster.Urls).ConfigureAwait(false);
        var js = await LogScenarios.ResetStreamAsync(admin, 3).ConfigureAwait(false);
        var kv = await KvScenarios.ResetBucketAsync(admin, 3).ConfigureAwait(false);
        var kvLeader = await LeaderAsync(js, "KV_" + KvScenarios.Bucket).ConfigureAwait(false);
        var logLeader = await LeaderAsync(js, LogScenarios.Stream).ConfigureAwait(false);
        // Kill the leader of both streams at once (worst case): re-create the log stream until its leader is the KV leader.
        for (var i = 0; i < 20 && logLeader != kvLeader; i++)
        {
            js = await LogScenarios.ResetStreamAsync(admin, 3).ConfigureAwait(false);
            logLeader = await LeaderAsync(js, LogScenarios.Stream).ConfigureAwait(false);
        }

        var victim = Enumerable.Range(0, cluster.Size).First(i => NatsCluster.NodeName(i) == kvLeader);
        var survivor = (victim + 1) % cluster.Size;

        await using var client = await ConnectAsync(cluster.ClientUrl(survivor), fast: true).ConfigureAwait(false);
        var clientJs = new NatsJSContext(client);
        var clientKv = await KvScenarios.OpenAsync(client).ConfigureAwait(false);
        var rev = (await clientKv.CreateAsync(LeaseKey, KvScenarios.Value("A", 0)).ConfigureAwait(false));

        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var leases = new Timeline();
        var log = new Timeline();
        var acked = new ConcurrentDictionary<string, ulong>();
        var renewer = RenewLoopAsync(clientKv, rev, leases, clock, stop.Token);
        var appender = AppendLoopAsync(clientJs, log, acked, clock, stop.Token);

        await Task.Delay(2000).ConfigureAwait(false);
        var killedAt = clock.Elapsed;
        cluster.Kill(victim);
        await Task.Delay(12000).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(renewer, appender).ConfigureAwait(false);

        var newKvLeader = await LeaderAsync(new NatsJSContext(client), "KV_" + KvScenarios.Bucket).ConfigureAwait(false);
        var newLogLeader = await LeaderAsync(new NatsJSContext(client), LogScenarios.Stream).ConfigureAwait(false);
        Detail(detail, report, "Killed node", string.Create(CultureInfo.InvariantCulture,
            $"{kvLeader} (leader of `KV_wp_leases`; `WP_LOG` leader was {logLeader}); clients connected to {NatsCluster.NodeName(survivor)}; new leaders: KV {newKvLeader}, log {newLogLeader}"));
        Detail(detail, report, "Failed operations after the kill", string.Create(CultureInfo.InvariantCulture,
            $"renewals {leases.Ops.Count(o => !o.Ok && o.At > killedAt)}, appends {log.Ops.Count(o => !o.Ok && o.At > killedAt)}"));
        Detail(detail, report, "Lease renewals (every 100 ms, 1 s timeout): longest gap between successes", Report.Ms(leases.OutageAfter(killedAt).TotalMilliseconds));
        Detail(detail, report, "Lease renewals: timed out but actually committed (resolved by re-reading the key)",
            leases.Ambiguous.ToString(CultureInfo.InvariantCulture));
        Detail(detail, report, "Lease renewals: lost to another writer (must be 0)", leases.Lost.ToString(CultureInfo.InvariantCulture));
        Detail(detail, report, "Log appends (sequential, 1 s timeout, retry same `Nats-Msg-Id`): longest gap", Report.Ms(log.OutageAfter(killedAt).TotalMilliseconds));
        Detail(detail, report, "Log appends: retries", log.Retries.ToString(CultureInfo.InvariantCulture));

        // Every acknowledged record must be in the log exactly once.
        var (present, duplicates) = await ReadIdsAsync(new NatsJSContext(admin)).ConfigureAwait(false);
        var missing = acked.Keys.Count(id => !present.Contains(id));
        Detail(detail, report, "Acknowledged records after failover", string.Create(CultureInfo.InvariantCulture,
            $"{acked.Count:N0} acked, {missing} missing, {duplicates} duplicated record ids in the stream"));

        var restartedAt = Stopwatch.StartNew();
        cluster.Start(victim);
        await cluster.WaitReadyAsync().ConfigureAwait(false);
        var caughtUp = await WaitReplicasCurrentAsync(new NatsJSContext(admin), LogScenarios.Stream, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Detail(detail, report, "Restarted node: process start → `WP_LOG` replicas all current",
            caughtUp ? Report.Ms(restartedAt.Elapsed.TotalMilliseconds) : "not current after 60 s");
        return new LeaderLossResult(leases.OutageAfter(killedAt), log.OutageAfter(killedAt), missing + duplicates, leases.Lost,
            caughtUp ? restartedAt.Elapsed : TimeSpan.MaxValue);
    }

    private static void Detail(bool detail, Report report, string metric, string value)
    {
        if (detail)
        {
            report.Row(metric, value);
        }
    }

    public static async Task QuorumLossAsync(NatsCluster cluster, Report report)
    {
        await using var admin = await ConnectAsync(cluster.Urls).ConfigureAwait(false);
        var kv = await KvScenarios.ResetBucketAsync(admin, 3).ConfigureAwait(false);
        const int survivor = 0;
        await using var client = await ConnectAsync(cluster.ClientUrl(survivor), fast: true).ConfigureAwait(false);
        var clientKv = await KvScenarios.OpenAsync(client).ConfigureAwait(false);
        var rev = await clientKv.CreateAsync(LeaseKey, KvScenarios.Value("A", 0)).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource();
        var leases = new Timeline();
        var renewer = RenewLoopAsync(clientKv, rev, leases, clock, stop.Token);
        await Task.Delay(2000).ConfigureAwait(false);

        var killedAt = clock.Elapsed;
        cluster.Kill(1);
        cluster.Kill(2);
        await Task.Delay(8000).ConfigureAwait(false);
        var restartAt = clock.Elapsed;
        cluster.Start(1);
        await Task.Delay(15000).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await renewer.ConfigureAwait(false);
        cluster.Start(2);
        await cluster.WaitReadyAsync().ConfigureAwait(false);

        var firstFail = leases.FirstFailureAfter(killedAt);
        report.Row("2 of 3 nodes killed: time to first failed renewal",
            firstFail is null ? "none (bad)" : Report.Ms(firstFail.Value.TotalMilliseconds));
        report.Row("Successful renewals while 2 of 3 down (from +200 ms, must be 0)",
            leases.SuccessesBetween(killedAt + TimeSpan.FromMilliseconds(200), restartAt).ToString(CultureInfo.InvariantCulture));
        var firstOk = leases.FirstSuccessAfter(restartAt);
        report.Row("One node restarted: process start → first successful renewal",
            firstOk is null ? "none within 15 s" : Report.Ms(firstOk.Value.TotalMilliseconds));
        report.Row("Lease renewals lost to another writer (must be 0)", leases.Lost.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<string> LeaderAsync(INatsJSContext js, string stream)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                var leader = (await js.GetStreamAsync(stream).ConfigureAwait(false)).Info.Cluster?.Leader;
                if (!string.IsNullOrEmpty(leader) || i > 100)
                {
                    return leader ?? "?";
                }
            }
            catch (Exception e) when (e is NatsException or TimeoutException)
            {
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private static async Task RenewLoopAsync(INatsKVStore kv, ulong rev, Timeline t, Stopwatch clock, CancellationToken stop)
    {
        long attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            attempt++;
            var value = KvScenarios.Value("A", attempt);
            try
            {
                using var cts = new CancellationTokenSource(_opTimeout);
                var r = await kv.TryUpdateAsync(LeaseKey, value, rev, cancellationToken: cts.Token).ConfigureAwait(false);
                if (r.Success)
                {
                    rev = r.Value;
                    t.Add(clock, true);
                }
                else
                {
                    t.Add(clock, false);
                    if (r.Error is NatsKVWrongLastRevisionException)
                    {
                        // An earlier attempt timed out on our side but was committed: adopt its revision if it is ours.
                        using var cts2 = new CancellationTokenSource(_opTimeout);
                        var entry = await kv.GetEntryAsync<string>(LeaseKey, cancellationToken: cts2.Token).ConfigureAwait(false);
                        if (entry.Value is { } v && v.Contains("\"owner\":\"A\"", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref t.Ambiguous);
                            rev = entry.Revision;
                        }
                        else
                        {
                            Interlocked.Increment(ref t.Lost);
                        }
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or NatsException or TimeoutException)
            {
                t.Add(clock, false);
            }

            try
            {
                await Task.Delay(100, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task AppendLoopAsync(NatsJSContext js, Timeline t, ConcurrentDictionary<string, ulong> acked, Stopwatch clock,
        CancellationToken stop)
    {
        var i = 0;
        while (!stop.IsCancellationRequested)
        {
            var id = Guid.CreateVersion7();
            var p = i++ % LogScenarios.Partitions;
            var data = LogScenarios.Envelope(id, p);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var cts = new CancellationTokenSource(_opTimeout);
                    var ack = await js.PublishAsync(LogScenarios.Subject(p), data, opts: new NatsJSPubOpts { MsgId = id.ToString() },
                        cancellationToken: cts.Token).ConfigureAwait(false);
                    ack.EnsureSuccess();
                    acked[id.ToString()] = ack.Seq;
                    t.Add(clock, true);
                    break;
                }
                catch (Exception e) when (e is OperationCanceledException or NatsException or TimeoutException)
                {
                    t.Add(clock, false);
                    Interlocked.Increment(ref t.Retries);
                }
            }

            try
            {
                await Task.Delay(2, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task<(HashSet<string> Ids, int Duplicates)> ReadIdsAsync(NatsJSContext js)
    {
        var ids = new HashSet<string>();
        var duplicates = 0;
        var info = await js.GetStreamAsync(LogScenarios.Stream).ConfigureAwait(false);
        if (info.Info.State.Messages == 0)
        {
            return (ids, 0);
        }

        var consumer = await js.CreateOrderedConsumerAsync(LogScenarios.Stream).ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: cts.Token).ConfigureAwait(false))
        {
            var json = System.Text.Encoding.UTF8.GetString(msg.Data!);
            var id = json.Substring(7, 36);
            if (!ids.Add(id))
            {
                duplicates++;
            }

            if (msg.Metadata!.Value.NumPending == 0)
            {
                break;
            }
        }

        return (ids, duplicates);
    }

    private static async Task<bool> WaitReplicasCurrentAsync(NatsJSContext js, string stream, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                var info = await js.GetStreamAsync(stream).ConfigureAwait(false);
                var replicas = info.Info.Cluster?.Replicas;
                if (replicas is { Count: 2 } && replicas.All(r => r.Current && !r.Offline))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is NatsException or TimeoutException)
            {
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Connects; <paramref name="fast"/> uses the 1 s operation timeout of the lease and append loops.</summary>
    public static async Task<NatsConnection> ConnectAsync(string urls, bool fast = false)
    {
        var opts = fast
            ? NatsOpts.Default with { Url = urls, RequestTimeout = _opTimeout, ConnectTimeout = _opTimeout }
            : NatsOpts.Default with { Url = urls, RequestTimeout = TimeSpan.FromSeconds(10) };
        var c = new NatsConnection(opts);
        await c.ConnectAsync().ConfigureAwait(false);
        return c;
    }
}
