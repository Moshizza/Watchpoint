using System.Globalization;
using System.Text.Json;
using Watchpoint.History.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>A <c>command.*</c> record as history needs it (read from the JSON body CMD writes, CMD §6).</summary>
/// <param name="Id">The command.</param>
/// <param name="State">The state the record moves the command to.</param>
/// <param name="Request">Whether the record is the request (it carries the whole command).</param>
/// <param name="RequestedAt">When the command was requested (request only).</param>
/// <param name="GroupId">The command group (request, rejection).</param>
/// <param name="Path">The command path.</param>
/// <param name="ArgumentsJson">The arguments.</param>
/// <param name="IssuerJson">The issuer.</param>
/// <param name="IssuerUser">The issuing user.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Risk">The effective risk as a number.</param>
/// <param name="Priority">The priority as a number.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="FailureJson">The failure or rejection error.</param>
/// <param name="ApprovalJson">The approval decision.</param>
internal sealed record ParsedCommandRecord(
    Guid Id,
    CommandHistoryState State,
    bool Request,
    DateTimeOffset? RequestedAt,
    Guid? GroupId,
    string? Path,
    string? ArgumentsJson,
    string? IssuerJson,
    Guid? IssuerUser,
    string? Reason,
    short? Risk,
    short? Priority,
    int? Attempt,
    string? FailureJson,
    string? ApprovalJson);

/// <summary>
/// Reads the JSON body of <c>command.*</c> records without a reference to the commands module: <c>command.requested</c> is the whole
/// command (camelCase members: <c>id</c>, <c>target</c>, <c>path</c>, <c>arguments</c>, <c>issuer</c>, <c>reason</c>,
/// <c>effectiveRisk</c>, <c>priority</c>, <c>requestedAt</c>, <c>groupId</c>), the others carry <c>id</c> and what changed.
/// </summary>
internal static class CommandRecordReader
{
    private static readonly string[] _priorities = ["critical", "high", "normal", "background"];

    private static readonly string[] _risks = ["normal", "sensitive", "critical"];

    /// <summary>The state a record kind moves a command to; <see langword="null"/> for an unknown kind. <c>approved</c> leaves it awaiting until the queued record.</summary>
    public static CommandHistoryState? StateOf(string kind) => kind switch
    {
        "command.requested" => CommandHistoryState.Requested,
        "command.rejected" or "command.approvalRejected" => CommandHistoryState.Rejected,
        "command.awaitingApproval" or "command.approved" => CommandHistoryState.AwaitingApproval,
        "command.queued" => CommandHistoryState.Queued,
        "command.dispatched" => CommandHistoryState.Dispatched,
        "command.succeeded" => CommandHistoryState.Succeeded,
        "command.failed" => CommandHistoryState.Failed,
        "command.timedOut" => CommandHistoryState.TimedOut,
        "command.expired" => CommandHistoryState.Expired,
        "command.cancelled" => CommandHistoryState.Cancelled,
        "command.verified" => CommandHistoryState.Verified,
        "command.unverified" => CommandHistoryState.Unverified,
        _ => null,
    };

    /// <summary>Reads a record; <see langword="null"/> with a reason when it cannot be used.</summary>
    public static ParsedCommandRecord? Read(LogRecord record, out string? problem)
    {
        problem = null;
        var kind = record.Envelope.Kind;
        if (StateOf(kind) is not { } state)
        {
            problem = "unknown command record kind";
            return null;
        }

        var body = record.Envelope.Body;
        if (body.ValueKind != JsonValueKind.Object || !Guid.TryParse(Text(body, "id"), out var id))
        {
            problem = "the body has no command id";
            return null;
        }

        var request = state == CommandHistoryState.Requested;
        var approval = kind is "command.approved" or "command.approvalRejected"
            ? HistoryJson.Serialize(new Dictionary<string, Watchpoint.Kernel.Values.WpValue>
            {
                ["decision"] = Watchpoint.Kernel.Values.WpValue.String(kind == "command.approved" ? "approved" : "rejected"),
                ["by"] = Watchpoint.Kernel.Values.WpValue.String(Text(body, "by") ?? string.Empty),
                ["reason"] = Watchpoint.Kernel.Values.WpValue.String(Text(body, "reason") ?? string.Empty),
            })
            : null;
        var issuer = Raw(body, "issuer");
        var failure = kind == "command.failed" ? Raw(body, "failure") : kind == "command.rejected" ? Raw(body, "error") : null;
        return new ParsedCommandRecord(
            id,
            state,
            request,
            request ? Time(body, "requestedAt") : null,
            Guid.TryParse(Text(body, "groupId"), out var group) ? group : null,
            Text(body, "path"),
            Raw(body, "arguments"),
            issuer,
            issuer is not null && Issuer(issuer) is { } user ? user : null,
            kind is "command.requested" or "command.rejected" ? Text(body, "reason") : null,
            Enum(body, "effectiveRisk", _risks),
            Enum(body, "priority", _priorities),
            body.TryGetProperty("attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number && attempt.TryGetInt32(out var n) ? n : null,
            failure,
            approval);
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Raw(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.GetRawText() : null;

    private static DateTimeOffset? Time(JsonElement body, string name) =>
        Text(body, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;

    private static short? Enum(JsonElement body, string name, string[] names)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt16(out var number))
        {
            return number;
        }

        var index = value.ValueKind == JsonValueKind.String ? Array.FindIndex(names, n => string.Equals(n, value.GetString(), StringComparison.OrdinalIgnoreCase)) : -1;
        return index < 0 ? null : (short)index;
    }

    /// <summary>The user behind an issuer: <c>userId</c> of a user, <c>identityId</c> of an automation or script.</summary>
    private static Guid? Issuer(string issuerJson)
    {
        using var document = JsonDocument.Parse(issuerJson);
        foreach (var name in new[] { "userId", "identityId" })
        {
            if (Text(document.RootElement, name) is { } text && Guid.TryParse(text, out var id))
            {
                return id;
            }
        }

        return null;
    }
}
