using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Pipeline;

[Trait("layer", "I")]
[Trait("spec", "PIP")]
public sealed class PipelineLogIntegrationTests(NatsContainerFixture nats)
{
    private const long Mb = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PIP_I_001_Streams_provisioned_on_real_NATS()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_001_Streams_provisioned_on_real_NATS));

        await using var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct);

        await using var client = new NatsClient(nats.Url);
        var js = client.CreateJetStreamContext();
        foreach (var (stream, subjects, maxAge, maxBytes) in new[]
        {
            (LogStream.Log, scope.SubjectPrefix + ".log.>", TimeSpan.FromDays(7), 64 * Mb),
            (LogStream.Cfg, scope.SubjectPrefix + ".cfg.>", TimeSpan.FromDays(30), 16 * Mb),
            (LogStream.Sys, scope.SubjectPrefix + ".sys.>", TimeSpan.FromDays(30), 16 * Mb),
        })
        {
            var config = (await js.GetStreamAsync(node.StreamName(stream), cancellationToken: Ct)).Info.Config;
            config.Subjects.ShouldBe([subjects]);
            config.MaxAge.ShouldBe(maxAge);
            config.MaxBytes.ShouldBe(maxBytes);
            config.Storage.ShouldBe(StreamConfigStorage.File);
            config.Retention.ShouldBe(StreamConfigRetention.Limits);
            config.Discard.ShouldBe(StreamConfigDiscard.Old);
            config.DuplicateWindow.ShouldBe(TimeSpan.FromMinutes(2));
            config.MaxMsgSize.ShouldBe(1024 * 1024);
            config.NumReplicas.ShouldBe(1);
            (config.AllowDirect, config.DenyDelete, config.DenyPurge).ShouldBe((true, true, true));
        }

        node.Logs.WithEventId(1601).ShouldHaveSingleItem();
        var health = await node.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Tags.Contains("ready"), Ct);
        health.Entries[Watchpoint.Pipeline.PipelineModule.HealthCheckName].Status.ShouldBe(HealthStatus.Healthy);
        (await node.Reader.GetLastSequenceAsync(Ct)).ShouldBe(0);
        (await node.Reader.GetFirstAvailableSequenceAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task PIP_I_002_Provisioning_idempotent()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_002_Provisioning_idempotent));
        await using var client = new NatsClient(nats.Url);
        var js = client.CreateJetStreamContext();
        async Task<List<(StreamConfig Config, DateTimeOffset Created)>> StreamsAsync(PipelineNode node)
        {
            var streams = new List<(StreamConfig, DateTimeOffset)>();
            foreach (var stream in new[] { LogStream.Log, LogStream.Cfg, LogStream.Sys })
            {
                var info = (await js.GetStreamAsync(node.StreamName(stream), cancellationToken: Ct)).Info;
                streams.Add((info.Config, info.Created));
            }

            return streams;
        }

        List<(StreamConfig Config, DateTimeOffset Created)> first;
        await using (var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct))
        {
            first = await StreamsAsync(node);
            await node.Writer.AppendAsync([node.Envelope(DeviceId.New(), 1)], Ct);
        }

        // Restart: no error, same streams (not re-created), same configuration, data kept.
        await using (var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct))
        {
            var second = await StreamsAsync(node);
            for (var i = 0; i < first.Count; i++)
            {
                second[i].Created.ShouldBe(first[i].Created);
                System.Text.Json.JsonSerializer.Serialize(second[i].Config).ShouldBe(System.Text.Json.JsonSerializer.Serialize(first[i].Config));
            }

            node.Logs.WithEventId(1603).ShouldBeEmpty();
            (await node.Reader.GetLastSequenceAsync(Ct)).ShouldBe(1);
        }

        // A changed setting is applied to the existing stream.
        var changed = PipelineNode.OptionsFor(scope);
        changed.Retention = TimeSpan.FromDays(3);
        await using (var node = await PipelineNode.StartAsync(nats.Url, changed, Ct))
        {
            var third = await StreamsAsync(node);
            third[0].Config.MaxAge.ShouldBe(TimeSpan.FromDays(3));
            third[0].Created.ShouldBe(first[0].Created);
            node.Logs.WithEventId(1603).ShouldHaveSingleItem();
            (await node.Reader.GetLastSequenceAsync(Ct)).ShouldBe(1);
        }

        // A stream holding other subjects under our name stops the start-up.
        var foreign = nats.CreateScope("foreign");
        await js.CreateStreamAsync(new StreamConfig(foreign.StreamPrefix + "WP_LOG", [foreign.SubjectPrefix + ".other.>"]) { Storage = StreamConfigStorage.Memory }, Ct);
        var ex = await Should.ThrowAsync<PipelineException>(() => PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(foreign), Ct));
        ex.Error.Code.Value.ShouldBe(PipelineErrors.StreamIncompatibleCode);
    }

    [Fact]
    public async Task PIP_I_003_Append_and_replay_one_or_several_partitions()
    {
        await nats.StartAsync(Ct);
        await using var node = await PipelineNode.StartAsync(
            nats.Url, PipelineNode.OptionsFor(nats.CreateScope(nameof(PIP_I_003_Append_and_replay_one_or_several_partitions))), Ct);
        int[] partitions = [3, 17, 42, 63];
        var devices = partitions.Select(PipelineNode.DeviceIn).ToArray();

        // 1,000 records over 4 partitions, in batches that mix the partitions.
        var records = Enumerable.Range(0, 1000).Select(n => node.Envelope(devices[n % 4], n)).ToList();
        var sequences = new List<long>();
        foreach (var batch in records.Chunk(100))
        {
            sequences.AddRange(await node.Writer.AppendAsync(batch, Ct));
        }

        sequences.Distinct().Count().ShouldBe(1000);
        (await node.Reader.GetLastSequenceAsync(Ct)).ShouldBe(1000);

        // Replay of one partition: its records, in append order.
        for (var p = 0; p < 4; p++)
        {
            var replay = await ToListAsync(node.Reader.ReadPartitionAsync(partitions[p], 1, Ct));
            replay.Select(node.NumberOf).ShouldBe(Enumerable.Range(0, 250).Select(i => (long)((i * 4) + p)));
            replay.ShouldAllBe(r => r.Envelope.P == partitions[p]);
            replay.Select(r => r.Sequence).ShouldBe(replay.Select(r => r.Sequence).Order());
            replay.Select(r => r.Sequence).ShouldBe(Enumerable.Range(0, 250).Select(i => sequences[(i * 4) + p]));
        }

        // From the middle.
        var middle = sequences[502];
        var tail = await ToListAsync(node.Reader.ReadPartitionAsync(42, middle, Ct));
        tail[0].Sequence.ShouldBe(middle);
        tail.Count.ShouldBe(await CountAsync(node, 42, middle));

        // Two partitions with one consumer: the records of both in global sequence order, nothing else.
        var two = await ToListAsync(node.Reader.ReadPartitionsAsync(new HashSet<int> { 17, 63 }, 1, Ct));
        two.Count.ShouldBe(500);
        two.ShouldAllBe(r => r.Envelope.P == 17 || r.Envelope.P == 63);
        two.Select(r => r.Sequence).ShouldBe(two.Select(r => r.Sequence).Order());
        two.Where(r => r.Envelope.P == 17).Select(node.NumberOf).ShouldBe(Enumerable.Range(0, 250).Select(i => (long)((i * 4) + 1)));

        // All 64 partitions: the whole log.
        var all = await ToListAsync(node.Reader.ReadPartitionsAsync(Enumerable.Range(0, 64).ToHashSet(), 1, Ct));
        all.Select(r => r.Sequence).ShouldBe(Enumerable.Range(1, 1000).Select(n => (long)n));
        (await ToListAsync(node.Reader.ReadPartitionAsync(5, 1, Ct))).ShouldBeEmpty();

        async Task<int> CountAsync(PipelineNode n, int partition, long from) =>
            (await ToListAsync(n.Reader.ReadPartitionAsync(partition, 1, Ct))).Count(r => r.Sequence >= from);
    }

    [Fact]
    public async Task PIP_I_004_Dedup_within_window()
    {
        await nats.StartAsync(Ct);
        await using var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(nats.CreateScope(nameof(PIP_I_004_Dedup_within_window))), Ct);
        var device = DeviceId.New();
        var record = node.Envelope(device, 1);
        var other = node.Envelope(device, 2);

        var first = await node.Writer.AppendAsync([record], Ct);
        var again = await node.Writer.AppendAsync([record, other], Ct);
        var global = node.Envelope(device, 3) with { Kind = RecordKinds.RegistryChanged, P = 0, Dev = null, Body = node.Registry.SerializeBody(RecordKinds.RegistryChanged, new RegistryChangedBody(1, new RegistryChangedContext(null, null, null), [])) };
        var cfg1 = await node.Writer.AppendGlobalAsync(LogStream.Cfg, global, Ct);
        var cfg2 = await node.Writer.AppendGlobalAsync(LogStream.Cfg, global, Ct);

        // Same envelope id appended twice: one message, and the second append reports the sequence of the first.
        first.ShouldBe([1L]);
        again.ShouldBe([1L, 2L]);
        (cfg1, cfg2).ShouldBe((1L, 1L));
        await using var client = new NatsClient(nats.Url);
        var js = client.CreateJetStreamContext();
        (await js.GetStreamAsync(node.StreamName(LogStream.Log), cancellationToken: Ct)).Info.State.Messages.ShouldBe(2);
        (await js.GetStreamAsync(node.StreamName(LogStream.Cfg), cancellationToken: Ct)).Info.State.Messages.ShouldBe(1);
        (await ToListAsync(node.Reader.ReadPartitionAsync(Partitioning.Of(device), 1, Ct))).Select(r => r.Envelope.Id).ShouldBe([record.Id, other.Id]);
    }

    [Fact]
    public async Task PIP_I_005_Durable_consumer_survives_restart()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(PIP_I_005_Durable_consumer_survives_restart));
        var options = new LogConsumerOptions(MaxBatch: 500);
        IReadOnlyList<LogEnvelope> records;
        await using (var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct))
        {
            var devices = Enumerable.Range(0, 8).Select(_ => DeviceId.New()).ToArray();
            records = [.. Enumerable.Range(1, 1000).Select(n => node.Envelope(devices[n % 8], n))];
            foreach (var record in records)
            {
                await node.Writer.AppendAsync([record], Ct); // one by one: sequence n holds record n
            }

            // Both ways of rewinding a consumer to its last acknowledged record (reset API, and re-creation for
            // servers without it).
            foreach (var (name, useReset) in new[] { ("history", true), ("history-legacy", false) })
            {
                node.Options.UseConsumerReset = useReset;

                // Consume 500 of 1,000 and acknowledge; read the next 500 without acknowledging; stop.
                await using var consumer = node.Consumers.CreateDurable(name, LogStream.Log, [], options);
                await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
                (await batches.MoveNextAsync()).ShouldBeTrue();
                batches.Current.Records.Select(r => r.Sequence).ShouldBe(Enumerable.Range(1, 500).Select(n => (long)n));
                await batches.Current.AckAsync(Ct);
                (await batches.MoveNextAsync()).ShouldBeTrue();
                batches.Current.Records[0].Sequence.ShouldBe(501);
                batches.Current.Records.Count.ShouldBe(500);
            }
        }

        // A new process: a new consumer with the same name starts at 501, in order, although 501…1000 were already
        // delivered once and new records arrived meanwhile.
        await using (var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct))
        {
            await node.Writer.AppendAsync([node.Envelope(DeviceId.New(), 1001)], Ct);
            foreach (var (name, useReset) in new[] { ("history", true), ("history-legacy", false) })
            {
                node.Options.UseConsumerReset = useReset;
                await using var consumer = node.Consumers.CreateDurable(name, LogStream.Log, [], options);
                await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
                (await batches.MoveNextAsync()).ShouldBeTrue();
                batches.Current.Records.Select(r => r.Sequence).ShouldBe(Enumerable.Range(501, 500).Select(n => (long)n), name);
                batches.Current.Records.Select(r => r.Envelope.Id).ShouldBe(records.Skip(500).Select(r => r.Id), name);
                await batches.Current.AckAsync(Ct);
                (await batches.MoveNextAsync()).ShouldBeTrue();
                batches.Current.Records.Select(r => r.Sequence).ShouldBe([1001L], name);
                await batches.Current.AckAsync(Ct);
            }
        }

        // Everything acknowledged: a third start has nothing to deliver until a new record arrives.
        await using (var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(scope), Ct))
        {
            await using var consumer = node.Consumers.CreateDurable("history", LogStream.Log, [], options);
            await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
            var next = batches.MoveNextAsync();
            await node.Writer.AppendAsync([node.Envelope(DeviceId.New(), 1002)], Ct);
            (await next.AsTask().WaitAsync(TestTimeouts.Container, Ct)).ShouldBeTrue();
            batches.Current.Records.Select(r => r.Sequence).ShouldBe([1002L]);
            await batches.Current.AckAsync(Ct);
        }
    }

    [Fact]
    public async Task PIP_I_006_NATS_restart_during_append()
    {
        // A server of its own: stopping the shared one would break the other tests.
        await using var server = await RestartableNatsContainer.StartNewAsync(Ct);
        var scope = new NatsScope("t6.wp", "T6_");
        await using var node = await PipelineNode.StartAsync(server.Url, PipelineNode.OptionsFor(scope), Ct);
        const int producers = 8;
        const int minimumPerProducer = 300;
        var devices = Enumerable.Range(0, producers).Select(i => PipelineNode.DeviceIn(i * 7)).ToArray();
        var appended = new ConcurrentDictionary<int, List<(Guid Id, long Sequence)>>();
        var restarted = false;

        // A durable consumer runs during the whole test.
        var consumed = new ConcurrentQueue<LogRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var consumer = node.Consumers.CreateDurable("history", LogStream.Log, [], new LogConsumerOptions(MaxBatch: 200));
        var consuming = consumer.ProcessAsync(
            new LogProcessingOptions("history", node.Registry, node.Services.GetRequiredService<ILogger<PipelineLogIntegrationTests>>())
            {
                RetryDelay = TimeSpan.FromMilliseconds(100),
            },
            (record, _) =>
            {
                consumed.Enqueue(record);
                return ValueTask.CompletedTask;
            },
            stop.Token);

        // Load: every producer appends to its partition, in batches of 5, until the server came back and it wrote enough.
        var load = Enumerable.Range(0, producers).Select(producer => Task.Run(
            async () =>
            {
                var mine = appended[producer] = [];
                for (var n = 0; !Volatile.Read(ref restarted) || mine.Count < minimumPerProducer; n += 5)
                {
                    var batch = Enumerable.Range(n, 5).Select(i => node.Envelope(devices[producer], i)).ToList();
                    var sequences = await node.Writer.AppendAsync(batch, Ct);
                    mine.AddRange(batch.Select((r, i) => (r.Id, sequences[i])));
                }
            },
            Ct)).ToArray();

        // Stop NATS during the load and keep it down until the writer has noticed and is repeating appends.
        await Eventually.UntilAsync(() => appended.Count == producers && appended.Values.All(l => l.Count >= 50), TestTimeouts.Container, "the load did not start", ct: Ct);
        await server.StopAsync(Ct);
        await Eventually.UntilAsync(() => node.Logs.WithEventId(1602).Any(), TestTimeouts.Container, "the writer did not notice the outage", ct: Ct);
        var beforeRestart = appended.Values.Sum(l => l.Count);
        await server.StartAsync(Ct);
        Volatile.Write(ref restarted, true);

        // Every append completes by itself: retried, never failed.
        await Task.WhenAll(load).WaitAsync(TimeSpan.FromSeconds(90), Ct);
        var expected = appended.Values.Sum(l => l.Count);
        expected.ShouldBeGreaterThan(beforeRestart);

        // No loss, no duplicates: the log holds exactly the appended records, each once, each partition in append order,
        // at the sequences the writer reported.
        var log = await ToListAsync(node.Reader.ReadPartitionsAsync(Enumerable.Range(0, 64).ToHashSet(), 1, Ct));
        log.Count.ShouldBe(expected);
        log.Select(r => r.Envelope.Id).Distinct().Count().ShouldBe(expected);
        for (var producer = 0; producer < producers; producer++)
        {
            var partition = Partitioning.Of(devices[producer]);
            log.Where(r => r.Envelope.P == partition).Select(r => (r.Envelope.Id, r.Sequence)).ShouldBe(appended[producer]);
            log.Where(r => r.Envelope.P == partition).Select(node.NumberOf).ShouldBe(Enumerable.Range(0, appended[producer].Count).Select(i => (long)i));
        }

        // The consumer resumed by itself and handled every record once, in log order.
        await Eventually.UntilAsync(() => consumed.Count >= expected, TimeSpan.FromSeconds(60), "the consumer did not catch up after the restart", ct: Ct);
        await stop.CancelAsync();
        await consuming;
        consumed.Select(r => r.Sequence).ShouldBe(log.Select(r => r.Sequence));
    }

    [Fact]
    public async Task PIP_I_007_Ordering_under_concurrency()
    {
        await nats.StartAsync(Ct);
        await using var node = await PipelineNode.StartAsync(
            nats.Url, PipelineNode.OptionsFor(nats.CreateScope(nameof(PIP_I_007_Ordering_under_concurrency)), logBytes: 256 * Mb), Ct);
        const int perPartition = 1000;
        var devices = Enumerable.Range(0, Partitioning.PartitionCount).Select(PipelineNode.DeviceIn).ToArray();

        // 64 partitions × 1,000 records, appended concurrently: two writers per partition take turns (odd and even
        // batches), and each batch also carries records of a neighbour partition.
        var batches = new List<LogEnvelope>[Partitioning.PartitionCount][];
        var counters = new int[Partitioning.PartitionCount];
        for (var p = 0; p < Partitioning.PartitionCount; p++)
        {
            batches[p] = [.. Enumerable.Range(0, perPartition / 50).Select(_ => new List<LogEnvelope>())];
        }

        for (var p = 0; p < Partitioning.PartitionCount; p++)
        {
            for (var b = 0; b < perPartition / 50; b++)
            {
                for (var i = 0; i < 25; i++)
                {
                    batches[p][b].Add(node.Envelope(devices[p], counters[p]++));
                }
            }
        }

        // The second half of every partition goes into the batches of the next partition's writer.
        for (var p = 0; p < Partitioning.PartitionCount; p++)
        {
            var neighbour = (p + 1) % Partitioning.PartitionCount;
            for (var b = 0; b < perPartition / 50; b++)
            {
                for (var i = 0; i < 25; i++)
                {
                    batches[neighbour][b].Add(node.Envelope(devices[p], counters[p]++ + 1_000_000));
                }
            }
        }

        var expected = new ConcurrentDictionary<int, List<(Guid Id, long Sequence)>>();
        await Task.WhenAll(Enumerable.Range(0, Partitioning.PartitionCount).Select(p => Task.Run(
            async () =>
            {
                foreach (var batch in batches[p])
                {
                    var sequences = await node.Writer.AppendAsync(batch, Ct);
                    for (var i = 0; i < batch.Count; i++)
                    {
                        lock (expected)
                        {
                            expected.GetOrAdd(batch[i].P, _ => []).Add((batch[i].Id, sequences[i]));
                        }
                    }
                }
            },
            Ct))).WaitAsync(TimeSpan.FromMinutes(3), Ct);

        var log = await ToListAsync(node.Reader.ReadPartitionsAsync(Enumerable.Range(0, 64).ToHashSet(), 1, Ct));
        log.Count.ShouldBe(Partitioning.PartitionCount * perPartition);
        log.Select(r => r.Sequence).ShouldBe(Enumerable.Range(1, log.Count).Select(n => (long)n));
        foreach (var partition in log.GroupBy(r => r.Envelope.P))
        {
            // Per writer, the records of the partition are in the log in the order that writer appended them.
            var numbers = partition.Select(node.NumberOf).ToList();
            numbers.Count.ShouldBe(perPartition);
            numbers.Where(n => n < 1_000_000).ShouldBe(Enumerable.Range(0, perPartition / 2).Select(i => (long)i));
            numbers.Where(n => n >= 1_000_000).Select(n => n - 1_000_000).ShouldBe(Enumerable.Range(perPartition / 2, perPartition / 2).Select(i => (long)i));

            // And the sequences the writer reported are the ones in the log.
            expected[partition.Key].OrderBy(x => x.Sequence).ShouldBe(partition.Select(r => (r.Envelope.Id, r.Sequence)));
        }
    }

    [Fact]
    public async Task PIP_I_008_Max_message_size_enforced()
    {
        await nats.StartAsync(Ct);
        await using var node = await PipelineNode.StartAsync(nats.Url, PipelineNode.OptionsFor(nats.CreateScope(nameof(PIP_I_008_Max_message_size_enforced))), Ct);
        var device = DeviceId.New();
        var small = node.Envelope(device, 1);
        var huge = node.Envelope(device, 2) with
        {
            Body = node.Registry.SerializeBody(RecordKinds.Health, new HealthRecordBody(Kernel.Values.Connectivity.Online, [], new string('x', 2 * 1024 * 1024))),
        };

        // 2 MB body: error, and nothing of the batch is appended.
        var ex = await Should.ThrowAsync<PipelineException>(async () => await node.Writer.AppendAsync([small, huge], Ct));
        ex.Error.Code.Value.ShouldBe(PipelineErrors.RecordTooLargeCode);
        ex.Error.Args["max"].ShouldBe(1024L * 1024);
        (await node.Reader.GetLastSequenceAsync(Ct)).ShouldBe(0);

        // The limit counts headers and body: just below it passes, just above it does not.
        HealthRecordBody Body(int reason) => new(Kernel.Values.Connectivity.Online, [], new string('x', reason));
        var overhead = Internal(node.Envelope(device, 3) with { Body = node.Registry.SerializeBody(RecordKinds.Health, Body(0)) });
        var fits = small with { Id = Guid.CreateVersion7(), Body = node.Registry.SerializeBody(RecordKinds.Health, Body((1024 * 1024) - overhead)) };
        var tooBig = small with { Id = Guid.CreateVersion7(), Body = node.Registry.SerializeBody(RecordKinds.Health, Body((1024 * 1024) - overhead + 1)) };
        (await Should.ThrowAsync<PipelineException>(async () => await node.Writer.AppendAsync([tooBig], Ct))).Error.Code.Value.ShouldBe(PipelineErrors.RecordTooLargeCode);
        (await node.Writer.AppendAsync([fits], Ct)).ShouldBe([1L]);

        // The writer still works.
        (await node.Writer.AppendAsync([small], Ct)).ShouldBe([2L]);
        (await ToListAsync(node.Reader.ReadPartitionAsync(Partitioning.Of(device), 1, Ct))).Select(r => r.Envelope.Id).ShouldBe([fits.Id, small.Id]);

        static int Internal(LogEnvelope envelope) => Watchpoint.Pipeline.Internal.Log.LogMessageCodec.Encode(envelope, 1, null).Size;
    }

    private static async Task<List<LogRecord>> ToListAsync(IAsyncEnumerable<LogRecord> source)
    {
        var list = new List<LogRecord>();
        await foreach (var record in source)
        {
            list.Add(record);
        }

        return list;
    }
}
