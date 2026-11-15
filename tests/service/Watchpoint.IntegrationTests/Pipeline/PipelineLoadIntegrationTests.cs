using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.IntegrationTests.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Pipeline;

/// <summary>
/// Throughput, latency and flooding of the ingest on real NATS (spec PIP §14). Wall-clock tests: they run one at a
/// time and take their limits from <see cref="TimingLimits"/> (TST §4.7).
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "PIP")]
[Collection(SerialTiming.Name)]
public sealed class PipelineLoadIntegrationTests(NatsContainerFixture nats)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PIP_I_010_Throughput_target()
    {
        const int Devices = 1000;
        const int Observations = 50_000;
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_010_Throughput_target));
        var directory = new MemoryDirectory();

        // 25 connections of 40 devices: 2,000 observations each, within the burst of one connection (20,000).
        var connections = Enumerable.Range(0, 25).Select(_ => ConnectionId.New()).ToList();
        var devices = Enumerable.Range(0, Devices).Select(i => (Device: directory.AddDoor(connections[i % 25]), Connection: connections[i % 25])).ToList();
        var state = new StateStandIn();
        await using var node = await IngestNode.StartAsync(nats, scope, directory, Ct, configure: s => s.AddSingleton<IPartitionSubscriber>(state));
        var ingest = node.Services.GetRequiredService<IObservationIngest>();

        // 50 rounds; in each, every device reports a new position. One batch per connection and round.
        string[] codes = ["open", "closed"];
        var started = Stopwatch.GetTimestamp();
        var accepted = 0;
        await Parallel.ForEachAsync(connections, Ct, async (connection, ct) =>
        {
            var own = devices.Where(d => d.Connection == connection).Select(d => d.Device).ToList();
            for (var round = 0; round < Observations / Devices; round++)
            {
                var result = await ingest.IngestAsync([.. own.Select(d => IngestNode.Property(d, connection, codes[round % 2]))], ct);
                result.Rejected.ShouldBeEmpty();
                Interlocked.Add(ref accepted, result.Accepted);
            }
        });
        var elapsed = Stopwatch.GetElapsedTime(started);

        accepted.ShouldBe(Observations);
        state.Records.Count.ShouldBe(Observations);
        var rate = Observations / elapsed.TotalSeconds;

        // 5,000/s is the target (strict on a developer machine); a shared CI runner must reach 1,000/s.
        var limit = TimingLimits.For(TimeSpan.FromSeconds(Observations / 5000), TimeSpan.FromSeconds(Observations / 1000));
        if (rate < 5000)
        {
            TestContext.Current.AddWarning($"PIP-I-010: {rate:0} observations/s is below the target of 5,000/s.");
        }

        elapsed.ShouldBeLessThan(limit, $"{Observations} observations took {elapsed.TotalSeconds:0.0} s ({rate:0}/s)");
    }

    [Fact]
    public async Task PIP_I_011_Latency_target()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_011_Latency_target));
        var directory = new MemoryDirectory();
        var connection = ConnectionId.New();
        var devices = Enumerable.Range(0, 200).Select(_ => directory.AddDoor(connection)).ToList();
        var done = new ConcurrentDictionary<Guid, long>();
        var state = new StateStandIn
        {
            OnRecordsHook = (_, records) =>
            {
                var now = Stopwatch.GetTimestamp();
                foreach (var record in records)
                {
                    done[record.Envelope.Id] = now;
                }
            },
        };
        await using var node = await IngestNode.StartAsync(nats, scope, directory, Ct, configure: s => s.AddSingleton<IPartitionSubscriber>(state));
        var ingest = node.Services.GetRequiredService<IObservationIngest>();

        // 1,000 observations per second for four seconds: ten every 10 ms, each for another device.
        var sent = new ConcurrentDictionary<Guid, long>();
        var calls = new List<Task<IngestResult>>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10), TimeProvider.System);
        for (var tick = 0; tick < 400; tick++)
        {
            await timer.WaitForNextTickAsync(Ct);
            var batch = Enumerable.Range(0, 10).Select(k => IngestNode.Event(devices[((tick * 10) + k) % devices.Count], connection)).ToList();
            var received = Stopwatch.GetTimestamp();
            foreach (var observation in batch)
            {
                sent[observation.Id] = received;
            }

            calls.Add(ingest.IngestAsync(batch, Ct).AsTask());
        }

        var results = await Task.WhenAll(calls).WaitAsync(TestTimeouts.Container, Ct);
        results.Sum(r => r.Accepted).ShouldBe(4000);

        // Received → subscribers done, per observation; the first second warms up connections and code.
        var latencies = sent.Keys
            .OrderBy(id => sent[id])
            .Skip(1000)
            .Select(id => Stopwatch.GetElapsedTime(sent[id], done[id]).TotalMilliseconds)
            .Order()
            .ToList();
        var p95 = latencies[(int)(latencies.Count * 0.95)];
        var limit = TimingLimits.For(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500));
        p95.ShouldBeLessThan(limit.TotalMilliseconds, $"p95 latency was {p95:0.0} ms (median {latencies[latencies.Count / 2]:0.0} ms)");
    }

    [Fact]
    public async Task PIP_I_014_Coalescing_under_real_load()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_014_Coalescing_under_real_load));
        var directory = new MemoryDirectory();
        var connection = ConnectionId.New();
        var devices = Enumerable.Range(0, 20).Select(_ => directory.AddDoor(connection)).ToList();
        var flooding = new FloodingSignals();

        // The quota scaled down: 200 observations per second, burst 200; flooding after 2 s of coalescing.
        var options = new IngestOptions { ConnectionRate = 200, ConnectionBurst = 200, FloodingAfter = TimeSpan.FromSeconds(2) };
        await using var node = await IngestNode.StartAsync(
            nats, scope, directory, Ct, options, s => s.AddSingleton<IIngestFloodingListener>(flooding));
        var ingest = node.Services.GetRequiredService<IObservationIngest>();
        var quotaStart = Stopwatch.GetTimestamp();

        // The flood: ten times the quota for five seconds. Every 50 ms a batch of 100: 95 position updates over the
        // 20 doors and 5 events, sent without waiting for the previous batch (like a sink that keeps flushing).
        string[] codes = ["open", "closed", "heldOpen", "forced"];
        var calls = new List<Task<IngestResult>>();
        var events = new List<(DeviceId Device, Guid Id)>();
        var lastValue = new Dictionary<DeviceId, string>();
        var sentProperties = 0;
        using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), TimeProvider.System))
        {
            for (var tick = 0; tick < 100; tick++)
            {
                await timer.WaitForNextTickAsync(Ct);
                var batch = new List<Observation>(100);
                for (var k = 0; k < 95; k++)
                {
                    var device = devices[k % devices.Count];
                    var code = codes[(tick + k + (k / devices.Count)) % codes.Length];
                    batch.Add(IngestNode.Property(device, connection, code));
                    lastValue[device] = code;
                    sentProperties++;
                }

                for (var k = 0; k < 5; k++)
                {
                    var observation = IngestNode.Event(devices[(tick + k) % devices.Count], connection);
                    batch.Add(observation);
                    events.Add((observation.Device!.Value, observation.Id));
                }

                calls.Add(ingest.IngestAsync(batch, Ct).AsTask());
            }
        }

        // The fault is raised while the flood lasts.
        flooding.Signals.ShouldBe([true]);
        node.Logs.WithEventId(1630).ShouldHaveSingleItem();
        node.Logs.WithEventId(1631).ShouldHaveSingleItem();

        // Everything completes: the buffer drains at the pace of the quota once the flood is over.
        var results = await Task.WhenAll(calls).WaitAsync(TimingLimits.For(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)), Ct);
        var elapsed = Stopwatch.GetElapsedTime(quotaStart);
        await Eventually.UntilAsync(() => flooding.Signals.Count == 2, TestTimeouts.Container, "the flooding fault was not cleared", ct: Ct);
        flooding.Signals.ShouldBe([true, false]);

        var registry = node.Registry;
        var logged = new List<LogRecord>();
        await foreach (var record in node.Reader.ReadPartitionsAsync(Enumerable.Range(0, Partitioning.PartitionCount).ToHashSet(), 1, Ct))
        {
            logged.Add(record);
        }

        // Events complete and, per device, in the order they were reported.
        results.SelectMany(r => r.Rejected).ShouldBeEmpty();
        var loggedEvents = logged.Where(r => r.Envelope.Kind == RecordKinds.Event).ToList();
        loggedEvents.Count.ShouldBe(events.Count);
        foreach (var device in devices)
        {
            loggedEvents.Where(r => r.Envelope.Dev == device).Select(r => r.Envelope.Id)
                .ShouldBe(events.Where(e => e.Device == device).Select(e => e.Id));
        }

        // Properties coalesced: most updates never reached the log, but the latest value of every door did.
        var coalesced = results.Sum(r => r.Coalesced);
        var properties = logged.Where(r => r.Envelope.Kind == RecordKinds.Property).ToList();
        coalesced.ShouldBeGreaterThan(sentProperties / 2);
        (results.Sum(r => r.Accepted) + results.Sum(r => r.Unchanged) + coalesced).ShouldBe(sentProperties + events.Count);
        foreach (var device in devices)
        {
            properties.Last(r => r.Envelope.Dev == device).BodyAs<PropertyRecordBody>(registry).Value.ShouldBe(WpValue.Enum(lastValue[device]));
        }

        // The quota held: the log never took more than the burst plus the rate over the whole time.
        logged.Count.ShouldBeLessThanOrEqualTo(options.ConnectionBurst + (int)(options.ConnectionRate * (elapsed.TotalSeconds + 1)));
    }
}
