using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// The envelope of every event log record (spec PIP §5.2). Serialised as JSON with a fixed member order; readers ignore
/// members they do not know, so members can be added without breaking older nodes.
/// </summary>
/// <param name="Id">The record id (UUID v7); the deduplication key of the log (<c>Nats-Msg-Id</c>).</param>
/// <param name="Kind">The record kind, e.g. <c>property</c> or <c>alarm.raised</c> (see <see cref="RecordKinds"/>).</param>
/// <param name="Ts">The received time (node clock, UTC, millisecond precision).</param>
/// <param name="Src">The source time of the integrated system, if known.</param>
/// <param name="P">The partition (0 for records of the global streams).</param>
/// <param name="Dev">The device the record is about, if any.</param>
/// <param name="Srv">The server the record is about, or the server of <paramref name="Dev"/>.</param>
/// <param name="Origin">Who produced the record.</param>
/// <param name="Corr">What the record is correlated with.</param>
/// <param name="Body">The kind-specific body (a JSON object).</param>
public sealed record LogEnvelope(
    Guid Id,
    string Kind,
    DateTimeOffset Ts,
    DateTimeOffset? Src,
    int P,
    DeviceId? Dev,
    ServerId? Srv,
    RecordOrigin Origin,
    RecordCorrelation Corr,
    JsonElement Body)
{
    /// <summary>The longest allowed record kind.</summary>
    public const int MaxKindLength = 64;

    /// <summary>
    /// Returns whether <paramref name="kind"/> is a well-formed record kind: 1 to 4 dot-separated tokens of ASCII letters
    /// and digits, each starting with a lowercase letter, at most <see cref="MaxKindLength"/> characters.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> when the kind can be used in a log subject.</returns>
    public static bool IsValidKind(string? kind)
    {
        if (string.IsNullOrEmpty(kind) || kind.Length > MaxKindLength)
        {
            return false;
        }

        var tokens = 1;
        var tokenStart = true;
        foreach (var c in kind)
        {
            if (c == '.')
            {
                if (tokenStart || ++tokens > 4)
                {
                    return false;
                }

                tokenStart = true;
            }
            else if (tokenStart)
            {
                if (c is < 'a' or > 'z')
                {
                    return false;
                }

                tokenStart = false;
            }
            else if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return !tokenStart;
    }

    /// <summary>
    /// Checks the envelope for the stream it is appended to: id, kind and received time present; for
    /// <see cref="LogStream.Log"/> a device or a server and the partition of that entity
    /// (<see cref="Partitioning.Of(DeviceId)"/>, the device when both are set); partition 0 for the global streams.
    /// </summary>
    /// <param name="stream">The stream the record is appended to.</param>
    /// <returns>Success, or <c>pipeline.record.invalid</c> with the reason.</returns>
    public Result Validate(LogStream stream)
    {
        if (Id == Guid.Empty)
        {
            return PipelineErrors.RecordInvalid(Id, "id is missing");
        }

        if (!IsValidKind(Kind))
        {
            return PipelineErrors.RecordInvalid(Id, "kind is missing or malformed");
        }

        if (Ts == default)
        {
            return PipelineErrors.RecordInvalid(Id, "ts is missing");
        }

        if (Origin is null || Corr is null)
        {
            return PipelineErrors.RecordInvalid(Id, "origin or corr is missing");
        }

        if (Body.ValueKind != JsonValueKind.Object)
        {
            return PipelineErrors.RecordInvalid(Id, "body is not a JSON object");
        }

        if (stream == LogStream.Log)
        {
            if (Dev is null && Srv is null)
            {
                return PipelineErrors.RecordInvalid(Id, "a device-bound record needs dev or srv");
            }

            var expected = Dev is { } device ? Partitioning.Of(device) : Partitioning.Of(Srv!.Value);
            if (P != expected)
            {
                return PipelineErrors.RecordInvalid(Id, $"p is {P} but the partition of the entity is {expected}");
            }
        }
        else if (P != 0)
        {
            return PipelineErrors.RecordInvalid(Id, "p must be 0 in a global stream");
        }

        return Result.Success();
    }
}
