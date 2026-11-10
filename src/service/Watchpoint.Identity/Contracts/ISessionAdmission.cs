using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Phase of session admission evaluation (spec IDN §12).</summary>
public enum AdmissionPhase
{
    /// <summary>Admission during primary sign-in.</summary>
    SignIn,

    /// <summary>Admission during token refresh.</summary>
    Refresh,
}

/// <summary>Admission hook for controlling session creation and refresh (e.g. MNT maintenance, LIC seat limits).</summary>
public interface ISessionAdmission
{
    /// <summary>Gets the evaluation order (ascending).</summary>
    int Order { get; }

    /// <summary>Evaluates whether the session may be admitted.</summary>
    /// <param name="user">The user attempting sign-in or refresh.</param>
    /// <param name="client">The client context.</param>
    /// <param name="phase">Admission phase (SignIn or Refresh).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success to allow, or an error to refuse.</returns>
    ValueTask<Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase, CancellationToken ct);
}
