namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Publishes registry mutations to external consumers or message bus (e.g. PIP).
/// </summary>
public interface IRegistryChangePublisher
{
    /// <summary>
    /// Publishes an applied change batch.
    /// </summary>
    /// <param name="batch">The batch that was applied.</param>
    /// <param name="context">The change context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default);
}
