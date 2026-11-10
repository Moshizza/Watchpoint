using Watchpoint.Kernel.Ids;

namespace Watchpoint.Expressions.Contracts;

/// <summary>Represents a compiled, type-checked CEL expression ready for evaluation (spec EXP §5).</summary>
public sealed class CompiledExpression
{
    private readonly Func<IEvaluationContext, EvalResult> _evaluator;
    private readonly string _astJson;

    internal CompiledExpression(
        string source,
        ExprType resultType,
        IReadOnlyList<PropertyPath> referencedProperties,
        IReadOnlyList<string> referencedRefs,
        bool usesNow,
        IReadOnlyList<string> referencedCalendars,
        Func<IEvaluationContext, EvalResult> evaluator,
        string astJson)
    {
        Source = source;
        ResultType = resultType;
        ReferencedProperties = referencedProperties;
        ReferencedRefs = referencedRefs;
        UsesNow = usesNow;
        ReferencedCalendars = referencedCalendars;
        _evaluator = evaluator;
        _astJson = astJson;
    }

    /// <summary>Gets the original expression source text.</summary>
    public string Source { get; }

    /// <summary>Gets the inferred return type of the expression.</summary>
    public ExprType ResultType { get; }

    /// <summary>Gets the device property paths referenced in the expression (for indexing and subscriptions).</summary>
    public IReadOnlyList<PropertyPath> ReferencedProperties { get; }

    /// <summary>Gets the declared reference names (refs.&lt;name&gt;) used in the expression.</summary>
    public IReadOnlyList<string> ReferencedRefs { get; }

    /// <summary>Gets whether the expression depends on the current time (<c>now</c> or time functions).</summary>
    public bool UsesNow { get; }

    /// <summary>Gets the calendar keys referenced in <c>inSchedule("...")</c> calls.</summary>
    public IReadOnlyList<string> ReferencedCalendars { get; }

    /// <summary>Evaluates the expression within the given evaluation context.</summary>
    /// <param name="context">The evaluation context.</param>
    /// <returns>The evaluation result.</returns>
    public EvalResult Evaluate(IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _evaluator(context);
    }

    /// <summary>Returns the canonical AST JSON representation (identical to TypeScript AST JSON).</summary>
    /// <returns>The canonical JSON string.</returns>
    public string ToAstJson() => _astJson;
}
