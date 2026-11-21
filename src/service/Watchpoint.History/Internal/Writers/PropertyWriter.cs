using Microsoft.Extensions.Logging;
using Watchpoint.History.Internal.Policies;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>Applies the history policy to <c>property</c> records and stores the ones it keeps (spec HIS §6).</summary>
internal sealed class PropertyWriter(
    ILogRecordTypeRegistry registry,
    IHistoryEnrichment enrichment,
    PolicyResolver policies,
    LastStoredCache cache,
    IHistoryStore store,
    ILogger logger,
    string consumer)
{
    /// <summary>Evaluates the batch and writes the rows to keep. The cache is updated only after the write succeeded, so a repeated attempt decides the same.</summary>
    /// <param name="records">The records of the batch.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of rows inserted.</returns>
    public async Task<int> WriteAsync(IReadOnlyList<LogRecord> records, CancellationToken ct)
    {
        await policies.RefreshAsync(ct).ConfigureAwait(false);
        List<(HistoryPropertyRow Row, PropertyHistoryPolicy Policy)> candidates = [];
        foreach (var record in records)
        {
            var envelope = record.Envelope;
            if (envelope.Kind != RecordKinds.Property || envelope.Dev is not { } device)
            {
                continue;
            }

            PropertyRecordBody body;
            try
            {
                body = record.BodyAs<PropertyRecordBody>(registry);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                HistoryLog.RecordSkipped(logger, consumer, envelope.Id, envelope.Kind, ex.GetType().Name);
                continue;
            }

            var source = enrichment.ForDevice(device);
            var policy = source is null ? PropertyHistoryPolicy.EveryChange : policies.Resolve(device, source.TypeRef, body.Path);
            if (policy.Mode is HistoryMode.None or HistoryMode.Periodic)
            {
                continue;
            }

            var columns = ValueColumns.From(body.Value);
            candidates.Add((
                new HistoryPropertyRow(envelope.Ts, device.Value, body.Path, columns.Num, columns.Text, columns.Bool, columns.Json, (short)body.Quality, envelope.Src, envelope.Id),
                policy));
        }

        // The latest stored row of every deadband property seen for the first time since start (or since the cache was emptied).
        var overlay = new Dictionary<(Guid Device, string Path), HistoryPropertyRow>();
        var missing = candidates
            .Where(c => c.Policy.Mode == HistoryMode.Deadband && !cache.TryGet(c.Row.DeviceId, c.Row.Path, out _))
            .Select(c => (c.Row.DeviceId, c.Row.Path))
            .Distinct()
            .ToList();
        if (missing.Count > 0)
        {
            foreach (var row in await store.GetLastPropertyRowsAsync(missing, ct).ConfigureAwait(false))
            {
                overlay[(row.DeviceId, row.Path)] = row;
            }
        }

        List<HistoryPropertyRow> toStore = [];
        foreach (var (row, policy) in candidates)
        {
            var key = (row.DeviceId, row.Path);
            if (policy.Mode == HistoryMode.Deadband)
            {
                HistoryPropertyRow? last = overlay.TryGetValue(key, out var inBatch) ? inBatch : cache.TryGet(row.DeviceId, row.Path, out var cached) ? cached : null;
                if (!policy.ShouldStore(row, last))
                {
                    continue;
                }

                overlay[key] = row;
            }

            toStore.Add(row);
        }

        var written = await store.WritePropertyChangesAsync(toStore, ct).ConfigureAwait(false);
        foreach (var row in overlay.Values)
        {
            cache.Set(row);
        }

        return written;
    }
}
