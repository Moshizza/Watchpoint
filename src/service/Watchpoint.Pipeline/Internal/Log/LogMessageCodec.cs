using System.Diagnostics;
using System.Globalization;
using System.Text;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>A record ready to be published.</summary>
/// <param name="MessageId">The deduplication id (<c>Nats-Msg-Id</c>): the record id.</param>
/// <param name="Headers">The headers, <c>Nats-Msg-Id</c> included.</param>
/// <param name="Payload">The envelope JSON.</param>
/// <param name="Size">The message size JetStream counts: header block and payload.</param>
internal sealed record EncodedRecord(string MessageId, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Payload, int Size);

/// <summary>Turns envelopes into log messages and back (spec PIP §5.2).</summary>
internal static class LogMessageCodec
{
    public const string MessageIdHeader = "Nats-Msg-Id";
    public const string KindHeader = "Wp-Kind";
    public const string SchemaHeader = "Wp-Schema";
    public const string EnvelopeHeader = "Wp-Envelope";
    public const string TraceParentHeader = "traceparent";

    /// <summary>The envelope format version this node writes, and the highest it reads.</summary>
    public const int EnvelopeVersion = 1;

    private const int HeaderBlockOverhead = 12; // "NATS/1.0\r\n" + final "\r\n"

    public static EncodedRecord Encode(LogEnvelope envelope, int schemaVersion, Activity? activity)
    {
        var id = envelope.Id.ToString("D");
        var headers = new List<KeyValuePair<string, string>>(5)
        {
            new(MessageIdHeader, id),
            new(KindHeader, envelope.Kind),
            new(SchemaHeader, schemaVersion.ToString(CultureInfo.InvariantCulture)),
            new(EnvelopeHeader, EnvelopeVersion.ToString(CultureInfo.InvariantCulture)),
        };
        if (activity is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent })
        {
            headers.Add(new(TraceParentHeader, traceParent));
        }

        var payload = LogEnvelopeJson.Serialize(envelope);
        var size = HeaderBlockOverhead + payload.Length;
        foreach (var (name, value) in headers)
        {
            size += Encoding.UTF8.GetByteCount(name) + 2 + Encoding.UTF8.GetByteCount(value) + 2;
        }

        return new EncodedRecord(id, headers, payload, size);
    }

    /// <summary>Decodes a message; returns <see langword="null"/> and the reason when it is not a record this node can read.</summary>
    public static LogRecord? Decode(BrokerDelivery delivery, out string? reason)
    {
        if (delivery.Headers.TryGetValue(EnvelopeHeader, out var version)
            && (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var envelopeVersion) || envelopeVersion > EnvelopeVersion))
        {
            reason = $"envelope version {version} is newer than {EnvelopeVersion}";
            return null;
        }

        LogEnvelope envelope;
        try
        {
            envelope = LogEnvelopeJson.Deserialize(delivery.Payload);
        }
        catch (FormatException ex)
        {
            reason = ex.Message;
            return null;
        }

        if (delivery.Headers.TryGetValue(KindHeader, out var kind) && !string.Equals(kind, envelope.Kind, StringComparison.Ordinal))
        {
            reason = "the Wp-Kind header and the envelope kind differ";
            return null;
        }

        var schema = 1;
        if (delivery.Headers.TryGetValue(SchemaHeader, out var schemaText)
            && (!int.TryParse(schemaText, NumberStyles.None, CultureInfo.InvariantCulture, out schema) || schema < 1))
        {
            reason = "the Wp-Schema header is not a positive number";
            return null;
        }

        reason = null;
        return new LogRecord(delivery.Sequence, envelope)
        {
            SchemaVersion = schema,
            TraceParent = delivery.Headers.TryGetValue(TraceParentHeader, out var traceParent) ? traceParent : null,
        };
    }
}
