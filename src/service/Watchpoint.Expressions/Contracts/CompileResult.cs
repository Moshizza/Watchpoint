using Watchpoint.Kernel.Values;

namespace Watchpoint.Expressions.Contracts;

/// <summary>Expected return type when compiling an expression (spec EXP §5).</summary>
public enum ExpectedType
{
    /// <summary>Accept any valid result type.</summary>
    Any,

    /// <summary>Condition expressions must return boolean (true or false).</summary>
    Bool,
}

/// <summary>Result of compiling an expression (spec EXP §5).</summary>
/// <param name="Expression">The compiled expression if compilation succeeded; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostics">Diagnostics produced during lexing, parsing, and type-checking.</param>
public sealed record CompileResult(CompiledExpression? Expression, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Gets whether compilation succeeded with no errors.</summary>
    public bool Success => Expression is not null && Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}

/// <summary>Result of evaluating a compiled expression (spec EXP §5).</summary>
/// <param name="Value">The evaluated result value as a <see cref="WpValue"/>.</param>
/// <param name="ErrorCode">The runtime error code if evaluation failed, or <see langword="null"/> on success.</param>
public readonly record struct EvalResult(WpValue Value, string? ErrorCode)
{
    /// <summary>Evaluates as a boolean condition; returns <see langword="true"/> only if successful, boolean, and true.</summary>
    /// <returns><see langword="true"/> if condition met; otherwise <see langword="false"/>.</returns>
    public bool AsCondition() => ErrorCode is null && Value.Kind == WpValueKind.Bool && Value.AsBool();
}
