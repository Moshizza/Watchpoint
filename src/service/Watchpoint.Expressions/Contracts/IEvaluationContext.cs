using Watchpoint.Kernel.Time;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Expressions.Contracts;

/// <summary>Represents an environment object whose members can be accessed or inspected dynamically (spec EXP §4.5, §5).</summary>
public interface IExprObject
{
    /// <summary>Attempts to read a property or member by name.</summary>
    /// <param name="name">The member name.</param>
    /// <param name="value">The resulting value if found.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    bool TryGetMember(string name, out WpValue value);

    /// <summary>Checks whether a member exists and (for properties) a value is known (spec EXP §4.4).</summary>
    /// <param name="name">The member name.</param>
    /// <returns><see langword="true"/> if present and non-null; otherwise <see langword="false"/>.</returns>
    bool HasMember(string name);

    /// <summary>Calls a method on this object (e.g. quality, changedAgo, isA).</summary>
    /// <param name="name">The method name.</param>
    /// <param name="arguments">The arguments provided in the expression.</param>
    /// <param name="result">The resulting return value.</param>
    /// <returns><see langword="true"/> if the method was invoked; otherwise <see langword="false"/>.</returns>
    bool TryCallMethod(string name, IReadOnlyList<WpValue> arguments, out WpValue result);
}

/// <summary>Evaluation context providing runtime variables, calendars, and time (spec EXP §5).</summary>
public interface IEvaluationContext
{
    /// <summary>Gets the evaluation time (UTC instant).</summary>
    DateTimeOffset Now { get; }

    /// <summary>Gets the site time zone for time functions.</summary>
    SiteTimeZone SiteTimeZone { get; }

    /// <summary>Attempts to retrieve an environment variable or object by name.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The resulting object.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    bool TryGetVariable(string name, out IExprObject value);

    /// <summary>Checks whether the given calendar schedule is active at the specified instant.</summary>
    /// <param name="calendarKey">The calendar key.</param>
    /// <param name="at">The instant to test.</param>
    /// <returns><see langword="true"/> if active; otherwise <see langword="false"/>.</returns>
    bool InSchedule(string calendarKey, DateTimeOffset at);
}
