namespace Watchpoint.Configuration.Internal.Validation;

/// <summary>Builds the argument dictionaries of problems (insertion order is kept, so problems serialise the same way every time).</summary>
internal static class Args
{
    public static IReadOnlyDictionary<string, object?> Of(params (string Name, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(values.Length, StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            result[name] = value;
        }

        return result;
    }
}
