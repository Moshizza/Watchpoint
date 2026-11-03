using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Kernel.Results;

/// <summary>
/// Outcome of an operation that returns a value: success with a <typeparamref name="T"/>, or failure with an
/// <see cref="Results.Error"/>. <see langword="default"/> is invalid: <see cref="IsSuccess"/> is
/// <see langword="false"/> and <see cref="Error"/> throws "uninitialised result".
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly struct Result<T> : IEquatable<Result<T>>
{
    private readonly T _value;
    private readonly Error? _error;
    private readonly bool _initialised;

    private Result(T value, Error? error)
    {
        _value = value;
        _error = error;
        _initialised = true;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess => _initialised && _error is null;

    /// <summary>Gets the value.</summary>
    /// <exception cref="InvalidOperationException">The result is a failure or uninitialised.</exception>
    public T Value => IsSuccess ? _value : throw ResultGuard.NoValue(_initialised);

    /// <summary>Gets the error.</summary>
    /// <exception cref="InvalidOperationException">The result is a success or uninitialised.</exception>
    public Error Error => _error ?? throw ResultGuard.NoError(_initialised);

    /// <summary>Converts a value to a success.</summary>
    /// <param name="value">The value.</param>
    [SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Named alternative is Result.Success<T>.")]
    public static implicit operator Result<T>(T value) => new(value, null);

    /// <summary>Converts an error to a failure.</summary>
    /// <param name="error">The error.</param>
    [SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Named alternative is Result.Failure<T>.")]
    public static implicit operator Result<T>(Error error) =>
        new(default!, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Converts to a <see cref="Result"/> without value (same error).</summary>
    /// <returns>The result without value.</returns>
    public Result ToResult() => IsSuccess ? Result.Success() : Result.Failure(Error);

    /// <summary>Gets the value or the error.</summary>
    /// <param name="value">The value on success.</param>
    /// <param name="error">The error on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    /// <exception cref="InvalidOperationException">The result is uninitialised.</exception>
    public bool TryGetValue([MaybeNullWhen(false)] out T value, [MaybeNullWhen(true)] out Error error)
    {
        if (IsSuccess)
        {
            value = _value;
            error = null;
            return true;
        }

        value = default;
        error = Error;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(Result<T> other) =>
        _initialised == other._initialised
        && Equals(_error, other._error)
        && EqualityComparer<T>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_initialised, _error, _value);

    /// <inheritdoc />
    public override string ToString() =>
        !_initialised ? "Uninitialised" : _error is null ? $"Success({_value})" : $"Failure({_error.Code})";

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Result<T> left, Result<T> right) => left.Equals(right);

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(Result<T> left, Result<T> right) => !left.Equals(right);
}
