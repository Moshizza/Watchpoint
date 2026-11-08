namespace Watchpoint.Hosting;

/// <summary>
/// A task executed sequentially within a startup phase during node startup.
/// </summary>
public interface IStartupTask
{
    /// <summary>Gets the phase in which this task executes.</summary>
    StartupPhase Phase { get; }

    /// <summary>Gets the order within the phase (ascending).</summary>
    int Order => 0;

    /// <summary>Gets the descriptive name of the startup task.</summary>
    string Name { get; }

    /// <summary>Executes the task asynchronously. Throwing stops the node with exit code 70.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ExecuteAsync(CancellationToken ct);
}
