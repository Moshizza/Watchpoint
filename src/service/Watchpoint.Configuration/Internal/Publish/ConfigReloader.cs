using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Publish;

/// <summary>
/// Other nodes (spec CFG §7.2, M9): a <c>config.published</c> record of another node makes each module reload the listed
/// objects from the database. The publishing node already applied them (<c>CompleteApply</c>) and skips its own records.
/// </summary>
internal sealed class ConfigReloader(IConfigObjectTypeRegistry types, IEnumerable<IConfigApplier> appliers, Guid? node)
{
    private readonly Dictionary<string, IConfigApplier> _appliers = appliers.ToDictionary(a => a.Module, StringComparer.Ordinal);

    public async ValueTask OnPublishedAsync(ConfigPublishedBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.NodeId is { } from && from == node)
        {
            return;
        }

        var byModule = body.Changes
            .Select(c => (Module: types.Find(c.Type)?.Module, Ref: new ConfigObjectRef(c.Type, c.Id)))
            .Where(c => c.Module is not null)
            .GroupBy(c => c.Module!, StringComparer.Ordinal);
        foreach (var module in byModule)
        {
            if (_appliers.TryGetValue(module.Key, out var applier))
            {
                await applier.ReloadAsync([.. module.Select(c => c.Ref)], ct).ConfigureAwait(false);
            }
        }
    }
}
