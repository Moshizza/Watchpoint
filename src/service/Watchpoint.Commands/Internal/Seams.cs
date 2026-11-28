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

/// <summary>The connections as commands see them: existence (CMD §5 step 9), state and package (CMD §7).</summary>
internal interface ICommandConnections
{
    bool Exists(ConnectionId connection);

    /// <summary>The live state (an unknown connection reads as <see cref="ConnectionState.Disabled"/>).</summary>
    ConnectionState State(ConnectionId connection);

    /// <summary>The package of the connection's connector, which qualifies its messages (<c>packages.&lt;id&gt;.&lt;key&gt;</c>).</summary>
    string? PackageOf(ConnectionId connection);

    /// <summary>Raised after a state change of a connection.</summary>
    event EventHandler<ConnectionId>? StateChanged;
}

/// <summary>The connections of the integration runtime.</summary>
internal sealed class RuntimeCommandConnections : ICommandConnections, IDisposable
{
    private readonly IConnectionManager _connections;

    public RuntimeCommandConnections(IConnectionManager connections)
    {
        _connections = connections;
        _connections.StatusChanged += OnStatusChanged;
    }

    public event EventHandler<ConnectionId>? StateChanged;

    public bool Exists(ConnectionId connection) => _connections.Get(connection).IsSuccess;

    public ConnectionState State(ConnectionId connection) => _connections.GetStatus(connection).State;

    public string? PackageOf(ConnectionId connection) =>
        _connections.Get(connection).TryGetValue(out var found, out _) ? found.PackageId.Value : null;

    public void Dispose() => _connections.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, ConnectionStatusChangedEventArgs e) => StateChanged?.Invoke(this, e.Status.Id);
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
