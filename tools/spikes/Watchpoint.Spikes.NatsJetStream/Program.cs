using System.Globalization;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using Watchpoint.Spikes.NatsJetStream;

// T0.09 spike runner. Usage:
//   dotnet run -c Release --project tools/spikes/Watchpoint.Spikes.NatsJetStream -- <nats-server path> [records] [report.md]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: <nats-server path> [records = 1000000] [report file = spike-report.md]");
    return 2;
}

var server = args[0];
var records = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1_000_000;
var output = args.Length > 2 ? args[2] : "spike-report.md";
var work = Path.Combine(Path.GetTempPath(), "wp-nats-spike");
var report = new Report();
var mid = records / 5;

report.Heading("1. Single node, R1 (M1 topology), default fsync (`sync_interval` 2 min)");
report.TableHeader();
await using (var node = await NatsCluster.StartAsync(server, Path.Combine(work, "r1"), 1, 14222).ConfigureAwait(false))
{
    await using var nats = await FailoverScenarios.ConnectAsync(node.Urls).ConfigureAwait(false);
    var js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
    await LogScenarios.SequentialLatencyAsync(js, report, "R1", 5000).ConfigureAwait(false);
    foreach (var window in new[] { 64, 256, 1024 })
    {
        js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
        report.Row(string.Create(CultureInfo.InvariantCulture, $"R1: pipelined append, {window} in flight"),
            Report.Rate(mid, await LogScenarios.WindowedAppendAsync(js, mid, window).ConfigureAwait(false)));
    }

    js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
    await LogScenarios.DedupAsync(js, report).ConfigureAwait(false);
    await LogScenarios.FencingAsync(js, report).ConfigureAwait(false);

    js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
    var fenced = await LogScenarios.FencedLanesAppendAsync(js, mid).ConfigureAwait(false);
    report.Row("R1: fenced append, 64 lanes (one per partition), each awaits its ack",
        Report.Rate(mid, fenced.Elapsed) + Resolved(fenced.Resolved));

    foreach (var (batch, lanes) in new[] { (10, 32), (100, 32), (100, 64) })
    {
        await LogScenarios.ResetStreamAsync(nats, 1, atomic: true).ConfigureAwait(false);
        var (elapsed, error) = await LogScenarios.FencedAtomicBatchesAsync(nats, mid, batch, lanes).ConfigureAwait(false);
        report.Row(string.Create(CultureInfo.InvariantCulture, $"R1: fenced atomic batches of {batch}, {lanes} concurrent batches"),
            error is null ? Report.Rate(mid, elapsed) : "failed: " + error);
    }

    // Replay data set: `records` envelopes over 64 partitions.
    js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
    report.Row("R1: fill replay data set, 1024 in flight",
        Report.Rate(records, await LogScenarios.WindowedAppendAsync(js, records, 1024).ConfigureAwait(false)));
    var info = (await js.GetStreamAsync(LogScenarios.Stream).ConfigureAwait(false)).Info.State;
    report.Row("Stream size", string.Create(CultureInfo.InvariantCulture,
        $"{info.Messages:N0} records, {info.Bytes / 1024.0 / 1024.0:N0} MiB ({info.Bytes / (double)info.Messages:N0} B/record stored)"));
    await ReplaySetAsync(js, report, "warm").ConfigureAwait(false);
}

// Same data, server restarted: its block cache is cold (the OS page cache is not dropped in the sandbox).
await using (var node = await RestartAsync(server, Path.Combine(work, "r1")).ConfigureAwait(false))
{
    await using var nats = await FailoverScenarios.ConnectAsync(node.Urls).ConfigureAwait(false);
    await ReplaySetAsync(new NatsJSContext(nats), report, "after server restart").ConfigureAwait(false);
}

