using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>Maps <c>event</c>, <c>sysevent</c> and <c>operator.note</c> records to event rows (spec HIS §4–§5).</summary>
internal sealed class EventRowMapper(ILogRecordTypeRegistry registry, IHistoryEnrichment enrichment, ITypeCatalog catalog)
{
    private static readonly string[] _personRefFields = ["holderRef", "credentialRef"];

    /// <summary>Maps one record; <see langword="null"/> for a kind that is not an event.</summary>
    /// <param name="record">The record.</param>
    /// <param name="stream">The stream it was read from: <c>WP_SYS</c> records get sequence 0.</param>
    /// <returns>The row.</returns>
    public HistoryEventRow? Map(LogRecord record, LogStream stream)
    {
        var envelope = record.Envelope;
        var source = envelope.Dev is { } device ? enrichment.ForDevice(device) : envelope.Srv is { } server ? enrichment.ForServer(server) : null;
        var seq = stream == LogStream.Sys ? 0 : record.Sequence;
        switch (envelope.Kind)
        {
            case RecordKinds.Event:
                {
                    var body = record.BodyAs<EventRecordBody>(registry);
                    var personal = PersonalFieldsOf(source, body.Path);
                    var category = body.Path.StartsWith("health.", StringComparison.Ordinal) ? EventCategory.Health : EventCategory.Device;
                    var dot = body.Path.IndexOf('.', StringComparison.Ordinal);
                    var titleKey = dot > 0
                        ? $"capabilities.{body.Path[..dot]}.events.{body.Path[(dot + 1)..]}.title"
                        : $"events.{body.Path}.title";
                    return Build(record, seq, source, body.Path, category, body.Severity, body.Payload, personal, titleKey, device: true);
                }

            case HistoryRecordKinds.SysEvent:
                {
                    var body = record.BodyAs<SysEventBody>(registry);
                    return Build(
                        record, seq, source, body.Kind, EventCategory.System, body.Severity ?? Severity.Normal, body.Payload,
                        body.PersonalFields ?? [], $"events.{body.Kind}.title", device: false);
                }

            case HistoryRecordKinds.OperatorNote:
                {
                    var body = record.BodyAs<OperatorNoteBody>(registry);
                    var fields = new Dictionary<string, WpValue> { ["text"] = WpValue.String(body.Text) };
                    if (body.Location is { } location)
                    {
                        fields["location"] = WpValue.String(location.ToString("D"));
                    }

                    return Build(record, seq, source, HistoryRecordKinds.OperatorNote, EventCategory.Operator, Severity.Normal, fields, [], "events.operator.note", device: false);
                }

            default:
                return null;
        }
    }

    private IReadOnlyList<string> PersonalFieldsOf(HistorySource? source, string path)
    {
        if (source is null || !EventPath.Parse(path).TryGetValue(out var eventPath, out _)
            || !catalog.Get(source.TypeRef).TryGetValue(out var type, out _)
            || !type.Events.TryGetValue(eventPath, out var definition))
        {
            return [];
        }

        return definition.PersonalDataFields;
    }

    private static HistoryEventRow Build(
        LogRecord record,
        long seq,
        HistorySource? source,
        string kind,
        EventCategory category,
        Severity severity,
        IReadOnlyDictionary<string, WpValue> payload,
        IReadOnlyList<string> personalFields,
        string titleKey,
        bool device)
    {
        var envelope = record.Envelope;
        var open = new Dictionary<string, WpValue>(StringComparer.Ordinal);
        var personal = new Dictionary<string, WpValue>(StringComparer.Ordinal);
        List<string> refs = [];
        foreach (var (name, value) in payload)
        {
            if (Array.IndexOf(_personRefFields, name) >= 0 && value.TryAsString(out var reference) && reference.Length > 0)
            {
                refs.Add(reference);
            }

            (personalFields.Contains(name, StringComparer.Ordinal) ? personal : open)[name] = value;
        }

        // The title is stored in clear: it never carries a personal field.
        var titleArgs = new Dictionary<string, WpValue>(open, StringComparer.Ordinal);
        if (source is not null && device)
        {
            titleArgs["device"] = WpValue.String(source.DisplayName);
        }

        var entity = envelope.Dev?.Value ?? envelope.Srv?.Value;
        return new HistoryEventRow(
            envelope.Ts,
            envelope.Id,
            seq,
            kind,
            (short)category,
            (short)severity,
            envelope.Src,
            entity,
            entity is null ? null : (short)(envelope.Dev is null ? 1 : 0),
            source?.ServerId ?? envelope.Srv?.Value,
            source?.LocationId,
            source?.LocationIds,
            source?.TypeId,
            source?.TagKeys,
            (short)envelope.Origin.Type,
            envelope.Origin.Id,
            HistoryJson.Serialize(open),
            personal.Count == 0 ? null : HistoryJson.Serialize(personal),
            refs.Count == 0 ? null : [.. refs.Distinct(StringComparer.Ordinal)],
            titleKey,
            HistoryJson.Serialize(titleArgs),
            source?.DisplayName,
            source?.LocationNames,
            HistoryJson.CorrelationJson(envelope.Corr));
    }
}
