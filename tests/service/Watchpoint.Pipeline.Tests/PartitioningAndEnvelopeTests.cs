using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Log;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class PartitioningAndEnvelopeTests : IDisposable
{
    private readonly PipelineHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void PIP_U_001_Partition_function_stable()
    {
        // Golden values computed with an independent XXH32 implementation over the UUID bytes in network order.
        // They are part of the stored data format: if this test fails, the partition function changed.
        (string Id, int Partition)[] golden =
        [
            ("00000000-0000-7000-8000-000000000001", 53),
            ("00000000-0000-7000-8000-000000000002", 19),
            ("00000000-0000-7000-8000-00000000002a", 37),
            ("0192f3a1-7c4e-7b2a-9d1f-3e5a6b7c8d9e", 20),
            ("01930b6c-2f00-7000-8000-000000000000", 47),
            ("ffffffff-ffff-ffff-ffff-ffffffffffff", 42),
            ("00000000-0000-0000-0000-000000000000", 58),
            ("123e4567-e89b-12d3-a456-426614174000", 44),
            ("01937f5e-9a3c-7d4f-b821-0c6e5f4a3b2d", 29),
            ("6ba7b810-9dad-11d1-80b4-00c04fd430c8", 34),
        ];

        foreach (var (id, partition) in golden)
        {
            var guid = Guid.Parse(id);
            Partitioning.Of(guid).ShouldBe(partition, id);
            if (guid != Guid.Empty)
            {
                Partitioning.Of(DeviceId.From(guid)).ShouldBe(partition, id);
                Partitioning.Of(ServerId.From(guid)).ShouldBe(partition, id);
            }
        }

        Partitioning.PartitionCount.ShouldBe(64);
    }

    [Fact]
    public void PIP_U_002_Partition_distribution()
    {
        // 100,000 UUID v7 values: increasing millisecond timestamps, seeded random bits.
        var random = new Random(20261114);
        var counts = new int[Partitioning.PartitionCount];
        var bytes = new byte[16];
        var start = TestTime.Start.ToUnixTimeMilliseconds();
        for (var i = 0; i < 100_000; i++)
        {
            random.NextBytes(bytes);
            var ms = start + i;
            for (var b = 5; b >= 0; b--)
            {
                bytes[b] = (byte)ms;
                ms >>= 8;
            }

            bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
            bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
            var id = new Guid(bytes, bigEndian: true);
            id.Version.ShouldBe(7);
            counts[Partitioning.Of(id)]++;
        }

        const double mean = 100_000d / Partitioning.PartitionCount;
        for (var p = 0; p < counts.Length; p++)
        {
            ((double)counts[p]).ShouldBeInRange(mean * 0.9, mean * 1.1, $"partition {p}");
        }
    }

    [Fact]
    public void PIP_U_003_Subject_naming_with_prefix()
    {
        var naming = new LogNaming("t1.wp", "T1_");

        naming.Subject(LogStream.Log, 7, "property").ShouldBe("t1.wp.log.07.property");
        naming.Subject(LogStream.Log, 63, "alarm.raised").ShouldBe("t1.wp.log.63.alarm.raised");
        naming.Subject(LogStream.Cfg, 0, "registry.changed").ShouldBe("t1.wp.cfg.registry.changed");
        naming.Subject(LogStream.Sys, 0, "node.started").ShouldBe("t1.wp.sys.node.started");
        naming.PartitionFilter(7).ShouldBe("t1.wp.log.07.>");
        naming.StreamName(LogStream.Log).ShouldBe("T1_WP_LOG");
        naming.StreamName(LogStream.Cfg).ShouldBe("T1_WP_CFG");
        naming.StreamName(LogStream.Sys).ShouldBe("T1_WP_SYS");
        naming.StreamSubjects(LogStream.Log).ShouldBe("t1.wp.log.>");
        naming.PartitionFilters(new HashSet<int> { 5, 3 }).ShouldBe(["t1.wp.log.03.>", "t1.wp.log.05.>"]);
        naming.PartitionFilters(Enumerable.Range(0, 64).ToHashSet()).ShouldBe(["t1.wp.log.>"]);
        new LogNaming("wp", string.Empty).Subject(LogStream.Log, 0, "health").ShouldBe("wp.log.00.health");
        Should.Throw<ArgumentOutOfRangeException>(() => naming.Subject(LogStream.Log, 64, "property"));
    }

    [Fact]
    public void PIP_U_004_Envelope_JSON_round_trip()
    {
        var envelope = new LogEnvelope(
            Guid.Parse("0192f3a1-7c4e-7b2a-9d1f-3e5a6b7c8d9e"),
            "property",
            new DateTimeOffset(2026, 11, 3, 9, 0, 0, 123, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 9, 0, 0, 100, TimeSpan.Zero),
            17,
            TestIds.Device(1),
            TestIds.Server(2),
            new RecordOrigin(RecordOriginType.Connection, TestIds.Uuid(3)),
            new RecordCorrelation(TestIds.Of<CommandId>(4), TestIds.Of<AlarmId>(5), TestIds.Of<AutomationRunId>(6)),
            JsonDocument.Parse("""{"path":"doorPosition.position","value":{"t":"enum","v":"open"},"quality":"good","prev":null}""").RootElement);

        var json = LogEnvelopeJson.Serialize(envelope);
        var read = LogEnvelopeJson.Deserialize(json);

        // Deterministic: fixed member order, every member written, no whitespace.
        var expected = (
            "{'id':'0192f3a1-7c4e-7b2a-9d1f-3e5a6b7c8d9e','kind':'property','ts':'2026-11-03T09:00:00.123Z','src':'2026-11-03T09:00:00.100Z','p':17,"
            + "'dev':'00000000-0000-7000-8000-000000000001','srv':'00000000-0000-7000-8000-000000000002',"
            + "'origin':{'type':'connection','id':'00000000-0000-7000-8000-000000000003'},"
            + "'corr':{'command':'00000000-0000-7000-8000-000000000004','alarm':'00000000-0000-7000-8000-000000000005','automationRun':'00000000-0000-7000-8000-000000000006'},"
            + "'body':{'path':'doorPosition.position','value':{'t':'enum','v':'open'},'quality':'good','prev':null}}").Replace('\'', '"');
        Encoding.UTF8.GetString(json).ShouldBe(expected);
        LogEnvelopeJson.Serialize(read).ShouldBe(json);
        (read with { Body = default }).ShouldBe(envelope with { Body = default });
        JsonElement.DeepEquals(read.Body, envelope.Body).ShouldBeTrue();

        // Optional members absent, and members of a future version present: still readable.
        var future = LogEnvelopeJson.Deserialize(Encoding.UTF8.GetBytes(
            """{"future":{"a":[1,2]},"id":"0192f3a1-7c4e-7b2a-9d1f-3e5a6b7c8d9e","kind":"property","ts":"2026-11-03T09:00:00.123Z","body":{},"zone":"x"}"""));
        future.Id.ShouldBe(envelope.Id);
        future.Src.ShouldBeNull();
        future.Dev.ShouldBeNull();
        future.Origin.ShouldBe(RecordOrigin.System);
        future.Corr.ShouldBe(RecordCorrelation.None);

        // The minimal envelope round-trips with explicit nulls.
        var minimal = envelope with { Src = null, Srv = null, Origin = RecordOrigin.System, Corr = RecordCorrelation.None };
        var minimalRead = LogEnvelopeJson.Deserialize(LogEnvelopeJson.Serialize(minimal));
        (minimalRead with { Body = default }).ShouldBe(minimal with { Body = default });
    }

    [Fact]
    public async Task PIP_U_005_Envelope_rejects_missing_id_kind_ts()
    {
        var device = PipelineHarness.DeviceIn(3);
        var valid = _harness.Envelope(1, device);
        valid.Validate(LogStream.Log).IsSuccess.ShouldBeTrue();

        LogEnvelope[] invalid =
        [
            valid with { Id = Guid.Empty },
            valid with { Kind = string.Empty },
            valid with { Kind = "Not A Kind" },
            valid with { Kind = "alarm.*" },
            valid with { Ts = default },
            valid with { P = (Partitioning.Of(device) + 1) % 64 },
            valid with { Dev = null },
        ];
        foreach (var envelope in invalid)
        {
            envelope.Validate(LogStream.Log).Error.Code.Value.ShouldBe(PipelineErrors.RecordInvalidCode);

            // The writer refuses the whole batch before anything is sent.
            var ex = await Should.ThrowAsync<PipelineException>(async () => await _harness.Writer.AppendAsync([valid, envelope], Ct));
            ex.Error.Code.Value.ShouldBe(PipelineErrors.RecordInvalidCode);
        }

        (await Should.ThrowAsync<PipelineException>(async () =>
            await _harness.Writer.AppendAsync([valid with { Kind = "not.registered" }], Ct))).Error.Code.Value.ShouldBe(PipelineErrors.RecordInvalidCode);
        valid.Validate(LogStream.Cfg).Error.Code.Value.ShouldBe(PipelineErrors.RecordInvalidCode); // p must be 0 in a global stream
        _harness.Broker.PublishAttempts.ShouldBeEmpty();

        // Reading: an envelope without id, kind or ts is not a record.
        const string id = "\"id\":\"0192f3a1-7c4e-7b2a-9d1f-3e5a6b7c8d9e\"";
        const string kind = "\"kind\":\"health\"";
        const string ts = "\"ts\":\"2026-11-03T09:00:00.123Z\"";
        const string body = "\"body\":{}";
        LogEnvelopeJson.Deserialize(Json(id, kind, ts, body)).Kind.ShouldBe("health");
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Json(kind, ts, body)));
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Json(id, ts, body)));
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Json(id, kind, body)));
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Json(id, kind, ts)));
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Json("\"id\":null", kind, ts, body)));
        Should.Throw<FormatException>(() => LogEnvelopeJson.Deserialize(Encoding.UTF8.GetBytes("not json")));

        static byte[] Json(params string[] members) => Encoding.UTF8.GetBytes("{" + string.Join(",", members) + "}");
    }

    [Fact]
    public void PIP_U_006_Record_kind_registry()
    {
        var registry = new LogRecordTypeRegistry();
        registry.Register("alarm.raised", 1, TestJsonContext.LogProfile<AlarmBody>());
        registry.Register("alarm.raised", 1, TestJsonContext.LogProfile<AlarmBody>()); // same registration again: allowed

        var envelope = _harness.Envelope(1, TestIds.Device(1)) with
        {
            Kind = "alarm.raised",
            Body = registry.SerializeBody("alarm.raised", new AlarmBody("door-forced", 2)),
        };
        var record = new LogRecord(42, envelope);

        record.BodyAs<AlarmBody>(registry).ShouldBe(new AlarmBody("door-forced", 2));
        envelope.Body.GetRawText().ShouldBe("""{"rule":"door-forced","priority":2}""");
        registry.TryGetSchemaVersion("alarm.raised", out var version).ShouldBeTrue();
        version.ShouldBe(1);
        registry.CanRead(record).ShouldBeTrue();
        registry.CanRead(record with { SchemaVersion = 2 }).ShouldBeFalse();
        registry.TryGetSchemaVersion("alarm.cleared", out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => record.BodyAs<HealthRecordBody>(registry));
        Should.Throw<InvalidOperationException>(() => new LogRecord(1, envelope with { Kind = "alarm.cleared" }).BodyAs<AlarmBody>(registry));
        Should.Throw<InvalidOperationException>(() => registry.Register("alarm.raised", 2, TestJsonContext.LogProfile<AlarmBody>()));
        Should.Throw<ArgumentException>(() => registry.Register("Alarm Raised", 1, TestJsonContext.LogProfile<AlarmBody>()));

        // The M1 kinds are registered by the module.
        foreach (var kind in new[] { RecordKinds.Property, RecordKinds.Event, RecordKinds.Health, RecordKinds.RegistryChanged })
        {
            _harness.Registry.TryGetSchemaVersion(kind, out _).ShouldBeTrue(kind);
        }
    }

    [Fact]
    public async Task PIP_U_007_Unknown_kind_skipped_on_read()
    {
        // A newer node wrote a kind this node does not know (the writer's registry has it, the consumer's has not).
        var device = PipelineHarness.DeviceIn(9);
        await _harness.Writer.AppendAsync(
            [
                _harness.Envelope(1, device),
                _harness.Envelope(2, device, PipelineHarness.AlarmRaised),
                _harness.Envelope(3, device),
            ],
            Ct);
        var older = PipelineModule.CreateRegistry();

        // The reader yields the record as it is.
        var replay = await _harness.Reader.ReadPartitionAsync(9, 1, Ct).ToListAsync();
        replay.Select(r => r.Envelope.Kind).ShouldBe([RecordKinds.Health, PipelineHarness.AlarmRaised, RecordKinds.Health]);
        older.CanRead(replay[1]).ShouldBeFalse();

        // The consumer helper skips it with a debug log and handles the others.
        var handled = new List<long>();
        var done = new TaskCompletionSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var consumer = _harness.Consumers.CreateDurable("history", LogStream.Log, [], new LogConsumerOptions());
        var processing = consumer.ProcessAsync(
            new LogProcessingOptions("history", older, _harness.Logger("test")) { TimeProvider = _harness.Time },
            (record, _) =>
            {
                handled.Add(record.Sequence);
                if (record.Sequence == 3)
                {
                    done.SetResult();
                }

                return ValueTask.CompletedTask;
            },
            stop.Token);
        await done.Task.WaitAsync(Ct);
        await stop.CancelAsync();
        await processing;

        handled.ShouldBe([1, 3]);
        var skipped = _harness.Logs.WithEventId(1606).ShouldHaveSingleItem();
        skipped.Level.ShouldBe(LogLevel.Debug);
        skipped.Properties["Kind"].ShouldBe(PipelineHarness.AlarmRaised);
        skipped.Properties["Sequence"].ShouldBe(2L);
    }

    [Fact]
    public void PIP_U_015_Batch_size_limit()
    {
        var connection = TestIds.Of<ConnectionId>(1);
        Observation Health(long n) => new()
        {
            Id = TestIds.Uuid(n),
            Kind = ObservationKind.Health,
            Device = TestIds.Device(1),
            Health = new HealthReport(Kernel.Values.Connectivity.Online, [], null),
            Connection = connection,
        };
        var batch = Enumerable.Range(1, 5001).Select(n => Health(n)).ToList();

        var tooLarge = IngestLimits.CheckBatchSize(batch);
        var allowed = IngestLimits.CheckBatchSize(batch.GetRange(0, 5000));

        tooLarge.IsSuccess.ShouldBeFalse();
        tooLarge.Error.Code.Value.ShouldBe("pipeline.batch.too_large");
        tooLarge.Error.Kind.ShouldBe(Kernel.Results.ErrorKind.Validation);
        tooLarge.Error.Args["count"].ShouldBe(5001);
        tooLarge.Error.Args["max"].ShouldBe(5000);
        allowed.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task PIP_U_019_Dedup_id_header_set()
    {
        var device = PipelineHarness.DeviceIn(4);
        var envelope = _harness.Envelope(1, device);

        var encoded = LogMessageCodec.Encode(envelope, schemaVersion: 1, activity: null);
        var first = await _harness.Writer.AppendAsync([envelope], Ct);
        var second = await _harness.Writer.AppendAsync([envelope], Ct);

        // Envelope id = Nats-Msg-Id, in the canonical lowercase form.
        encoded.MessageId.ShouldBe(envelope.Id.ToString("D"));
        encoded.Headers.ShouldContain(new KeyValuePair<string, string>("Nats-Msg-Id", envelope.Id.ToString("D")));
        encoded.Headers.ShouldContain(new KeyValuePair<string, string>("Wp-Kind", RecordKinds.Health));
        encoded.Headers.ShouldContain(new KeyValuePair<string, string>("Wp-Schema", "1"));
        encoded.Headers.ShouldContain(new KeyValuePair<string, string>("Wp-Envelope", "1"));
        encoded.Size.ShouldBe(encoded.Payload.Length + "NATS/1.0\r\n\r\n".Length + encoded.Headers.Sum(h => h.Key.Length + h.Value.Length + 4));
        _harness.Broker.PublishAttempts.Select(a => a.MessageId).ShouldBe([encoded.MessageId, encoded.MessageId]);

        // Appending the same record again stores nothing new and reports the same sequence.
        second.ShouldBe(first);
        var stored = _harness.Broker.Messages(_harness.LogStreamName).ShouldHaveSingleItem();
        stored.Headers["Nats-Msg-Id"].ShouldBe(encoded.MessageId);
    }

    [Fact]
    public async Task PIP_U_020_Trace_context_propagated()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Watchpoint.Pipeline.Tests");
        var device = PipelineHarness.DeviceIn(4);

        string traceParent;
        ActivityTraceId traceId;
        ActivitySpanId spanId;
        using (var activity = source.StartActivity("ingest").ShouldNotBeNull())
        {
            traceParent = activity.Id.ShouldNotBeNull();
            traceId = activity.TraceId;
            spanId = activity.SpanId;
            await _harness.Writer.AppendAsync([_harness.Envelope(1, device)], Ct);
        }

        await _harness.Writer.AppendAsync([_harness.Envelope(2, device)], Ct); // no current activity

        // Written as the W3C traceparent header…
        var stored = _harness.Broker.Messages(_harness.LogStreamName);
        stored[0].Headers["traceparent"].ShouldBe(traceParent);
        stored[1].Headers.ContainsKey("traceparent").ShouldBeFalse();

        // …restored on read…
        var records = await _harness.Reader.ReadPartitionAsync(4, 1, Ct).ToListAsync();
        records[0].TraceParent.ShouldBe(traceParent);
        records[0].GetTraceContext().ShouldNotBeNull().TraceId.ShouldBe(traceId);
        records[1].GetTraceContext().ShouldBeNull();

        // …and continued when a consumer handles the record.
        var seen = new List<(ActivityTraceId? Trace, ActivitySpanId? Parent)>();
        var done = new TaskCompletionSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var consumer = _harness.Consumers.CreateDurable("tracing", LogStream.Log, [], new LogConsumerOptions());
        var processing = consumer.ProcessAsync(
            new LogProcessingOptions("tracing", _harness.Registry, _harness.Logger("test")) { TimeProvider = _harness.Time },
            (record, _) =>
            {
                seen.Add((Activity.Current?.TraceId, Activity.Current?.ParentSpanId));
                if (record.Sequence == 2)
                {
                    done.SetResult();
                }

                return ValueTask.CompletedTask;
            },
            stop.Token);
        await done.Task.WaitAsync(Ct);
        await stop.CancelAsync();
        await processing;

        seen[0].ShouldBe((traceId, spanId));
        seen[1].Trace.ShouldBeNull();
        PipelineMetrics.ActivitySource.Name.ShouldBe("Watchpoint.Pipeline");
    }
}
