namespace Watchpoint.Kernel.Results;

/// <summary>
/// Outcome of an operation without a value: success, or failure with an <see cref="Results.Error"/>.
/// <see langword="default"/> is invalid: <see cref="IsSuccess"/> is <see langword="false"/> and
/// <see cref="Error"/> throws "uninitialised result".
/// </summary>
public readonly struct Result : IEquatable<Result>
{
    private static readonly Result _success = new(null, initialised: true);

    private readonly Error? _error;
    private readonly bool _initialised;

    private Result(Error? error, bool initialised)
    {
        _error = error;
        _initialised = initialised;
    }

    /// <summary>Gets a value indicating whether the operation succeeded.</summary>
    public bool IsSuccess => _initialised && _error is null;

    /// <summary>Gets the error.</summary>
    /// <exception cref="InvalidOperationException">The result is a success or uninitialised.</exception>
    public Error Error => _error ?? throw ResultGuard.NoError(_initialised);

    /// <summary>Returns the success result.</summary>
    /// <returns>A success.</returns>
    public static Result Success() => _success;

    /// <summary>Returns a failure.</summary>
    /// <param name="error">The error.</param>
    /// <returns>A failure.</returns>
    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)), initialised: true);

    /// <summary>Returns a success carrying a value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>A success.</returns>
    public static Result<T> Success<T>(T value) => value;

    /// <summary>Returns a failure for an operation that returns a value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="error">The error.</param>
    /// <returns>A failure.</returns>
    public static Result<T> Failure<T>(Error error) => error;

    /// <summary>Converts an error to a failure.</summary>
    /// <param name="error">The error.</param>
    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>Converts an error to a failure (named alternative of the implicit operator).</summary>
    /// <param name="error">The error.</param>
    /// <returns>A failure.</returns>
    public static Result FromError(Error error) => Failure(error);

    /// <inheritdoc />
    public bool Equals(Result other) => _initialised == other._initialised && Equals(_error, other._error);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Result other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_initialised, _error);

    /// <inheritdoc />
    public override string ToString() =>
        !_initialised ? "Uninitialised" : _error is null ? "Success" : $"Failure({_error.Code})";

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(Result left, Result right) => left.Equals(right);

    /// <summary>Compares two results.</summary>
    /// <param name="left">The first result.</param>
    /// <param name="right">The second result.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(Result left, Result right) => !left.Equals(right);
}
