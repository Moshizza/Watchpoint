using Watchpoint.Kernel.Text;

namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>
/// A translatable message from the package's i18n bundle (INT §4). The key is relative to
/// <c>packages.&lt;packageId&gt;.</c>; arguments follow the Kernel rules (K §5.4: camelCase names, scalar types,
/// ids, lists of strings), anything else throws <see cref="ArgumentException"/>.
/// </summary>
public sealed record ConnectorMessage
{
    private const string ValidationPrefix = "packages.connector.";
    private readonly Message _message;

    /// <summary>Initialises a message.</summary>
    /// <param name="key">The key relative to the package bundle, e.g. <c>errors.loginRefused</c>.</param>
    /// <param name="args">The arguments.</param>
    /// <exception cref="ArgumentException">The key, an argument name or an argument type is invalid.</exception>
    public ConnectorMessage(string key, IReadOnlyDictionary<string, object?> args)
    {
        ArgumentNullException.ThrowIfNull(key);
        _message = new Message(ValidationPrefix + key, args);
        Key = key;
    }

    /// <summary>Gets the key relative to <c>packages.&lt;packageId&gt;.</c>.</summary>
    public string Key { get; }

    /// <summary>Gets the arguments.</summary>
    public IReadOnlyDictionary<string, object?> Args => _message.Args;

    /// <summary>Creates a message.</summary>
    /// <param name="key">The key relative to the package bundle.</param>
    /// <param name="args">The arguments as (name, value) pairs.</param>
    /// <returns>The message.</returns>
    /// <exception cref="ArgumentException">The key, an argument name or an argument type is invalid, or a name repeats.</exception>
    public static ConnectorMessage Of(string key, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ConnectorMessage(key, Message.Of(ValidationPrefix + key, args).Args);
    }

    /// <summary>Converts to a system message with the full key <c>packages.&lt;packageId&gt;.&lt;key&gt;</c>.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The message.</returns>
    public Message ToMessage(string packageId) => new("packages." + packageId + "." + Key, Args);

    /// <inheritdoc/>
    public bool Equals(ConnectorMessage? other) =>
        other is not null && Key == other.Key && Args.Count == other.Args.Count
        && Args.All(a => other.Args.TryGetValue(a.Key, out var v) && Equals(a.Value, v));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Key, Args.Count);
}
