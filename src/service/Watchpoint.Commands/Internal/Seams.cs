using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Watchpoint.Identity.Contracts;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Commands.Internal;

/// <summary>Checks a step-up proof against IDN (bound to the session, unexpired).</summary>
internal interface IStepUpValidator
{
    Result Validate(string stepUpToken, SessionId session);
}

/// <summary>IDN's check (<see cref="IAuthenticationService.ValidateStepUp"/>).</summary>
internal sealed class IdentityStepUpValidator(IAuthenticationService authentication) : IStepUpValidator
{
    public Result Validate(string stepUpToken, SessionId session) => authentication.ValidateStepUp(stepUpToken, session);
}

/// <summary>Whether a connection exists (CMD §5 step 9; its state is checked at dispatch).</summary>
internal interface ICommandConnections
{
    bool Exists(ConnectionId connection);
}

/// <summary>The connections of the integration runtime.</summary>
internal sealed class RuntimeCommandConnections(IConnectionManager connections) : ICommandConnections
{
    public bool Exists(ConnectionId connection) => connections.Get(connection).IsSuccess;
}

/// <summary>
/// Step-up proofs for commands (CMD §5 step 8): valid for IDN and used once. A proof that already authorised a command
/// is refused (<c>identity.stepup.invalid</c>); used proofs are remembered for a day, longer than any proof lives.
/// </summary>
internal sealed class StepUpGuard(IStepUpValidator validator)
{
    private static readonly TimeSpan _remember = TimeSpan.FromDays(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public Result Consume(string? token, SessionId session, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token))
        {
            return IdentityErrors.StepUpRequired();
        }

        var valid = validator.Validate(token, session);
        if (!valid.IsSuccess)
        {
            return valid;
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (!_used.TryAdd(key, now))
        {
            return IdentityErrors.StepUpInvalid();
        }

        foreach (var (used, at) in _used)
        {
            if (at + _remember <= now)
            {
                _used.TryRemove(used, out _);
            }
        }

        return Result.Success();
    }
}
