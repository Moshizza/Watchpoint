using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Watchpoint.Spikes.ConnectorIsolation.Contract;
using Watchpoint.Spikes.ConnectorIsolation.Protocol;
using Watchpoint.Spikes.ConnectorIsolation.Runner;

// Child mode: load the connector in-process, make it misbehave, report whether the process survived.
if (args is ["--inproc-misbehave", var mode])
{
    var (_, victim) = PackageLoadContext.LoadConnector(Layout.ConnectorAssembly);
    victim.Initialize(new Ingest().CreateDirectSink());
    victim.Misbehave(Enum.Parse<Misbehaviour>(mode));
    Thread.Sleep(2000);
    Console.WriteLine("survived");
    return 0;
}

var quick = args.Contains("--quick");
var outFile = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault();
var burst = quick ? 200_000 : 2_000_000;
var pacedSeconds = quick ? 1 : 3;
var commands = quick ? 2_000 : 20_000;
var startRuns = quick ? 3 : 10;

var report = new StringBuilder();
void Line(string text = "")
{
    report.AppendLine(text);
    Console.WriteLine(text);
}

string F(double v) => Stats.F(v);
string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

Line("# Spike T0.11 — measurements");
Line();
Line(Inv($"- Date (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}"));
Line(Inv($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); .NET {Environment.Version}; {Environment.ProcessorCount} logical CPUs"));
Line(Inv($"- CPU: {File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':')[1].Trim()}"));
Line(Inv($"- Counts: burst {burst:N0} reports, paced {pacedSeconds} s per rate, {commands:N0} commands, {startRuns} host starts"));
Line();

var ingest = new Ingest();
await using var server = await HostLinkServer.StartAsync(ingest);

// ---------------------------------------------------------------- load and start
Line("## 1. Load, start and memory");
Line();
var nodeRssBefore = IsolatedHost.ReadResidentMb(Environment.ProcessId);
var loadWatch = Stopwatch.StartNew();
var (inprocContext, inproc) = PackageLoadContext.LoadConnector(Layout.ConnectorAssembly);
inproc.Initialize(ingest.CreateDirectSink());
var inprocLoadMs = loadWatch.Elapsed.TotalMilliseconds;
await inproc.ExecuteAsync("warm-up", CancellationToken.None);
var nodeRssAfter = IsolatedHost.ReadResidentMb(Environment.ProcessId);

var starts = new List<double>();
var hostLoads = new List<double>();
var rss = new List<double>();
var rssInvariant = new List<double>();
for (var i = 0; i < startRuns; i++)
{
    await using var host = await IsolatedHost.StartAsync(server, "eager", invariantGlobalization: i % 2 == 1);
    starts.Add(host.StartToReadyMs);
    hostLoads.Add(host.Session.Hello!.LoadMs);
    if (i < 2)
    {
        await Task.Delay(3000);
        (i == 0 ? rss : rssInvariant).Add(host.ResidentMb());
    }
}

starts.Sort();
Line("| Metric | Value |");
Line("|---|---|");
Line(Inv($"| In-process: load context + connector load (first, cold) | {F(inprocLoadMs)} ms |"));
Line(Inv($"| In-process: node RSS growth after loading the connector | {F(nodeRssAfter - nodeRssBefore)} MB |"));
Line(Inv($"| Isolated: process start → Hello, median / max of {startRuns} | {F(starts[starts.Count / 2])} / {F(starts[^1])} ms |"));
Line(Inv($"| Isolated: connector load inside the host (median) | {F(hostLoads.Order().ElementAt(hostLoads.Count / 2))} ms |"));
Line(Inv($"| Isolated: idle host RSS (ICU globalization) | {F(rss[0])} MB |"));
Line(Inv($"| Isolated: idle host RSS (invariant globalization) | {F(rssInvariant[0])} MB |"));
Line();

// ---------------------------------------------------------------- throughput
Line("## 2. Observation throughput (burst, connector reports as fast as it can)");
Line();
Line("| Mode | reports/s (median of 3) |");
Line("|---|---|");

async Task<double> BurstInProcess()
{
    var done = ingest.Expect(burst);
    var watch = Stopwatch.StartNew();
    await inproc.EmitAsync(burst, 0, CancellationToken.None);
    await done;
    return burst / watch.Elapsed.TotalSeconds;
}

async Task<double> BurstIsolated(HostSession session)
{
    var done = ingest.Expect(burst);
    var watch = Stopwatch.StartNew();
    _ = session.SendAsync(new Request { Emit = new Emit { Count = burst } });
    await done;
    return burst / watch.Elapsed.TotalSeconds;
}

static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

var inprocRates = new List<double>();
for (var i = 0; i < 3; i++)
{
    inprocRates.Add(await BurstInProcess());
}

Line(Inv($"| In-process | {Median(inprocRates):N0} |"));
foreach (var batching in new[] { "eager", "timer" })
{
    await using var host = await IsolatedHost.StartAsync(server, batching);
    var rates = new List<double>();
    for (var i = 0; i < 3; i++)
    {
        rates.Add(await BurstIsolated(host.Session));
    }

    Line(Inv($"| Isolated, batching `{batching}` | {Median(rates):N0} |"));
}

Line();

// ---------------------------------------------------------------- latency
Line("## 3. Latency report → ingest (paced load)");
Line();
Line("| Rate | Mode | p50 ms | p99 ms | p99.9 ms | max ms |");
Line("|---|---|---|---|---|---|");
foreach (var rate in new[] { 1_000, 10_000, 50_000 })
{
    var count = rate * pacedSeconds;
    var done = ingest.Expect(count);
    await inproc.EmitAsync(count, rate, CancellationToken.None);
    await done;
    Line(Inv($"| {rate:N0}/s | In-process | {Stats.FromTicks(ingest.Latencies).Row} |"));
    foreach (var batching in new[] { "eager", "timer" })
    {
        await using var host = await IsolatedHost.StartAsync(server, batching);
        done = ingest.Expect(count);
        _ = host.Session.SendAsync(new Request { Emit = new Emit { Count = count, RatePerSecond = rate } });
        await done;
        Line(Inv($"| {rate:N0}/s | Isolated, `{batching}` | {Stats.FromTicks(ingest.Latencies).Row} |"));
    }
}

Line();

// ---------------------------------------------------------------- commands
Line("## 4. Command round-trip (sequential, node → connector → node)");
Line();
Line("| Mode | p50 ms | p99 ms | p99.9 ms | max ms |");
Line("|---|---|---|---|---|");
var samples = new long[commands];
for (var i = 0; i < commands; i++)
{
    var t = Stopwatch.GetTimestamp();
    await inproc.ExecuteAsync("ping", CancellationToken.None);
    samples[i] = Stopwatch.GetTimestamp() - t;
}

Line(Inv($"| In-process | {Stats.FromTicks(samples).Row} |"));
await using (var host = await IsolatedHost.StartAsync(server, "eager"))
{
    for (var i = 0; i < commands; i++)
    {
        var t = Stopwatch.GetTimestamp();
        await host.Session.SendAsync(new Request { Execute = new Execute { Command = "ping" } });
        samples[i] = Stopwatch.GetTimestamp() - t;
    }

    Line(Inv($"| Isolated | {Stats.FromTicks(samples).Row} |"));
}

Line();

// ---------------------------------------------------------------- faults
Line("## 5. Connector faults");
Line();
Line("| Fault | In-process (node process) | Isolated: node detects link loss after | Isolated: node afterwards |");
Line("|---|---|---|---|");
foreach (var kind in new[] { Misbehaviour.BackgroundThreadException, Misbehaviour.StackOverflow, Misbehaviour.FailFast, Misbehaviour.Hang })
{
    var child = Process.Start(new ProcessStartInfo(Layout.RunnerExecutable, ["--inproc-misbehave", kind.ToString()])
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    })!;
    var stdout = child.StandardOutput.ReadToEndAsync();
    _ = child.StandardError.ReadToEndAsync();
    await child.WaitForExitAsync();
    var survived = (await stdout).Contains("survived", StringComparison.Ordinal);
    var inprocOutcome = survived ? "survives" : Inv($"**node process dies** (exit code {child.ExitCode})");
    if (kind == Misbehaviour.Hang)
    {
        inprocOutcome = "survives, one core spinning until the node restarts (threads cannot be aborted)";
    }

    await using var host = await IsolatedHost.StartAsync(server, "eager");
    var sent = Stopwatch.GetTimestamp();
    _ = host.Session.SendAsync(new Request { Misbehave = (int)kind }).ContinueWith(_ => { }, TaskScheduler.Default);
    string detected;
    string after;
    if (kind == Misbehaviour.Hang)
    {
        await Task.Delay(3000);
        var stillAnswers = await host.Session.SendAsync(new Request { Execute = new Execute { Command = "ping" } }).WaitAsync(TimeSpan.FromSeconds(2)) == "ping";
        var heartbeatAge = Stopwatch.GetElapsedTime(host.Session.LastStatus).TotalSeconds;
        var kill = Stopwatch.GetTimestamp();
        host.Process.Kill(entireProcessTree: true);
        await host.Session.Ended.WaitAsync(TimeSpan.FromSeconds(10));
        detected = Inv($"not detected by the link: heartbeat age {F(heartbeatAge)} s, commands answered: {stillAnswers}");
        after = Inv($"host killed by the node, link loss seen after {F(Stopwatch.GetElapsedTime(kill).TotalMilliseconds)} ms");
    }
    else
    {
        await host.Session.Ended.WaitAsync(TimeSpan.FromSeconds(20));
        detected = Inv($"{F((host.Session.EndedAt - sent) * 1000.0 / Stopwatch.Frequency)} ms");
        var ok = await inproc.ExecuteAsync("still alive", CancellationToken.None) == "still alive";
        await using var restarted = await IsolatedHost.StartAsync(server, "eager");
        after = Inv($"keeps running ({(ok ? "in-process work unaffected" : "?")}); new host Ready in {F(restarted.StartToReadyMs)} ms");
    }

    Line(Inv($"| {kind} | {inprocOutcome} | {detected} | {after} |"));
}

