using System.Diagnostics.CodeAnalysis;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Results;

/// <summary>
/// A business failure: a stable code, a kind (mapped to an HTTP status by the API) and arguments for the
/// translated message <c>errors.&lt;code&gt;</c>. Business failures are returned in a <see cref="Result"/>,
/// never thrown across module boundaries.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec K §5.2.")]
public sealed record Error
{
    private readonly IReadOnlyDictionary<string, object?> _args = ErrorArgs.Empty;
    private readonly IReadOnlyList<FieldError> _fieldErrors = [];

    /// <summary>Gets the error code, e.g. <c>registry.device.not_found</c>.</summary>
    public required ErrorCode Code { get; init; }

    /// <summary>Gets the error kind.</summary>
    public required ErrorKind Kind { get; init; }

    /// <summary>
    /// Gets the message arguments (camelCase names; allowed value types in spec K §5.4, others throw
    /// <see cref="ArgumentException"/>).
    /// </summary>
    public IReadOnlyDictionary<string, object?> Args
    {
        get => _args;
        init => _args = ErrorArgs.Validate(value);
    }

    /// <summary>Gets the field errors (only for <see cref="ErrorKind.Validation"/>).</summary>
    public IReadOnlyList<FieldError> FieldErrors
    {
        get => _fieldErrors;
        init => _fieldErrors = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the translation key of the message: <c>errors.&lt;code&gt;</c>.</summary>
    public string MessageKey => "errors." + Code.Value;

    /// <summary>Gets a technical detail for logs; never sent to clients.</summary>
    public string? DeveloperDetail { get; init; }

    /// <summary>Creates an error of any kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="code">The error code; must match the <see cref="ErrorCode"/> pattern.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    /// <exception cref="ArgumentException">The code or an argument is invalid.</exception>
    public static Error Create(ErrorKind kind, string code, params (string Key, object? Value)[] args) =>
        new() { Code = ErrorCode.Of(code), Kind = kind, Args = ErrorArgs.Create(args) };

    /// <summary>Creates a validation error carrying field errors.</summary>
    /// <param name="code">The error code, usually <c>common.validation.failed</c>.</param>
    /// <param name="fields">The field errors.</param>
    /// <returns>The error.</returns>
    public static Error Validation(string code, IReadOnlyList<FieldError> fields) =>
        new() { Code = ErrorCode.Of(code), Kind = ErrorKind.Validation, FieldErrors = fields };

    /// <summary>Creates a <see cref="ErrorKind.NotFound"/> error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string code, params (string Key, object? Value)[] args) =>
        Create(ErrorKind.NotFound, code, args);

    /// <summary>Creates a <see cref="ErrorKind.Conflict"/> error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    public static Error Conflict(string code, params (string Key, object? Value)[] args) =>
        Create(ErrorKind.Conflict, code, args);

    /// <summary>Creates a <see cref="ErrorKind.Forbidden"/> error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(string code, params (string Key, object? Value)[] args) =>
        Create(ErrorKind.Forbidden, code, args);

    /// <summary>Creates a <see cref="ErrorKind.Unprocessable"/> error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    public static Error Unprocessable(string code, params (string Key, object? Value)[] args) =>
        Create(ErrorKind.Unprocessable, code, args);

    /// <summary>Creates a <see cref="ErrorKind.Unavailable"/> error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The error.</returns>
    public static Error Unavailable(string code, params (string Key, object? Value)[] args) =>
        Create(ErrorKind.Unavailable, code, args);

    /// <summary>Creates the <c>common.internal</c> error.</summary>
    /// <param name="developerDetail">Technical detail for logs; never sent to clients.</param>
    /// <returns>The error.</returns>
    public static Error Internal(string developerDetail) =>
        new() { Code = CommonErrorCodes.Internal, Kind = ErrorKind.Internal, DeveloperDetail = developerDetail };

    /// <summary>Returns a copy of this error with one argument added or replaced; this error is unchanged.</summary>
    /// <param name="key">The argument name (camelCase).</param>
    /// <param name="value">The argument value (allowed types in spec K §5.4).</param>
    /// <returns>The new error.</returns>
    public Error WithArg(string key, object? value) => this with { Args = ErrorArgs.With(Args, key, value) };
}
