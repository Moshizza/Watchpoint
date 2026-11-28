using Watchpoint.Kernel.Results;

namespace Watchpoint.Audit.Contracts;

/// <summary>Error codes and factories of the audit module (spec AUD §11).</summary>
public static class AuditErrors
{
    /// <summary><c>audit.query.range_too_large</c>.</summary>
    public const string RangeTooLargeCode = "audit.query.range_too_large";

    /// <summary><c>audit.entry.not_found</c>.</summary>
    public const string EntryNotFoundCode = "audit.entry.not_found";

    /// <summary><c>audit.verification.chain_not_found</c>.</summary>
    public const string ChainNotFoundCode = "audit.verification.chain_not_found";

    /// <summary>The query range is longer than 366 days (or empty or inverted).</summary>
    /// <param name="maxDays">The most days.</param>
    /// <returns>The error.</returns>
    public static Error RangeTooLarge(int maxDays) => Error.Create(ErrorKind.Validation, RangeTooLargeCode, ("maxDays", maxDays));

    /// <summary>The entry does not exist.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The error.</returns>
    public static Error EntryNotFound(long id) => Error.Create(ErrorKind.NotFound, EntryNotFoundCode, ("id", id));

    /// <summary>The chain does not exist.</summary>
    /// <param name="chainId">The chain.</param>
    /// <returns>The error.</returns>
    public static Error ChainNotFound(string chainId) => Error.Create(ErrorKind.NotFound, ChainNotFoundCode, ("chainId", chainId));
}
