using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Kernel.Validation;

/// <summary>
/// Collects all field problems of a document (does not stop at the first) and builds a <see cref="Result"/>
/// (spec K §9.2). Paths are JSON Pointers (build them with <see cref="JsonPointer"/>). Rules other than
/// <see cref="Required"/> skip <see langword="null"/> values, so combine them with <see cref="Required"/> for
/// mandatory fields.
/// </summary>
public sealed class ValidationBuilder
{
    private readonly List<FieldError> _errors = [];
    private readonly ILogger? _logger;

    /// <summary>Initialises a builder.</summary>
    public ValidationBuilder()
    {
    }

    /// <summary>Initialises a builder that logs failures (event 1002, Debug).</summary>
    /// <param name="logger">The logger.</param>
    public ValidationBuilder(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Gets the field errors collected so far.</summary>
    public IReadOnlyList<FieldError> Errors => _errors;

    /// <summary>Gets a value indicating whether no error was collected.</summary>
    public bool IsValid => _errors.Count == 0;

    /// <summary>Adds <c>common.value.required</c> when the value is <see langword="null"/> or a blank string.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The value.</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Required(string path, object? value)
    {
        if (value is null || (value is string s && string.IsNullOrWhiteSpace(s)))
        {
            _errors.Add(CommonErrors.RequiredAt(path));
        }

        return this;
    }

    /// <summary>Applies <see cref="NameRules"/> to a non-null value.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The name.</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Name(string path, string? value)
    {
        if (value is not null)
        {
            _errors.AddRange(NameRules.Validate(value, path));
        }

        return this;
    }

    /// <summary>Adds <c>common.value.out_of_range</c> when a non-null value is outside <c>[min, max]</c>.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The value.</param>
    /// <param name="min">The minimum (inclusive).</param>
    /// <param name="max">The maximum (inclusive).</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Range(string path, long? value, long min, long max)
    {
        if (value is { } v && (v < min || v > max))
        {
            _errors.Add(CommonErrors.OutOfRangeAt(path, min, max));
        }

        return this;
    }

    /// <summary>Adds <c>common.value.too_long</c> when a non-null string is longer than <paramref name="max"/>.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The value.</param>
    /// <param name="max">The maximum length (UTF-16 code units).</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder MaxLength(string path, string? value, int max)
    {
        if (value is not null && value.Length > max)
        {
            _errors.Add(CommonErrors.TooLongAt(path, max));
        }

        return this;
    }

    /// <summary>Adds <c>common.value.invalid_format</c> when a non-null string does not match the pattern.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The value.</param>
    /// <param name="pattern">The pattern (anchor it).</param>
    /// <param name="expected">Description of the expected format, argument <c>expected</c> of the error.</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Pattern(string path, string? value, Regex pattern, string expected)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (value is not null && !pattern.IsMatch(value))
        {
            _errors.Add(CommonErrors.InvalidFormatAt(path, expected));
        }

        return this;
    }

    /// <summary>
    /// Runs a custom check. The returned errors have paths relative to <paramref name="path"/>, which is prefixed
    /// (an error at <c>/0/name</c> from a check at <c>/items</c> is reported at <c>/items/0/name</c>).
    /// </summary>
    /// <param name="path">JSON Pointer of the checked field.</param>
    /// <param name="check">The check.</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Custom(string path, Func<IEnumerable<FieldError>> check)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(check);
        foreach (var error in check())
        {
            _errors.Add(path.Length == 0 ? error : error with { Path = path + error.Path });
        }

        return this;
    }

    /// <summary>Adds a field error.</summary>
    /// <param name="error">The error (absolute path).</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder Add(FieldError error)
    {
        _errors.Add(error ?? throw new ArgumentNullException(nameof(error)));
        return this;
    }

    /// <summary>Adds field errors.</summary>
    /// <param name="errors">The errors (absolute paths).</param>
    /// <returns>This builder.</returns>
    public ValidationBuilder AddRange(IEnumerable<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        foreach (var error in errors)
        {
            Add(error);
        }

        return this;
    }

    /// <summary>Builds the result.</summary>
    /// <returns>Success, or <c>common.validation.failed</c> with all field errors in the order found.</returns>
    public Result Build()
    {
        if (_errors.Count == 0)
        {
            return Result.Success();
        }

        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            var paths = string.Join(',', _errors.Select(e => e.Path));
            KernelLog.ValidationFailed(_logger, _errors.Count, paths);
        }

        return CommonErrors.ValidationFailed([.. _errors]);
    }
}
