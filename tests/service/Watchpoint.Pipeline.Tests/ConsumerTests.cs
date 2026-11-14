using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Pipeline.Contracts;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class ConsumerTests : IDisposable
{
    private readonly PipelineHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PIP_U_013_Durable_consumer_batch_and_ack()
    {
        // 2,500 records over three partitions.
        var devices = new[] { PipelineHarness.DeviceIn(1), PipelineHarness.DeviceIn(2), PipelineHarness.DeviceIn(3) };
        for (var chunk = 0; chunk < 5; chunk++)
        {
            await _harness.Writer.AppendAsync([.. Enumerable.Range(chunk * 500, 500).Select(n => _harness.Envelope(n, devices[n % 3]))], Ct);
        }

        var options = new LogConsumerOptions(MaxBatch: 1000, MaxDeliver: 3);
        long[] Sequences(LogRecordBatch batch) => [.. batch.Records.Select(r => r.Sequence)];

        await using (var consumer = _harness.Consumers.CreateDurable("history", LogStream.Log, [], options))
        {
            await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);

            // First batch, not acknowledged: it comes again, nothing newer before it.
            (await batches.MoveNextAsync()).ShouldBeTrue();
            var first = batches.Current;
            first.Records.Count.ShouldBe(1000);
            first.Delivery.ShouldBe(1);
            first.IsLastDelivery.ShouldBeFalse();
            Sequences(first).ShouldBe(Enumerable.Range(1, 1000).Select(n => (long)n));

            (await batches.MoveNextAsync()).ShouldBeTrue();
            var again = batches.Current;
            again.Delivery.ShouldBe(2);
            Sequences(again).ShouldBe(Sequences(first));
            await again.AckAsync(Ct);
            await again.AckAsync(Ct); // acknowledging twice is harmless

            // Second batch acknowledged.
            (await batches.MoveNextAsync()).ShouldBeTrue();
            var second = batches.Current;
            second.Delivery.ShouldBe(1);
            Sequences(second).ShouldBe(Enumerable.Range(1001, 1000).Select(n => (long)n));
            await second.AckAsync(Ct);

            // Third batch read but the process "stops" before acknowledging.
            (await batches.MoveNextAsync()).ShouldBeTrue();
            batches.Current.Records.Count.ShouldBe(500);
        }

        // 2,500 records with MaxBatch 1,000 made three batches; a new consumer with the same name resumes after the
        // last acknowledged one.
        await using (var consumer = _harness.Consumers.CreateDurable("history", LogStream.Log, [], options))
        {
            await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
            (await batches.MoveNextAsync()).ShouldBeTrue();
            Sequences(batches.Current).ShouldBe(Enumerable.Range(2001, 500).Select(n => (long)n));

            // Never acknowledged: delivered MaxDeliver times, then logged as dropped; the consumer goes on.
            (await batches.MoveNextAsync()).ShouldBeTrue();
            batches.Current.Delivery.ShouldBe(2);
            (await batches.MoveNextAsync()).ShouldBeTrue();
            batches.Current.Delivery.ShouldBe(3);
            batches.Current.IsLastDelivery.ShouldBeTrue();
            await _harness.Writer.AppendAsync([_harness.Envelope(5000, devices[0])], Ct);
            (await batches.MoveNextAsync()).ShouldBeTrue();
            Sequences(batches.Current).ShouldBe([2501L]);

            var dropped = _harness.Logs.WithEventId(1609).ShouldHaveSingleItem();
            dropped.Level.ShouldBe(LogLevel.Error);
            dropped.Properties["First"].ShouldBe(2001L);
            dropped.Properties["Last"].ShouldBe(2500L);
            dropped.Properties["Deliveries"].ShouldBe(3);
        }

        // Only one reader per consumer, and names are checked.
        Should.Throw<ArgumentException>(() => _harness.Consumers.CreateDurable("not a name", LogStream.Log, [], options));
        Should.Throw<ArgumentOutOfRangeException>(() => _harness.Consumers.CreateDurable("x", LogStream.Log, [], new LogConsumerOptions(MaxBatch: 0)));
    }

    [Fact]
    public async Task PIP_U_014_Consumer_kind_filter()
    {
        var a = PipelineHarness.DeviceIn(10);
        var b = PipelineHarness.DeviceIn(20);
        LogEnvelope[] records =
        [
            _harness.Envelope(1, a),
            _harness.Envelope(2, a, PipelineHarness.AlarmRaised),
            _harness.Envelope(3, b),
            _harness.Envelope(4, b, PipelineHarness.AlarmRaised),
            _harness.Envelope(5, a, PipelineHarness.AlarmCleared),
        ];
        foreach (var record in records)
        {
            // One call per record: the order between partitions is then the call order.
            await _harness.Writer.AppendAsync([record], Ct);
        }

        await _harness.Writer.AppendGlobalAsync(LogStream.Cfg, _harness.GlobalEnvelope(1), Ct);

        async Task<List<string>> KindsAsync(string name, LogStream stream, params string[] filter)
        {
            await using var consumer = _harness.Consumers.CreateDurable(name, stream, filter, new LogConsumerOptions());
            await using var batches = consumer.ReadBatchesAsync(Ct).GetAsyncEnumerator(Ct);
            (await batches.MoveNextAsync()).ShouldBeTrue();
            await batches.Current.AckAsync(Ct);
            return [.. batches.Current.Records.Select(r => r.Envelope.Kind)];
        }

        (await KindsAsync("alarms", LogStream.Log, "alarm.*"))
            .ShouldBe([PipelineHarness.AlarmRaised, PipelineHarness.AlarmRaised, PipelineHarness.AlarmCleared]);
        (await KindsAsync("raised", LogStream.Log, "alarm.raised")).ShouldBe([PipelineHarness.AlarmRaised, PipelineHarness.AlarmRaised]);
        (await KindsAsync("two", LogStream.Log, "alarm.cleared", "health"))
            .ShouldBe([RecordKinds.Health, RecordKinds.Health, PipelineHarness.AlarmCleared]);
        (await KindsAsync("all", LogStream.Log)).Count.ShouldBe(5);
        (await KindsAsync("registry", LogStream.Cfg, "registry.>")).ShouldBe([RecordKinds.RegistryChanged]);

        _harness.Naming.KindFilters(LogStream.Log, ["alarm.*"]).ShouldBe(["t1.wp.log.*.alarm.*"]);
        _harness.Naming.KindFilters(LogStream.Cfg, ["registry.changed"]).ShouldBe(["t1.wp.cfg.registry.changed"]);
        _harness.Naming.KindFilters(LogStream.Sys, []).ShouldBe(["t1.wp.sys.>"]);
        Should.Throw<ArgumentException>(() => _harness.Consumers.CreateDurable("bad", LogStream.Log, ["alarm raised"], new LogConsumerOptions()));
        Should.Throw<ArgumentException>(() => _harness.Consumers.CreateDurable("bad", LogStream.Log, [">.alarm"], new LogConsumerOptions()));
    }
}
