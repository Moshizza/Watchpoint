namespace Watchpoint.Expressions.Contracts;

/// <summary>The severity level of a compiler diagnostic (spec EXP §5).</summary>
public enum DiagnosticSeverity
{
    /// <summary>A diagnostic warning that does not prevent evaluation but highlights potential issues.</summary>
    Warning,

    /// <summary>A diagnostic error that prevents successful expression compilation.</summary>
    Error,
}

/// <summary>Represents a diagnostic emitted by the expression compiler (spec EXP §5, §6).</summary>
/// <param name="Severity">The severity level (Error or Warning).</param>
/// <param name="Code">The error or warning code string (e.g. <c>expr.syntax.unexpected_token</c>).</param>
/// <param name="Args">Format arguments for the diagnostic message.</param>
/// <param name="Start">The UTF-16 character start offset in the source string.</param>
/// <param name="Length">The UTF-16 character length of the affected span.</param>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    IReadOnlyDictionary<string, object?> Args,
    int Start,
    int Length);
