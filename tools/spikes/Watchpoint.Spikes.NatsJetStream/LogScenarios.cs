using System.Diagnostics;
using System.Globalization;
using System.Text;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Watchpoint.Spikes.NatsJetStream;

/// <summary>Event log measurements: append, dedup, fenced append, atomic batches, replay (PIP §5).</summary>
internal static class LogScenarios
{
    public const string Stream = "WP_LOG";
    public const int Partitions = 64;

    private static int _staleRecreates;

    /// <summary>Times a deleted and immediately re-created stream still held the old records.</summary>
    public static int StaleRecreates => _staleRecreates;

    private sealed class StaleStreamException : Exception;

    /// <summary>Unexpected fencing rejections seen by <see cref="FencedLanesAppendAsync"/> (diagnostics).</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<string> Conflicts { get; } = new();

    public static StreamConfig LogStream(int replicas, bool atomic = false) => new(Stream, ["wp.log.>"])
    {
        Storage = StreamConfigStorage.File,
        DuplicateWindow = TimeSpan.FromMinutes(2),
        MaxMsgSize = 1024 * 1024,
        AllowDirect = true,
        NumReplicas = replicas,
        DenyDelete = true,
        DenyPurge = true,
        MaxAge = TimeSpan.FromDays(7),
        MaxBytes = 8L * 1024 * 1024 * 1024,   // PIP default is 20 GB; the spike sandbox disk is smaller
        Discard = StreamConfigDiscard.Old,
        AllowAtomicPublish = atomic,
    };

