using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Discriminated value indicating whether a patch property was specified (and what its value is) or omitted.
/// </summary>
/// <typeparam name="T">The underlying property type.</typeparam>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Matches spec definition for Optional<T>")]
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T _value;

    /// <summary>
    /// Initializes a specified optional value.
    /// </summary>
    /// <param name="value">The specified value.</param>
    public Optional(T value)
    {
        IsSpecified = true;
        _value = value;
    }

    /// <summary>
    /// Gets whether a value was specified.
    /// </summary>
    public bool IsSpecified { get; }

    /// <summary>
    /// Gets the value if specified, or default.
    /// </summary>
    public T? Value => _value;

    /// <summary>
    /// Implicit conversion from a value of type T to an Optional with that value.
    /// </summary>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <inheritdoc />
    public bool Equals(Optional<T> other) =>
        IsSpecified == other.IsSpecified && EqualityComparer<T>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsSpecified, _value);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);
}
