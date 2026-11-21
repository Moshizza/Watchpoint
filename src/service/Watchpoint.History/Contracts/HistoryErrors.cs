using Watchpoint.Kernel.Results;

namespace Watchpoint.History.Contracts;

/// <summary>Error codes and factories of the history module (spec HIS §13).</summary>
public static class HistoryErrors
{
    /// <summary><c>history.query.too_many_points</c>.</summary>
    public const string TooManyPointsCode = "history.query.too_many_points";

    /// <summary><c>history.query.timeout</c>.</summary>
    public const string TimeoutCode = "history.query.timeout";

    /// <summary><c>history.query.range_too_large</c>.</summary>
    public const string RangeTooLargeCode = "history.query.range_too_large";

    /// <summary><c>history.query.invalid</c>.</summary>
    public const string InvalidQueryCode = "history.query.invalid";

    /// <summary>A raw series has more points than allowed: ask for a bucket.</summary>
    /// <param name="max">The most raw points.</param>
    /// <returns>The error.</returns>
    public static Error TooManyPoints(int max) => Error.Create(ErrorKind.Unprocessable, TooManyPointsCode, ("max", max));

    /// <summary>The query ran into the statement timeout.</summary>
    /// <returns>The error.</returns>
    public static Error Timeout() => Error.Create(ErrorKind.Unavailable, TimeoutCode);

    /// <summary>The time range is longer than allowed.</summary>
    /// <param name="maxDays">The most days.</param>
    /// <returns>The error.</returns>
    public static Error RangeTooLarge(int maxDays) => Error.Create(ErrorKind.Validation, RangeTooLargeCode, ("maxDays", maxDays));

    /// <summary>A query field is not allowed (unknown table, group-by column or filter, empty or inverted range).</summary>
    /// <param name="field">The field.</param>
    /// <param name="reason">A short machine-readable reason, e.g. <c>not_whitelisted</c>.</param>
    /// <returns>The error.</returns>
    public static Error InvalidQuery(string field, string reason) => Error.Create(ErrorKind.Validation, InvalidQueryCode, ("field", field), ("reason", reason));
}
