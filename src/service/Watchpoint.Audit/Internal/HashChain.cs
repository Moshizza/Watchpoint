using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Canonical serialisation and SHA-256 chaining of audit rows (spec AUD §6).</summary>
internal static class HashChain
{
    public const int HashLength = 32;

    /// <summary><c>PrevHash</c> of the first record of a chain: SHA-256 of <c>"watchpoint-audit:" + chainId</c>.</summary>
    public static byte[] Genesis(string chainId) => SHA256.HashData(Encoding.UTF8.GetBytes("watchpoint-audit:" + chainId));

    /// <summary>Timestamps are stored with microsecond precision, so they are truncated before they are hashed.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    /// <summary><c>C(r)</c>: UTF-8 JSON with fixed property order and canonical nested values.</summary>
    public static byte[] Canonical(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var b = new StringBuilder(256);
        b.Append("{\"sequence\":").Append(row.Sequence.ToString(CultureInfo.InvariantCulture));
        b.Append(",\"chainId\":");
        CanonicalJson.WriteString(b, row.ChainId);
        b.Append(",\"occurredAt\":");
        CanonicalJson.WriteString(b, Iso(row.OccurredAt));
        b.Append(",\"recordedAt\":");
        CanonicalJson.WriteString(b, Iso(row.RecordedAt));
        b.Append(",\"action\":");
        CanonicalJson.WriteString(b, row.Action);
        b.Append(",\"outcome\":").Append(row.Outcome.ToString(CultureInfo.InvariantCulture));
        b.Append(",\"severity\":").Append(row.Severity.ToString(CultureInfo.InvariantCulture));
        b.Append(",\"actor\":").Append(CanonicalJson.Canonicalize(row.ActorJson));
        b.Append(",\"objects\":").Append(CanonicalJson.Canonicalize(row.ObjectsJson));
        b.Append(",\"reason\":");
        if (row.Reason is null)
        {
            b.Append("null");
        }
        else
        {
            CanonicalJson.WriteString(b, row.Reason);
        }

        b.Append(",\"details\":").Append(row.DetailsJson is null ? "null" : CanonicalJson.Canonicalize(row.DetailsJson));
        b.Append(",\"correlationId\":");
        if (row.CorrelationId is { } correlation)
        {
            CanonicalJson.WriteString(b, correlation.ToString("D", CultureInfo.InvariantCulture));
        }
        else
        {
            b.Append("null");
        }

        b.Append('}');
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    /// <summary><c>SHA-256(prevHash || C(r))</c>.</summary>
    public static byte[] ComputeHash(byte[] prevHash, AuditRow row)
    {
        var canonical = Canonical(row);
        var buffer = new byte[prevHash.Length + canonical.Length];
        prevHash.CopyTo(buffer, 0);
        canonical.CopyTo(buffer, prevHash.Length);
        return SHA256.HashData(buffer);
    }

    /// <summary>Returns <paramref name="row"/> chained after <paramref name="prevHash"/>.</summary>
    public static AuditRow Seal(AuditRow row, byte[] prevHash) => row with { PrevHash = prevHash, Hash = ComputeHash(prevHash, row with { PrevHash = prevHash }) };

    /// <summary>The bytes a checkpoint signature covers: <c>chainId|sequence|hash|at</c>.</summary>
    public static byte[] CheckpointPayload(string chainId, long sequence, byte[] hash, DateTimeOffset at) =>
        Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{chainId}|{sequence}|{Convert.ToHexStringLower(hash)}|{Iso(at)}"));

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
