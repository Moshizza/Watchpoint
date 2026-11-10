namespace Watchpoint.Identity.Contracts;

/// <summary>Service for creating the initial bootstrap administrator account (spec IDN §7.1).</summary>
public interface IBootstrapAdminService
{
    /// <summary>Checks and creates the bootstrap administrator if none exists.</summary>
    /// <param name="isProduction">Whether the node is running in Production mode.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteAsync(bool isProduction, CancellationToken ct);
}
