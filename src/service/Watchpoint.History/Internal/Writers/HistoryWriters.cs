using Microsoft.Extensions.Logging;
using Watchpoint.History.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>One durable consumer of history: its name, stream, kinds and how a batch is written.</summary>
/// <param name="Name">The durable name.</param>
/// <param name="Stream">The stream.</param>
/// <param name="Kinds">The kind filter.</param>
/// <param name="Table">The table written, for metrics.</param>
/// <param name="Write">Writes a batch (already limited to readable records) and returns the number of rows written.</param>
internal sealed record HistoryConsumerDefinition(
    string Name,
    LogStream Stream,
    IReadOnlyList<string> Kinds,
    string Table,
    Func<IReadOnlyList<LogRecord>, CancellationToken, Task<int>> Write);

/// <summary>Creates the four consumers of history (spec HIS §5).</summary>
internal sealed class HistoryWriters(
    ILogRecordTypeRegistry registry,
    IHistoryEnrichment enrichment,
    Watchpoint.Model.Contracts.ITypeCatalog catalog,
    IHistoryStore store,
    Policies.PolicyResolver policies,
    Policies.LastStoredCache cache,
    ILoggerFactory loggers)
{
    public IReadOnlyList<HistoryConsumerDefinition> Create()
    {
        var events = new EventRowMapper(registry, enrichment, catalog);
        var alarms = new AlarmFolder(registry, enrichment);
        var commands = new CommandFolder(registry, enrichment);
        var logger = loggers.CreateLogger("Watchpoint.History");

        Task<int> WriteEvents(string consumer, LogStream stream, IReadOnlyList<LogRecord> records, CancellationToken ct)
        {
            List<HistoryEventRow> rows = [];
            foreach (var record in records)
            {
                try
                {
                    if (events.Map(record, stream) is { } row)
                    {
                        rows.Add(row);
                    }
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                {
                    HistoryLog.RecordSkipped(logger, consumer, record.Envelope.Id, record.Envelope.Kind, ex.GetType().Name);
                }
            }

            return store.WriteEventsAsync(rows, ct);
        }

        void Skipped(string consumer, LogRecord record, string reason) =>
            HistoryLog.RecordSkipped(logger, consumer, record.Envelope.Id, record.Envelope.Kind, reason);

        var properties = new PropertyWriter(registry, enrichment, policies, cache, store, logger, "his-properties");
        return
        [
            new("his-events", LogStream.Log, [RecordKinds.Event, HistoryRecordKinds.SysEvent, HistoryRecordKinds.OperatorNote], "events",
                (records, ct) => WriteEvents("his-events", LogStream.Log, records, ct)),
            new("his-events-sys", LogStream.Sys, [HistoryRecordKinds.SysEvent, HistoryRecordKinds.OperatorNote], "events",
                (records, ct) => WriteEvents("his-events-sys", LogStream.Sys, records, ct)),
            new("his-alarms", LogStream.Log, [HistoryRecordKinds.AlarmFilter], "alarms",
                (records, ct) =>
                {
                    var (rows, timeline) = alarms.Fold(records, (r, why) => Skipped("his-alarms", r, why));
                    return store.WriteAlarmsAsync(rows, timeline, ct);
                }),
            new("his-commands", LogStream.Log, [HistoryRecordKinds.CommandFilter], "commands",
                (records, ct) => store.WriteCommandsAsync(commands.Fold(records, (r, why) => Skipped("his-commands", r, why)), ct)),
            new("his-properties", LogStream.Log, [RecordKinds.Property], "property_changes", properties.WriteAsync),
        ];
    }
}
