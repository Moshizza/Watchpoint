using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Paging;

/// <summary>A sort order: a list of fields, each ascending or descending.</summary>
/// <param name="Fields">The fields, most significant first.</param>
public sealed record SortSpec(IReadOnlyList<SortField> Fields)
{
    /// <summary>
    /// Parses a sort expression such as <c>"name,-createdAt"</c> (comma-separated fields, <c>-</c> prefix for
    /// descending) and validates the fields against the allowed set.
    /// </summary>
    /// <param name="text">The expression; <see langword="null"/> or blank returns <paramref name="default"/>.</param>
    /// <param name="allowed">The allowed field names (comparison of the set is used).</param>
    /// <param name="default">The sort order when no expression is given.</param>
    /// <returns>
    /// The sort order, or <c>common.sort.invalid_field</c> (arguments <c>field</c> and <c>allowed</c>, sorted) for an
    /// unknown, empty or repeated field.
    /// </returns>
    public static Result<SortSpec> Parse(string? text, IReadOnlySet<string> allowed, SortSpec @default)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(@default);
        if (string.IsNullOrWhiteSpace(text))
        {
            return @default;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        var fields = new List<SortField>(parts.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            var descending = part.StartsWith('-');
            var name = descending ? part[1..] : part;
            if (name.Length == 0 || !allowed.Contains(name) || !seen.Add(name))
            {
                return CommonErrors.SortInvalidField(part, [.. allowed.Order(StringComparer.Ordinal)]);
            }

            fields.Add(new SortField(name, descending));
        }

        return new SortSpec(fields);
    }
}
