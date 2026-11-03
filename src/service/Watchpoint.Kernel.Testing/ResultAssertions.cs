using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Testing;

/// <summary>
/// Shouldly-style assertions on <see cref="Result"/> and <see cref="Result{T}"/>. They throw
/// <see cref="ResultAssertionException"/> with the error code, arguments and field errors in the message.
/// </summary>
public static class ResultAssertions
{
    /// <summary>Asserts a success.</summary>
    /// <param name="result">The result.</param>
    public static void ShouldBeSuccess(this Result result)
    {
        if (!result.IsSuccess)
        {
            throw new ResultAssertionException("Expected success but got " + Describe(result));
        }
    }

    /// <summary>Asserts a success and returns the value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <returns>The value.</returns>
    public static T ShouldBeSuccess<T>(this Result<T> result) =>
        result.IsSuccess ? result.Value : throw new ResultAssertionException("Expected success but got " + Describe(result.ToResult()));

    /// <summary>Asserts a failure with the given code and returns the error.</summary>
    /// <param name="result">The result.</param>
    /// <param name="code">The expected error code.</param>
    /// <returns>The error.</returns>
    public static Error ShouldFailWith(this Result result, ErrorCode code)
    {
        if (result.IsSuccess)
        {
            throw new ResultAssertionException($"Expected failure {code} but got success.");
        }

        return CheckCode(result, code);
    }

    /// <summary>Asserts a failure with the given code and returns the error.</summary>
    /// <param name="result">The result.</param>
    /// <param name="code">The expected error code, e.g. <c>registry.device.not_found</c>.</param>
    /// <returns>The error.</returns>
    public static Error ShouldFailWith(this Result result, string code) => result.ShouldFailWith(ErrorCode.FromTrusted(code));

    /// <summary>Asserts a failure with the given code and returns the error.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="code">The expected error code.</param>
    /// <returns>The error.</returns>
    public static Error ShouldFailWith<T>(this Result<T> result, ErrorCode code) =>
        result.IsSuccess
            ? throw new ResultAssertionException($"Expected failure {code} but got success with {result.Value}.")
            : CheckCode(result.ToResult(), code);

    /// <summary>Asserts a failure with the given code and returns the error.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="code">The expected error code.</param>
    /// <returns>The error.</returns>
    public static Error ShouldFailWith<T>(this Result<T> result, string code) => result.ShouldFailWith(ErrorCode.FromTrusted(code));

    /// <summary>Asserts that a validation error contains a field error with the given path and code, and returns it.</summary>
    /// <param name="error">The error.</param>
    /// <param name="path">The expected JSON Pointer.</param>
    /// <param name="code">The expected field error code.</param>
    /// <returns>The field error.</returns>
    public static FieldError ShouldHaveFieldError(this Error error, string path, ErrorCode code)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.FieldErrors.FirstOrDefault(f => f.Path == path && f.Code == code)
            ?? throw new ResultAssertionException($"Expected field error {code} at '{path}' but got {Describe(error)}");
    }

    private static Error CheckCode(Result result, ErrorCode code) =>
        result.Error.Code == code
            ? result.Error
            : throw new ResultAssertionException($"Expected failure {code} but got {Describe(result)}");

    private static string Describe(Result result) =>
        result.IsSuccess ? "success." : result == default ? "an uninitialised result." : Describe(result.Error);

    private static string Describe(Error error)
    {
        var args = string.Join(", ", error.Args.Select(a => $"{a.Key}={Format(a.Value)}"));
        var fields = string.Join("; ", error.FieldErrors.Select(f => $"{f.Path} {f.Code}"));
        return $"failure {error.Code} ({error.Kind}) [{args}]" + (fields.Length > 0 ? $" fields: {fields}" : string.Empty) + ".";
    }

    private static string Format(object? value) =>
        value switch
        {
            null => "null",
            IReadOnlyList<string> list => "[" + string.Join(", ", list) + "]",
            _ => value.ToString() ?? string.Empty,
        };
}
