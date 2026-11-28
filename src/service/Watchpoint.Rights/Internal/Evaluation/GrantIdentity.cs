using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>Canonical form of a grant: <c>(permission, normalised scope, schedule, normalised constraints)</c> (spec RGT §5).</summary>
internal static class GrantIdentity
{
    public static string Canonical(RightGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var sb = new StringBuilder();
        sb.Append(grant.Permission.Value).Append('|').Append(CanonicalScope(grant.Scope)).Append('|').Append(grant.ScheduleCalendar ?? string.Empty).Append('|').Append(CanonicalConstraints(grant.Constraints));
        return sb.ToString();
    }

    public static string Hash(RightGrant grant) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(grant))));

    public static string CanonicalScope(GrantScope s)
    {
        if (s.Everything)
        {
            return "*";
        }

        return string.Join(';',
            List("loc", s.LocationsSubtree?.Select(x => x.Value.ToString())),
            List("srv", s.Servers?.Select(x => x.Value.ToString())),
            List("cat", s.Categories?.Select(x => x.ToString())),
            List("typ", s.Types?.Select(x => x.Value)),
            List("tga", s.TagsAny?.Select(x => x.Value.ToString())),
            List("tgx", s.TagsExclude?.Select(x => x.Value.ToString())),
            List("dev", s.Devices?.Select(x => x.Value.ToString())),
            List("sit", s.Sites?.Select(x => x.Value.ToString())));
    }

    public static string CanonicalConstraints(GrantConstraints c) =>
        string.Join(';',
            c.RequireReason ? "reason" : string.Empty,
            c.RequireFourEyes ? "4eyes" : string.Empty,
            c.RequireStepUp ? "stepup" : string.Empty,
            c.MaxDuration is { } d ? "dur=" + d.TotalSeconds.ToString(CultureInfo.InvariantCulture) : string.Empty,
            c.Priorities is { } p ? $"prio={p.Min}-{p.Max}" : string.Empty,
            c.ClientKinds is { } k ? "clients=" + string.Join(',', k.Select(x => x.ToString()).Order(StringComparer.Ordinal)) : string.Empty,
            c.MaxLookback is { } l ? "look=" + l.TotalSeconds.ToString(CultureInfo.InvariantCulture) : string.Empty);

    private static string List(string name, IEnumerable<string>? values) =>
        values is null ? string.Empty : name + "=" + string.Join(',', values.Order(StringComparer.Ordinal));
}