report.Heading("2. Single node, R1, `sync_interval: always` (fsync every write)");
report.TableHeader();
await using (var node = await NatsCluster.StartAsync(server, Path.Combine(work, "r1sync"), 1, 14222, syncAlways: true).ConfigureAwait(false))
{
    await using var nats = await FailoverScenarios.ConnectAsync(node.Urls).ConfigureAwait(false);
    var js = await LogScenarios.ResetStreamAsync(nats, 1).ConfigureAwait(false);
    await LogScenarios.SequentialLatencyAsync(js, report, "R1 fsync", 2000).ConfigureAwait(false);
    report.Row("R1 fsync: pipelined append, 1024 in flight",
        Report.Rate(mid, await LogScenarios.WindowedAppendAsync(js, mid, 1024).ConfigureAwait(false)));
}

report.Heading("3. Three nodes, R3 (CLU topology; all nodes on one host)");
report.TableHeader();
await using (var cluster = await NatsCluster.StartAsync(server, Path.Combine(work, "r3"), 3, 15222).ConfigureAwait(false))
{
    await using var nats = await FailoverScenarios.ConnectAsync(cluster.Urls).ConfigureAwait(false);
    var js = await LogScenarios.ResetStreamAsync(nats, 3).ConfigureAwait(false);
    await LogScenarios.SequentialLatencyAsync(js, report, "R3", 3000).ConfigureAwait(false);
    report.Row("R3: pipelined append, 1024 in flight",
        Report.Rate(mid, await LogScenarios.WindowedAppendAsync(js, mid, 1024).ConfigureAwait(false)));
    js = await LogScenarios.ResetStreamAsync(nats, 3).ConfigureAwait(false);
    var fenced = await LogScenarios.FencedLanesAppendAsync(js, mid / 2).ConfigureAwait(false);
    report.Row("R3: fenced append, 64 lanes", Report.Rate(mid / 2, fenced.Elapsed) + Resolved(fenced.Resolved));
    await LogScenarios.ResetStreamAsync(nats, 3, atomic: true).ConfigureAwait(false);
    var (elapsed, error) = await LogScenarios.FencedAtomicBatchesAsync(nats, mid, 100, 32).ConfigureAwait(false);
    report.Row("R3: fenced atomic batches of 100, 32 concurrent batches", error is null ? Report.Rate(mid, elapsed) : "failed: " + error);

    var kv = await KvScenarios.ResetBucketAsync(nats, 3).ConfigureAwait(false);
    await KvScenarios.SemanticsAsync(kv, report).ConfigureAwait(false);
    var connections = new List<NATS.Client.Core.NatsConnection>();
    var contenders = new List<INatsKVStore>();
    for (var i = 0; i < 6; i++)
    {
        var c = await FailoverScenarios.ConnectAsync(cluster.ClientUrl(i % 3)).ConfigureAwait(false);
        connections.Add(c);
        contenders.Add(await KvScenarios.OpenAsync(c).ConfigureAwait(false));
    }

    await KvScenarios.ContentionAsync(contenders, report, 500).ConfigureAwait(false);
    var leader = (await js.GetStreamAsync("KV_" + KvScenarios.Bucket).ConfigureAwait(false)).Info.Cluster!.Leader!;
    for (var i = 0; i < 3; i++)
    {
        var where = NatsCluster.NodeName(i) == leader ? "client on stream leader" : "client on follower";
        await KvScenarios.LatencyAsync(contenders[i], report, where + " " + NatsCluster.NodeName(i), 2000).ConfigureAwait(false);
    }

    await KvScenarios.BulkRenewAsync(kv, report, 5000, 256, 10).ConfigureAwait(false);

    foreach (var c in connections)
    {
        await c.DisposeAsync().ConfigureAwait(false);
    }
}

report.Heading("4. Node loss: leader of `WP_LOG` and `wp_leases` SIGKILLed (R3)");
report.TableHeader();
var runs = new List<LeaderLossResult>();
for (var run = 0; run < 5; run++)
{
    await using var cluster = await NatsCluster.StartAsync(server, Path.Combine(work, "fail1"), 3, 16222).ConfigureAwait(false);
    runs.Add(await FailoverScenarios.LeaderLossAsync(cluster, report, detail: run == 0).ConfigureAwait(false));
}

