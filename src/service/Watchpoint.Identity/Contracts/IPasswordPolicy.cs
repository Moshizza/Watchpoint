using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Password policy verification interface (spec IDN §5).</summary>
public interface IPasswordPolicy
{
    /// <summary>Validates a proposed password against policy rules.</summary>
    /// <param name="password">The proposed plain password.</param>
    /// <param name="login">The user login name.</param>
    /// <param name="displayName">The user display name.</param>
    /// <param name="userId">The user id (when checking against history), or null for new user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success if compliant, or a validation error with field error code.</returns>
    ValueTask<Result> ValidateAsync(string password, string login, string displayName, UserId? userId, CancellationToken ct);
}