Line();

// ---------------------------------------------------------------- unload
Line("## 6. Unloading the in-process load context");
Line();
Line("| Case | Collected | GC rounds | Time ms |");
Line("|---|---|---|---|");
inprocContext.Unload();
inproc = null!;
inprocContext = null!;
foreach (var leak in new[] { false, true })
{
    var (weak, unloadStart) = LoadRunAndUnload(ingest, leak);
    var rounds = 0;
    while (weak.IsAlive && rounds < 20)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        rounds++;
    }

    Line(Inv($"| {(leak ? "Connector subscribed to a static event of a shared assembly" : "Clean connector")} | {(!weak.IsAlive ? "yes" : "**no**")} | {rounds} | {F(Stopwatch.GetElapsedTime(unloadStart).TotalMilliseconds)} |"));
}

Line();
if (outFile is not null)
{
    await File.WriteAllTextAsync(outFile, report.ToString());
}

return 0;

[MethodImpl(MethodImplOptions.NoInlining)]
static (WeakReference Context, long UnloadStart) LoadRunAndUnload(Ingest ingest, bool leak)
{
    var (context, connector) = PackageLoadContext.LoadConnector(Layout.ConnectorAssembly);
    connector.Initialize(ingest.CreateDirectSink());
    var done = ingest.Expect(10_000);
    connector.EmitAsync(10_000, 0, CancellationToken.None).GetAwaiter().GetResult();
    done.GetAwaiter().GetResult();
    if (leak)
    {
        connector.Misbehave(Misbehaviour.LeakIntoSharedType);
    }

    var start = Stopwatch.GetTimestamp();
    context.Unload();
    return (new WeakReference(context), start);
}
