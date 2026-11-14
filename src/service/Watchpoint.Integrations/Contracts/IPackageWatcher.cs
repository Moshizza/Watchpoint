namespace Watchpoint.Integrations.Contracts;

/// <summary>Discovery of packages in the builtin and user folders (spec IRT §4.1).</summary>
public interface IPackageWatcher
{
    /// <summary>
    /// Scans both folders (start-up phase <c>Catalogue</c>, order 10), then watches the user folder: changes are
    /// processed once the folder has been quiet for the debounce time, and a periodic rescan is the safety net.
    /// </summary>
    /// <param name="ct">Cancels the initial scan.</param>
    /// <returns>A task completing when the initial scan is done.</returns>
    Task StartAsync(CancellationToken ct);
}
