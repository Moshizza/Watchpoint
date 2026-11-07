using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Watchpoint.Kernel.Validation;

/// <summary>
/// Argument checks for programming errors (parameters from code, not from users): they throw exceptions of the
/// <see cref="ArgumentException"/> family. Validate user input with <see cref="ValidationBuilder"/> instead.
/// </summary>
[SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Optional parameters are caller-filled argument names.")]
public static class Guard
{
    /// <summary>Throws <see cref="ArgumentNullException"/> when the value is <see langword="null"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="paramName">The parameter name (filled by the compiler).</param>
    /// <returns>The value.</returns>
    public static T NotNull<T>([NotNull] T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where T : class =>
        value ?? throw new ArgumentNullException(paramName);

    /// <summary>
    /// Throws <see cref="ArgumentNullException"/> for <see langword="null"/> and <see cref="ArgumentException"/> for an
    /// empty or blank string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="paramName">The parameter name (filled by the compiler).</param>
    /// <returns>The value.</returns>
    public static string NotEmpty([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value must not be empty.", paramName) : value;
    }

    /// <summary>
    /// Throws <see cref="ArgumentNullException"/> for <see langword="null"/> and <see cref="ArgumentException"/> for an
    /// empty collection.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="value">The collection.</param>
    /// <param name="paramName">The parameter name (filled by the compiler).</param>
    /// <returns>The collection.</returns>
    public static IReadOnlyCollection<T> NotEmpty<T>(
        [NotNull] IReadOnlyCollection<T>? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value.Count == 0 ? throw new ArgumentException("Collection must not be empty.", paramName) : value;
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when the value is outside <c>[min, max]</c>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="min">The minimum (inclusive).</param>
    /// <param name="max">The maximum (inclusive).</param>
    /// <param name="paramName">The parameter name (filled by the compiler).</param>
    /// <returns>The value.</returns>
    public static T InRange<T>(T value, T min, T max, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where T : IComparable<T> =>
        value.CompareTo(min) < 0 || value.CompareTo(max) > 0
            ? throw new ArgumentOutOfRangeException(paramName, value, $"Value must be between {min} and {max}.")
            : value;
}
