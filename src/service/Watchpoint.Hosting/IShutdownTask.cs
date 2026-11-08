namespace Watchpoint.Hosting;

/// <summary>
/// A task executed sequentially in descending phase order during node shutdown.
/// </summary>
public interface IShutdownTask
{
    /// <summary>Gets the phase in which this shutdown task executes (executed in descending phase order).</summary>
    StartupPhase Phase { get; }

    /// <summary>Gets the descriptive name of the shutdown task.</summary>
    string Name { get; }

    /// <summary>Executes the shutdown task asynchronously within the shutdown timeout budget.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ExecuteAsync(CancellationToken ct);
}
