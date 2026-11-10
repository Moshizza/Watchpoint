namespace Watchpoint.Expressions.Contracts;

/// <summary>Standard error and diagnostic codes for expressions (spec EXP §5, §6).</summary>
public static class ExpressionErrors
{
    // Syntax / Lexical diagnostics
    /// <summary>Source text exceeds the 2,000 character limit.</summary>
    public const string TooLong = "expr.too_long";

    /// <summary>AST node count exceeds 500 nodes.</summary>
    public const string TooComplex = "expr.too_complex";

    /// <summary>Nesting depth exceeds 32 levels.</summary>
    public const string TooDeep = "expr.too_deep";

    /// <summary>List literal or list value exceeds 1,000 items.</summary>
    public const string ListTooLarge = "expr.list.too_large";

    /// <summary>Unexpected token during parsing.</summary>
    public const string UnexpectedToken = "expr.syntax.unexpected_token";

    /// <summary>String literal is not closed with a matching quote.</summary>
    public const string UnterminatedString = "expr.syntax.unterminated_string";

    /// <summary>Comparisons cannot be chained (e.g. <c>a &lt; b &lt; c</c>).</summary>
    public const string ChainedComparison = "expr.syntax.chained_comparison";

    // Semantic / Type checking diagnostics
    /// <summary>Accessed member is unknown on the target object.</summary>
    public const string UnknownMember = "expr.member.unknown";

    /// <summary>Member is not common to all device types in the rule and is not guarded by <c>has(...)</c>.</summary>
    public const string MemberNotCommon = "expr.member.not_common";

    /// <summary>Type mismatch in operator or comparison.</summary>
    public const string TypeMismatch = "expr.type.mismatch";

    /// <summary>Specified string value is not a valid enum code for this enum property.</summary>
    public const string UnknownEnumValue = "expr.enum.unknown_value";

    /// <summary>Function name is unknown.</summary>
    public const string UnknownFunction = "expr.function.unknown";

    /// <summary>Invalid argument count or types passed to function.</summary>
    public const string FunctionArguments = "expr.function.arguments";

    /// <summary>Referenced calendar key does not exist.</summary>
    public const string UnknownCalendar = "expr.calendar.unknown";

    /// <summary>Expression result does not evaluate to boolean for a condition.</summary>
    public const string ResultNotBool = "expr.result.not_bool";

    /// <summary>Warning emitted when a potentially null property is compared with an ordering operator.</summary>
    public const string NullComparisonWarning = "expr.null.comparison";

    /// <summary>Regex pattern uses features outside the supported portable subset.</summary>
    public const string UnsupportedRegex = "expr.regex.unsupported";

    // Runtime evaluation errors
    /// <summary>Division or modulo by zero.</summary>
    public const string EvalDivisionByZero = "expr.eval.division_by_zero";

    /// <summary>Maximum step count (10,000) exceeded during evaluation.</summary>
    public const string EvalLimitExceeded = "expr.eval.limit_exceeded";

    /// <summary>Regex match timed out (10 ms limit).</summary>
    public const string EvalRegexTimeout = "expr.eval.regex_timeout";

    /// <summary>Index out of range on list access.</summary>
    public const string EvalIndexOutOfRange = "expr.eval.index_out_of_range";

    /// <summary>General runtime evaluation error.</summary>
    public const string EvalError = "expr.eval.error";
}
