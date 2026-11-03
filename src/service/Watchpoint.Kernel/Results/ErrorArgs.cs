using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Results;

/// <summary>Validation of error and message arguments (spec K §5.4).</summary>
internal static partial class ErrorArgs
{
    public static readonly IReadOnlyDictionary<string, object?> Empty =
        FrozenDictionary<string, object?>.Empty;

    private static readonly FrozenSet<Type> _scalarTypes = new[]
    {
        typeof(string), typeof(int), typeof(long), typeof(double), typeof(decimal),
        typeof(bool), typeof(DateTimeOffset), typeof(Guid),
    }.ToFrozenSet();

    public static IReadOnlyDictionary<string, object?> Validate(IReadOnlyDictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var (key, value) in args)
        {
            Check(key, value);
        }

        return args;
    }

    public static IReadOnlyDictionary<string, object?> Create((string Key, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return Empty;
        }

        var result = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            Check(key, value);
            if (!result.TryAdd(key, value))
            {
                throw new ArgumentException($"Duplicate argument '{key}'.", nameof(args));
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, object?> With(IReadOnlyDictionary<string, object?> args, string key, object? value)
    {
        Check(key, value);
        return new Dictionary<string, object?>(args, StringComparer.Ordinal) { [key] = value };
    }

    public static bool IsAllowedValue(object? value) =>
        value switch
        {
            null => true,
            IStringId => true,
            IReadOnlyList<string> => true,
            _ => _scalarTypes.Contains(value.GetType()) || IsEntityId(value.GetType()),
        };

    private static void Check(string key, object? value)
    {
        if (key is null || !NamePattern().IsMatch(key))
        {
            throw new ArgumentException($"Argument name '{key}' must be camelCase.", nameof(key));
        }

        if (!IsAllowedValue(value))
        {
            throw new ArgumentException(
                $"Argument '{key}' has type {value!.GetType().FullName}, which is not allowed in errors and messages (spec K §5.4).",
                nameof(value));
        }
    }

    private static bool IsEntityId(Type type) =>
        type.IsValueType
        && Array.Exists(type.GetInterfaces(), i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityId<>));

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
