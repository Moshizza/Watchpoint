using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Results;

/// <summary>A validation failure of one field.</summary>
/// <param name="Path">JSON Pointer (RFC 6901) relative to the validated document, e.g. <c>/settings/port</c>; empty for the whole document.</param>
/// <param name="Code">The error code, e.g. <c>common.value.required</c>.</param>
/// <param name="Args">The message arguments (allowed value types in spec K §5.4).</param>
public sealed record FieldError(string Path, ErrorCode Code, IReadOnlyDictionary<string, object?> Args)
{
    /// <summary>Gets the message arguments.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } = ErrorArgs.Validate(Args);

    /// <summary>Gets the translation key of the message: <c>errors.&lt;code&gt;</c>.</summary>
    public string MessageKey => "errors." + Code.Value;

    /// <summary>Creates a field error.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="code">The error code.</param>
    /// <param name="args">The message arguments.</param>
    /// <returns>The field error.</returns>
    public static FieldError Create(string path, ErrorCode code, params (string Key, object? Value)[] args) =>
        new(path, code, ErrorArgs.Create(args));
}