report.Row("5 runs: lease write outage (min / median / max)", Spread(runs.Select(r => r.LeaseOutage)));
report.Row("5 runs: log append outage (min / median / max)", Spread(runs.Select(r => r.LogOutage)));
report.Row("5 runs: restarted node catch-up (min / median / max)", Spread(runs.Select(r => r.CatchUp)));
report.Row("5 runs: acked records missing or duplicated / leases lost",
    string.Create(CultureInfo.InvariantCulture, $"{runs.Sum(r => r.LogAnomalies)} / {runs.Sum(r => r.LeasesLost)}"));

report.Heading("5. Quorum loss: 2 of 3 nodes SIGKILLed (R3)");
report.TableHeader();
await using (var cluster = await NatsCluster.StartAsync(server, Path.Combine(work, "fail2"), 3, 17222).ConfigureAwait(false))
{
    await FailoverScenarios.QuorumLossAsync(cluster, report).ConfigureAwait(false);
}

if (!LogScenarios.Conflicts.IsEmpty)
{
    report.Heading("Unexpected fencing rejections (no competing writer)");
    foreach (var c in LogScenarios.Conflicts.Take(20))
    {
        report.Line("- " + c);
    }

    report.Line(string.Create(CultureInfo.InvariantCulture, $"- total: {LogScenarios.Conflicts.Count}"));
}

await report.SaveAsync(output).ConfigureAwait(false);
return 0;

static async Task ReplaySetAsync(INatsJSContext js, Report report, string label)
{
    var state = (await js.GetStreamAsync(LogScenarios.Stream).ConfigureAwait(false)).Info.State;
    var half = state.LastSeq / 2;
    var (n, t) = await LogScenarios.ReplayAsync(js, ["wp.log.>"], 1).ConfigureAwait(false);
    report.Row("Replay whole log from seq 1, " + label, Report.Rate(n, t));
    (n, t) = await LogScenarios.ReplayAsync(js, [LogScenarios.PartitionFilter(17)], 1).ConfigureAwait(false);
    report.Row("Replay one partition (`wp.log.17.>`) from seq 1, " + label, Report.Rate(n, t));
    (n, t) = await LogScenarios.ReplayAsync(js, [LogScenarios.PartitionFilter(19)], 1, maxMsgs: 10_000).ConfigureAwait(false);
    report.Row("Replay one partition from seq 1, consumer pull batch 10,000, " + label, Report.Rate(n, t));
    (n, t) = await LogScenarios.ReplayAsync(js, ["wp.log.>"], half, maxMsgs: 10_000).ConfigureAwait(false);
    report.Row("Replay whole log from the middle, consumer pull batch 10,000, " + label, Report.Rate(n, t));
    (n, t) = await LogScenarios.ReplayAsync(js, [LogScenarios.PartitionFilter(18)], half).ConfigureAwait(false);
    report.Row("Replay one partition from the middle of the log, " + label, Report.Rate(n, t));
    (n, t) = await LogScenarios.ReplayAsync(js, [.. Enumerable.Range(32, 16).Select(p => LogScenarios.PartitionFilter(p))], 1)
        .ConfigureAwait(false);
    report.Row("Replay 16 partitions (one consumer, 16 filters) from seq 1, " + label, Report.Rate(n, t));
}

static string Resolved(int n) => n == 0
    ? string.Empty
    : string.Create(CultureInfo.InvariantCulture, $"; {n} publishes answered \"wrong last sequence\" although their own earlier attempt was stored (resolved by reading the record)");

static string Spread(IEnumerable<TimeSpan> values)
{
    var v = values.Select(t => t.TotalMilliseconds).Order().ToList();
    return Report.Ms(v[0]) + " / " + Report.Ms(v[v.Count / 2]) + " / " + Report.Ms(v[^1]);
}

static Task<NatsCluster> RestartAsync(string server, string dir) => NatsCluster.StartAsync(server, dir, 1, 14222, keepData: true);
