using Shouldly;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Tests.Fakes;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class WriterAndReaderTests : IDisposable
{
    private readonly PipelineHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PIP_U_008_Writer_preserves_order_within_partition()
    {
        // 100 records of one partition interleaved with 100 of another; every 10th publish is lost once and every
        // 10th acknowledgement is lost once, so the writer has to repeat them.
        var a = PipelineHarness.DeviceIn(3);
        var b = PipelineHarness.DeviceIn(5);
        var records = new List<LogEnvelope>();
        for (var i = 0; i < 100; i++)
        {
            records.Add(_harness.Envelope(i, a));
            records.Add(_harness.Envelope(1000 + i, b));
        }

        var index = records.Select((r, i) => (r.Id.ToString("D"), i)).ToDictionary(x => x.Item1, x => x.i);
        _harness.Broker.PublishFault = (message, attempt) => attempt > 1
            ? PublishFault.None
            : (index[message.MessageId] % 20) switch { 6 or 7 => PublishFault.Lost, 14 or 15 => PublishFault.AckLost, _ => PublishFault.None };

        var sequences = await _harness.PumpAsync(_harness.Writer.AppendAsync(records, Ct).AsTask());

        // Sequences strictly increasing in input order, per partition.
        sequences.Count.ShouldBe(200);
        var ofA = sequences.Where((_, i) => i % 2 == 0).ToList();
        var ofB = sequences.Where((_, i) => i % 2 == 1).ToList();
        ofA.ShouldBe(ofA.Order().Distinct().ToList());
        ofB.ShouldBe(ofB.Order().Distinct().ToList());

        // The log holds every record once, each partition in input order, at the reported sequences.
        var stored = _harness.Broker.Messages(_harness.LogStreamName);
        stored.Count.ShouldBe(200);
        stored.Where(m => m.Subject.Contains(".log.03.", StringComparison.Ordinal)).Select(m => m.Headers["Nats-Msg-Id"])
            .ShouldBe(records.Where((_, i) => i % 2 == 0).Select(r => r.Id.ToString("D")));
        stored.Where(m => m.Subject.Contains(".log.05.", StringComparison.Ordinal)).Select(m => m.Headers["Nats-Msg-Id"])
            .ShouldBe(records.Where((_, i) => i % 2 == 1).Select(r => r.Id.ToString("D")));
        for (var i = 0; i < records.Count; i++)
        {
            stored.Single(m => m.Sequence == sequences[i]).Headers["Nats-Msg-Id"].ShouldBe(records[i].Id.ToString("D"));
        }

        // A record of a partition was never sent before its predecessor was acknowledged.
        foreach (var partition in new[] { ".log.03.", ".log.05." })
        {
            var attempts = _harness.Broker.PublishAttempts.Where(x => x.Subject.Contains(partition, StringComparison.Ordinal)).Select(x => index[x.MessageId]).ToList();
            attempts.ShouldBe(attempts.Order().ToList());
        }

        _harness.Logs.WithEventId(1602).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task PIP_U_009_Append_completes_only_after_ack()
    {
        var device = PipelineHarness.DeviceIn(3);
        var record = _harness.Envelope(1, device);
        var ackGate = new TaskCompletionSource();
        _harness.Broker.AckGate = ackGate.Task;
        _harness.Broker.PublishFault = (_, attempt) => attempt == 1 ? PublishFault.Lost : PublishFault.None;

        // First publish lost: the writer waits, then sends the same message id again.
        var append = _harness.Writer.AppendAsync([record], Ct).AsTask();
        append.IsCompleted.ShouldBeFalse();
        await _harness.PumpUntilAsync(() => _harness.Broker.PublishAttempts.Count == 2);
        _harness.Broker.PublishAttempts.Select(x => x.MessageId).Distinct().ShouldHaveSingleItem().ShouldBe(record.Id.ToString("D"));

        // Stored, but the acknowledgement is still on its way: not complete.
        _harness.Broker.Messages(_harness.LogStreamName).Count.ShouldBe(1);
        append.IsCompleted.ShouldBeFalse();

        ackGate.SetResult();
        (await append.WaitAsync(Ct)).ShouldBe([1L]);

        // A second caller of the same partition waits for its turn, and a global append completes on ack too.
        _harness.Broker.AckGate = null;
        _harness.Broker.PublishFault = null;
        (await _harness.Writer.AppendAsync([_harness.Envelope(2, device)], Ct)).ShouldBe([2L]);
        (await _harness.Writer.AppendGlobalAsync(LogStream.Cfg, _harness.GlobalEnvelope(1), Ct)).ShouldBe(1L);

        // Log unreachable for longer than Log:AppendTimeout: the call fails, nothing is stored, nothing is buffered.
        _harness.Broker.PublishFault = (_, _) => PublishFault.Lost;
        var failing = _harness.Writer.AppendAsync([_harness.Envelope(3, device)], Ct).AsTask();
        await _harness.PumpUntilAsync(() => failing.IsCompleted, TimeSpan.FromSeconds(1));
        var ex = await Should.ThrowAsync<PipelineException>(() => failing);
        ex.Error.Code.Value.ShouldBe(PipelineErrors.LogUnavailableCode);
        ex.Error.Kind.ShouldBe(Kernel.Results.ErrorKind.Unavailable);
        _harness.Broker.Messages(_harness.LogStreamName).Count.ShouldBe(2);

        // The caller repeats the call with the same record once the log is back: stored once.
        _harness.Broker.PublishFault = null;
        (await _harness.Writer.AppendAsync([_harness.Envelope(3, device)], Ct)).ShouldBe([3L]);
    }

    [Fact]
    public async Task PIP_U_010_Replay_from_sequence()
    {
        // Records 1…10 in partition 3, 11…20 in partition 5.
        var p3 = PipelineHarness.DeviceIn(3);
        var p5 = PipelineHarness.DeviceIn(5);
        await _harness.Writer.AppendAsync([.. Enumerable.Range(1, 10).Select(n => _harness.Envelope(n, p3))], Ct);
        await _harness.Writer.AppendAsync([.. Enumerable.Range(11, 10).Select(n => _harness.Envelope(n, p5))], Ct);

        var fromFour = await _harness.Reader.ReadPartitionAsync(3, 4, Ct).ToListAsync();
        var whole = await _harness.Reader.ReadPartitionAsync(5, 0, Ct).ToListAsync();
        var both = await _harness.Reader.ReadPartitionsAsync(new HashSet<int> { 5, 3 }, 8, Ct).ToListAsync();
        var all = await _harness.Reader.ReadPartitionsAsync(Enumerable.Range(0, 64).ToHashSet(), 1, Ct).ToListAsync();

        fromFour.Select(r => r.Sequence).ShouldBe([4, 5, 6, 7, 8, 9, 10]);
        fromFour.ShouldAllBe(r => r.Envelope.P == 3 && r.Envelope.Dev == p3);
        whole.Select(r => r.Sequence).ShouldBe(Enumerable.Range(11, 10).Select(n => (long)n));
        both.Select(r => r.Sequence).ShouldBe(Enumerable.Range(8, 13).Select(n => (long)n));
        all.Count.ShouldBe(20);
        (await _harness.Reader.ReadPartitionAsync(7, 1, Ct).ToListAsync()).ShouldBeEmpty();
        (await _harness.Reader.ReadPartitionAsync(3, 11, Ct).ToListAsync()).ShouldBeEmpty();
        (await _harness.Reader.ReadPartitionsAsync(new HashSet<int>(), 1, Ct).ToListAsync()).ShouldBeEmpty();

        // The broker breaks off after three records: the replay resumes after the last one, no gap, no duplicate.
        _harness.Broker.BreakNextReadAfter = 3;
        var resumed = await _harness.PumpAsync(_harness.Reader.ReadPartitionAsync(3, 1, Ct).ToListAsync());
        resumed.Select(r => r.Sequence).ShouldBe(Enumerable.Range(1, 10).Select(n => (long)n));

        // A replay stops at the end it saw when it started.
        var seen = new List<long>();
        await foreach (var record in _harness.Reader.ReadPartitionAsync(3, 1, Ct))
        {
            seen.Add(record.Sequence);
            if (seen.Count == 1)
            {
                await _harness.Writer.AppendAsync([_harness.Envelope(21, p3)], Ct);
            }
        }

        seen.Count.ShouldBe(10);
    }

    [Fact]
    public async Task PIP_U_011_First_available_sequence_after_retention()
    {
        (await _harness.Reader.GetFirstAvailableSequenceAsync(Ct)).ShouldBe(1);
        (await _harness.Reader.GetLastSequenceAsync(Ct)).ShouldBe(0);
        var device = PipelineHarness.DeviceIn(3);
        await _harness.Writer.AppendAsync([.. Enumerable.Range(1, 10).Select(n => _harness.Envelope(n, device))], Ct);
        (await _harness.Reader.GetFirstAvailableSequenceAsync(Ct)).ShouldBe(1);

        _harness.Broker.Purge(_harness.LogStreamName, 5); // retention removed 1…5

        (await _harness.Reader.GetFirstAvailableSequenceAsync(Ct)).ShouldBe(6);
        (await _harness.Reader.GetLastSequenceAsync(Ct)).ShouldBe(10);
        (await _harness.Reader.ReadPartitionAsync(3, 1, Ct).ToListAsync()).Select(r => r.Sequence).ShouldBe([6, 7, 8, 9, 10]);

        _harness.Broker.Purge(_harness.LogStreamName, 10); // everything expired
        (await _harness.Reader.GetFirstAvailableSequenceAsync(Ct)).ShouldBe(11);
        (await _harness.Reader.GetLastSequenceAsync(Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task PIP_U_012_Global_streams_separate()
    {
        var device = PipelineHarness.DeviceIn(3);
        await _harness.Writer.AppendAsync([_harness.Envelope(1, device)], Ct);

        var cfg1 = await _harness.Writer.AppendGlobalAsync(LogStream.Cfg, _harness.GlobalEnvelope(1), Ct);
        var cfg2 = await _harness.Writer.AppendGlobalAsync(LogStream.Cfg, _harness.GlobalEnvelope(2), Ct);
        var sys1 = await _harness.Writer.AppendGlobalAsync(LogStream.Sys, _harness.GlobalEnvelope(3), Ct);

        // Each stream has its own sequence.
        (cfg1, cfg2, sys1).ShouldBe((1L, 2L, 1L));

        // Not visible in a partition replay, nor in the other global stream.
        var partitions = await _harness.Reader.ReadPartitionsAsync(Enumerable.Range(0, 64).ToHashSet(), 1, Ct).ToListAsync();
        partitions.ShouldHaveSingleItem().Envelope.Kind.ShouldBe(RecordKinds.Health);
        (await _harness.Reader.GetLastSequenceAsync(Ct)).ShouldBe(1);
        var cfg = await _harness.Reader.ReadGlobalAsync(LogStream.Cfg, 1, Ct).ToListAsync();
        cfg.Select(r => r.BodyAs<RegistryChangedBody>(_harness.Registry).Revision).ShouldBe([1, 2]);
        (await _harness.Reader.ReadGlobalAsync(LogStream.Cfg, 2, Ct).ToListAsync()).ShouldHaveSingleItem().Sequence.ShouldBe(2);
        (await _harness.Reader.ReadGlobalAsync(LogStream.Sys, 1, Ct).ToListAsync()).ShouldHaveSingleItem();
        _harness.Broker.Messages(_harness.Naming.StreamName(LogStream.Cfg)).Select(m => m.Subject)
            .ShouldAllBe(s => s == "t1.wp.cfg.registry.changed");

        // The global methods do not accept the partitioned stream, and a device-bound envelope is not a global record.
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await _harness.Writer.AppendGlobalAsync(LogStream.Log, _harness.GlobalEnvelope(4), Ct));
        Should.Throw<ArgumentOutOfRangeException>(() => _harness.Reader.ReadGlobalAsync(LogStream.Log, 1, Ct));
    }
}