    public static async Task<INatsJSContext> ResetStreamAsync(NatsConnection nats, int replicas, bool atomic = false)
    {
        // Right after start-up a cluster may still be electing its JetStream meta leader.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ResetStreamOnceAsync(nats, replicas, atomic).ConfigureAwait(false);
            }
            catch (Exception e) when (e is NatsJSApiNoResponseException or StaleStreamException && attempt < 40)
            {
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    private static async Task<INatsJSContext> ResetStreamOnceAsync(NatsConnection nats, int replicas, bool atomic)
    {
        var js = new NatsJSContext(nats);
        try
        {
            if (!await js.DeleteStreamAsync(Stream).ConfigureAwait(false))
            {
                throw new InvalidOperationException("stream delete returned false");
            }
        }
        catch (NatsJSApiException e) when (e.Error.ErrCode == 10059)
        {
            // stream not found
        }

        var created = await js.CreateStreamAsync(LogStream(replicas, atomic)).ConfigureAwait(false);
        if (created.Info.State.Messages != 0)
        {
            // Seen intermittently: delete + immediate re-create of the same name brings back the old records.
            Interlocked.Increment(ref _staleRecreates);
            throw new StaleStreamException();
        }

        return js;
    }

    public static string Subject(int partition, string kind = "property") =>
        string.Create(CultureInfo.InvariantCulture, $"wp.log.{partition:00}.{kind}");

    public static string PartitionFilter(int partition) =>
        string.Create(CultureInfo.InvariantCulture, $"wp.log.{partition:00}.>");

    private const string BodyJson =
        """"{"corr":{"command":null,"alarm":null,"automationRun":null},"body":{"path":"doorPosition.position","value":{"t":"enum","v":"open"},"quality":"good","prev":{"t":"enum","v":"closed"}}}"""";

    /// <summary>A representative <c>property</c> envelope (PIP §5.2), about 420 bytes.</summary>
    public static byte[] Envelope(Guid id, int partition)
    {
        var dev = Guid.CreateVersion7();
        var head = string.Create(CultureInfo.InvariantCulture,
            $$"""{"id":"{{id}}","kind":"property","ts":"2026-11-07T09:00:00.123Z","src":"2026-11-07T09:00:00.100Z","p":{{partition}},"dev":"{{dev}}","srv":"{{dev}}","origin":{"type":"connection","id":"{{dev}}"},""");
        var json = head + BodyJson;
        return Encoding.UTF8.GetBytes(json);
    }

    public static NatsHeaders Headers() => new()
    {
        ["Wp-Kind"] = "property",
        ["Wp-Schema"] = "1",
    };

    /// <summary>Awaited one-by-one appends: per-record durable latency.</summary>
    public static async Task SequentialLatencyAsync(INatsJSContext js, Report report, string label, int count)
    {
        var samples = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            var p = i % Partitions;
            var sw = Stopwatch.StartNew();
            var ack = await js.PublishAsync(Subject(p), Envelope(id, p), opts: new NatsJSPubOpts { MsgId = id.ToString() },
                headers: Headers()).ConfigureAwait(false);
            ack.EnsureSuccess();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        report.Row(label + ": append latency, one record awaited", Report.Latency(samples));
    }

    /// <summary>Pipelined appends with a window of in-flight acknowledgements (writer batches, PIP §5.4).</summary>
    public static async Task<TimeSpan> WindowedAppendAsync(INatsJSContext js, int count, int window)
    {
        using var gate = new SemaphoreSlim(window);
        var pending = new List<Task>(count);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            var id = Guid.CreateVersion7();
            var p = i % Partitions;
            pending.Add(PublishAndReleaseAsync(js, Subject(p), Envelope(id, p), id, gate));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        return sw.Elapsed;
    }

    private static async Task PublishAndReleaseAsync(INatsJSContext js, string subject, byte[] data, Guid id, SemaphoreSlim gate)
    {
        try
        {
            var ack = await js.PublishAsync(subject, data, opts: new NatsJSPubOpts { MsgId = id.ToString() }, headers: Headers())
                .ConfigureAwait(false);
            ack.EnsureSuccess();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Same record id published twice inside the duplicate window.</summary>
    public static async Task DedupAsync(INatsJSContext js, Report report)
    {
        var id = Guid.CreateVersion7();
        var first = await js.PublishAsync(Subject(3), Envelope(id, 3), opts: new NatsJSPubOpts { MsgId = id.ToString() }).ConfigureAwait(false);
        var second = await js.PublishAsync(Subject(3), Envelope(id, 3), opts: new NatsJSPubOpts { MsgId = id.ToString() }).ConfigureAwait(false);
        report.Row("Dedup: same `Nats-Msg-Id` twice",
            string.Create(CultureInfo.InvariantCulture,
                $"first seq {first.Seq} duplicate={first.Duplicate}; second seq {second.Seq} duplicate={second.Duplicate} (no new record)"));
    }

    /// <summary>
    /// Fencing on the partition (CLU §5): two writers use <c>Nats-Expected-Last-Subject-Sequence</c> with
    /// <c>Nats-Expected-Last-Subject-Sequence-Subject = wp.log.pp.&gt;</c> so the check spans every kind of the partition.
    /// </summary>
    public static async Task FencingAsync(INatsJSContext js, Report report)
    {
        const int p = 17;
        var filter = PartitionFilter(p);
        var a1 = await PublishExpectAsync(js, Subject(p, "property"), p, filter, 0).ConfigureAwait(false);
        var a2 = await PublishExpectAsync(js, Subject(p, "event"), p, filter, a1.Seq).ConfigureAwait(false);
        // Writer B still believes the last partition sequence is a1 (stale owner): rejected although the subject differs.
        var stale = await PublishExpectAsync(js, Subject(p, "health"), p, filter, a1.Seq).ConfigureAwait(false);
        // Without the -Subject header the check is per exact subject: the stale writer is NOT stopped.
        var id = Guid.CreateVersion7();
        var exact = await js.TryPublishAsync(Subject(p, "health"), Envelope(id, p),
            opts: new NatsJSPubOpts { MsgId = id.ToString(), ExpectedLastSubjectSequence = 0 }).ConfigureAwait(false);
        report.Row("Fencing: expected last sequence on `wp.log.17.>` (other kind)",
            string.Create(CultureInfo.InvariantCulture,
                $"owner appends seq {a1.Seq}, {a2.Seq}; stale writer expecting {a1.Seq} → {(stale.Error is null ? "ACCEPTED (bad)" : "rejected: " + stale.Error.Description + " (" + stale.Error.ErrCode + ")")}"));
        report.Row("Fencing: expected last sequence on the exact subject only",
            exact.Success ? "stale writer on another kind subject is accepted → per-subject check alone does not fence a partition"
                          : "rejected: " + exact.Error.Message);
    }

    private static async Task<PubAckResponse> PublishExpectAsync(INatsJSContext js, string subject, int p, string filter, ulong expected)
    {
        var id = Guid.CreateVersion7();
        var r = await js.TryPublishAsync(subject, Envelope(id, p), opts: new NatsJSPubOpts
        {
            MsgId = id.ToString(),
            ExpectedLastSubjectSequence = expected,
            ExpectedLastSubjectSequenceSubject = filter,
        }).ConfigureAwait(false);
        if (r.Success)
        {
            return r.Value;
        }

        if (r.Error is NatsJSApiException api)
        {
            return new PubAckResponse { Error = api.Error };
        }

        throw r.Error;
    }

    /// <summary>
    /// Fenced append at volume: one lane per partition; each record carries the expected last partition sequence,
    /// so a lane cannot pipeline (it needs the previous ack). Measures what fencing costs.
    /// </summary>
    public static async Task<(TimeSpan Elapsed, int Resolved)> FencedLanesAppendAsync(INatsJSContext js, int count)
    {
        var perLane = count / Partitions;
        var resolved = 0;
        var stream = await js.GetStreamAsync(Stream).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Partitions).Select(async p =>
        {
            ulong last = 0;
            var filter = PartitionFilter(p);
            for (var i = 0; i < perLane; i++)
            {
                var id = Guid.CreateVersion7();
                var data = Envelope(id, p);
                var ack = await js.PublishAsync(Subject(p), data, opts: new NatsJSPubOpts
                {
                    MsgId = id.ToString(),
                    ExpectedLastSubjectSequence = last,
                    ExpectedLastSubjectSequenceSubject = filter,
                }, headers: Headers()).ConfigureAwait(false);
                if (ack.Error is { } wrong && wrong.Description?.StartsWith("wrong last sequence", StringComparison.Ordinal) == true)
                {
                    // If the partition's last record is ours, an earlier attempt of this same publish was stored
                    // (the client re-sent it): adopt its sequence. Otherwise another writer appended: we are fenced out.
                    var stored = await stream.GetAsync(new StreamMsgGetRequest { LastBySubj = filter }).ConfigureAwait(false);
                    var storedBody = Encoding.UTF8.GetString(stored.Message.Data.Span);
                    if (storedBody.Contains(id.ToString(), StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref resolved);
                    }
                    else
                    {
                        // Unexpected: there is no other writer in this scenario. Record the evidence and retry.
                        var state = (await js.GetStreamAsync(Stream).ConfigureAwait(false)).Info.State;
                        Conflicts.Enqueue(string.Create(CultureInfo.InvariantCulture,
                            $"partition {p}, record {i}, expected {last}: '{wrong.Description}' ({wrong.ErrCode}); last on filter = seq {stored.Message.Seq} subject {stored.Message.Subject}; stream {state.Messages} msgs, last seq {state.LastSeq}"));
                        i--;
                    }

                    last = stored.Message.Seq;
                    continue;
                }

                ack.EnsureSuccess();
                last = ack.Seq;
            }
        })).ConfigureAwait(false);
        return (sw.Elapsed, resolved);
    }

    /// <summary>
    /// Fenced atomic batches (nats-server 2.12 atomic batch publish, ADR-50): the expected-sequence check is on the first
    /// message of the batch, one acknowledgement per batch. Uses raw headers (NATS.Net 3.3 has no batch API).
    /// </summary>
    public static async Task<(TimeSpan Elapsed, string? Error)> FencedAtomicBatchesAsync(NatsConnection nats, int count, int batchSize,
        int lanes)
    {
        var perPartition = count / Partitions;
        string? error = null;
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, lanes).Select(async lane =>
        {
            // A lane owns partitions lane, lane + lanes, …; at most one open batch per lane.
            var owned = Enumerable.Range(0, Partitions).Where(p => p % lanes == lane).ToArray();
            var last = new ulong[Partitions];
            for (var done = 0; done < perPartition && error is null; done += batchSize)
            {
                var n = Math.Min(batchSize, perPartition - done);
                foreach (var p in owned)
                {
                    var r = await AtomicBatchAsync(nats, p, n, last[p]).ConfigureAwait(false);
                    if (r.Error is not null)
                    {
                        error = r.Error;
                        return;
                    }

                    last[p] = r.Seq;
                }
            }
        })).ConfigureAwait(false);
        return (sw.Elapsed, error);
    }

    private static async Task<(ulong Seq, string? Error)> AtomicBatchAsync(NatsConnection nats, int p, int n, ulong expected)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var filter = PartitionFilter(p);
        NatsMsg<byte[]> reply = default;
        for (var i = 1; i <= n; i++)
        {
            var id = Guid.CreateVersion7();
            var h = Headers();
            h["Nats-Msg-Id"] = id.ToString();
            h["Nats-Batch-Id"] = batchId;
            h["Nats-Batch-Sequence"] = i.ToString(CultureInfo.InvariantCulture);
            if (i == 1)
            {
                h["Nats-Expected-Last-Subject-Sequence"] = expected.ToString(CultureInfo.InvariantCulture);
                h["Nats-Expected-Last-Subject-Sequence-Subject"] = filter;
            }

            if (i == n)
            {
                h["Nats-Batch-Commit"] = "1";
                reply = await nats.RequestAsync<byte[], byte[]>(Subject(p), Envelope(id, p), headers: h).ConfigureAwait(false);
            }
            else
            {
                await nats.PublishAsync(Subject(p), Envelope(id, p), headers: h).ConfigureAwait(false);
            }
        }

        var body = reply.Data is null ? string.Empty : Encoding.UTF8.GetString(reply.Data);
        if (body.Contains("\"error\"", StringComparison.Ordinal))
        {
            return (0, body);
        }

        var seqText = body.Split("\"seq\":")[1].Split([',', '}'])[0];
        return (ulong.Parse(seqText, CultureInfo.InvariantCulture), null);
    }

    /// <summary>Ordered consumer replay (PIP §5.4) from <paramref name="fromSequence"/> until caught up.</summary>
    public static async Task<(long Count, TimeSpan Elapsed)> ReplayAsync(INatsJSContext js, IReadOnlyList<string> filters, ulong fromSequence,
        int? maxMsgs = null)
    {
        var opts = new NatsJSOrderedConsumerOpts
        {
            FilterSubjects = [.. filters],
            DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
            OptStartSeq = fromSequence,
        };
        var sw = Stopwatch.StartNew();
        var consumer = await js.CreateOrderedConsumerAsync(Stream, opts).ConfigureAwait(false);
        long count = 0;
        ulong lastSeq = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await foreach (var msg in consumer.ConsumeAsync<byte[]>(opts: maxMsgs is null ? null : new NatsJSConsumeOpts { MaxMsgs = maxMsgs }, cancellationToken: cts.Token).ConfigureAwait(false))
        {
            count++;
            var seq = msg.Metadata!.Value.Sequence.Stream;
            if (seq <= lastSeq)
            {
                throw new InvalidOperationException("replay out of order");
            }

            lastSeq = seq;
            if (msg.Metadata.Value.NumPending == 0)
            {
                break;
            }
        }

        return (count, sw.Elapsed);
    }
}
