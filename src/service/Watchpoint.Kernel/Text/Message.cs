using System.Text.RegularExpressions;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Text;

/// <summary>
/// A translatable text produced by the system (events, notices): a translation key and arguments (spec K §8.1).
/// Argument rules are those of errors (spec K §5.4): disallowed types throw <see cref="ArgumentException"/>.
/// </summary>
public sealed partial record Message
{
    private readonly string _key = string.Empty;
    private readonly IReadOnlyDictionary<string, object?> _args = ErrorArgs.Empty;

    /// <summary>Initialises a message.</summary>
    /// <param name="key">The translation key: lowercase dotted, e.g. <c>events.door.forced.title</c>.</param>
    /// <param name="args">The arguments.</param>
    public Message(string key, IReadOnlyDictionary<string, object?> args)
    {
        Key = key;
        Args = args;
    }

    /// <summary>The pattern of keys: at least two dot-separated segments, each starting with a lowercase letter.</summary>
    public const string KeyPattern = @"^[a-z][a-zA-Z0-9_]*(\.[a-z][a-zA-Z0-9_]*)+$";

    /// <summary>Gets the translation key.</summary>
    public string Key
    {
        get => _key;
        init => _key = value is not null && KeyRegex().IsMatch(value)
            ? value
            : throw new ArgumentException($"'{value}' is not a valid message key (pattern {KeyPattern}).", nameof(value));
    }

    /// <summary>Gets the arguments (camelCase names).</summary>
    public IReadOnlyDictionary<string, object?> Args
    {
        get => _args;
        init => _args = ErrorArgs.Validate(value);
    }

    /// <summary>Creates a message.</summary>
    /// <param name="key">The translation key.</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">The key, an argument name or an argument type is invalid.</exception>
    public static Message Of(string key, params (string Name, object? Value)[] args) => new(key, ErrorArgs.Create(args));

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9_]*(\.[a-z][a-zA-Z0-9_]*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
