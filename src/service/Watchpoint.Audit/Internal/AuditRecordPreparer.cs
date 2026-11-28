using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Watchpoint.Audit.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Validates, redacts and canonicalises a record before it is queued (spec AUD §5, §13).</summary>
internal static partial class AuditRecordPreparer
{
    public const string Mask = "•••";

    public static PreparedRecord Prepare(AuditRecord record, DateTimeOffset now, int maxDetailsBytes, out bool actionNormalised)
    {
        ArgumentNullException.ThrowIfNull(record);
        var action = record.Action.Value;
        actionNormalised = false;
        if (!AuditAction.IsValid(action))
        {
#if DEBUG
            throw new ArgumentException($"audit.action.invalid: '{action}' does not match <module>.<verb>.", nameof(record));
#else
            action = AuditAction.Unknown.Value;
            actionNormalised = true;
#endif
        }

        var objects = record.Objects ?? [];
        var stored = objects.Select(o => new StoredObject(o.Type, o.Id)).ToArray();
        return new PreparedRecord(
            HashChain.Truncate(record.OccurredAt ?? now),
            action,
            (short)record.Outcome,
            (short)record.Severity,
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(record.Actor, AuditJsonContext.Default.AuditActor)),
            record.Actor.Id,
            CanonicalJson.Canonicalize(JsonSerializer.Serialize(stored, AuditJsonContext.Default.StoredObjectArray)),
            [.. objects.Where(o => o.Id.HasValue).Select(o => o.Id!.Value).Distinct()],
            record.Reason,
            PrepareDetails(record.Details, maxDetailsBytes),
            record.CorrelationId);
    }

    public static string? PrepareDetails(JsonElement? details, int maxBytes)
    {
        if (details is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        var node = JsonNode.Parse(element.GetRawText());
        node = Redact(node);
        var canonical = CanonicalJson.Canonicalize(node?.ToJsonString() ?? "null");
        var bytes = Encoding.UTF8.GetBytes(canonical);
        if (bytes.Length <= maxBytes)
        {
            return canonical;
        }

        return "{\"sha256\":\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\",\"truncated\":true}";
    }

    private static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("$secret"))
                {
                    return JsonValue.Create(Mask);
                }

                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[key] = SensitiveName().IsMatch(key) ? JsonValue.Create(Mask) : Redact(value);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(Redact(item));
                }

                return items;
            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex(@"^(.*(password|secret|token|apikey|authorization|cookie).*|pin)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SensitiveName();
}
