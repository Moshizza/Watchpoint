using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// The single <c>Changed</c> event raised by the writer after the snapshot swap (REG §6 step 5) and exposed on both
/// <see cref="IRegistryReader"/> and <see cref="IRegistryWriter"/> (REG §5, §6): one event, two contracts, so a
/// read-only consumer (e.g. PIP's enrichment cache) can subscribe without depending on the writer.
/// </summary>
internal sealed class RegistryChangeFeed
{
    /// <summary>Raised after a change batch has been committed and the in-memory snapshot updated.</summary>
    public event EventHandler<RegistryChangedEventArgs>? Changed;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    public void Raise(object sender, RegistryChangedEventArgs args) => Changed?.Invoke(sender, args);
}
