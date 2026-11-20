using Watchpoint.Kernel.Results;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Transactional write interface for mutating registry configuration (REG §6).
/// </summary>
public interface IRegistryWriter
{
    /// <summary>
    /// Applies an atomic batch of mutations.
    /// </summary>
    /// <param name="batch">The batch of changes.</param>
    /// <param name="context">Change context metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or validation/conflict error.</returns>
    Task<Result> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default);

    /// <summary>
    /// Event fired after a change batch has been committed and in-memory snapshot updated (in-process, after the
    /// snapshot swap). The same event is also exposed on <see cref="IRegistryReader"/>.
    /// </summary>
    event EventHandler<RegistryChangedEventArgs>? Changed;
}
