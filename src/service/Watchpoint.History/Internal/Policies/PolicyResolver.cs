using System.Text.Json;
using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Policies;

/// <summary>Finds the history policy of a property: override of the device, else override of the type, else the type definition (spec HIS §6).</summary>
internal sealed class PolicyResolver(ITypeCatalog catalog, IHistoryStore store, HistorySettings settings, TimeProvider time)
{
    private Dictionary<(string Scope, string Key, string Path), PropertyHistoryPolicy> _overrides = [];

    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>Reloads the overrides when they are older than <see cref="HistorySettings.PolicyOverrideRefresh"/>.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now - _loadedAt < settings.PolicyOverrideRefresh)
        {
            return;
        }

        var rows = await store.GetPolicyOverridesAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<(string, string, string), PropertyHistoryPolicy>();
        foreach (var row in rows)
        {
            try
            {
                var document = JsonSerializer.Deserialize(row.PolicyJson, HistoryJson.Info<HistoryPolicyDocument>());
                if (document is not null)
                {
                    map[(row.Scope, row.Key, row.Path)] = PropertyHistoryPolicy.From(document.Mode, document.Type, document.Threshold, document.Interval);
                }
            }
            catch (JsonException)
            {
                // A malformed override is ignored: the definition applies.
            }
        }

        _overrides = map;
        _loadedAt = now;
    }

    /// <summary>Resolves the policy. Unknown types and properties record every change: nothing is lost for want of a definition.</summary>
    /// <param name="device">The device.</param>
    /// <param name="type">The type of the device.</param>
    /// <param name="path">The property path.</param>
    /// <returns>The policy.</returns>
    public PropertyHistoryPolicy Resolve(DeviceId device, TypeRef type, string path)
    {
        if (_overrides.TryGetValue(("device", device.ToString(), path), out var byDevice))
        {
            return byDevice;
        }

        if (_overrides.TryGetValue(("type", type.Id.Value, path), out var byType))
        {
            return byType;
        }

        if (catalog.Get(type).TryGetValue(out var definition, out _)
            && PropertyPath.Parse(path).TryGetValue(out var propertyPath, out _)
            && definition.Properties.TryGetValue(propertyPath, out var property))
        {
            var policy = property.History;
            return PropertyHistoryPolicy.From(policy.Mode, policy.Type, policy.Threshold, policy.Interval);
        }

        return PropertyHistoryPolicy.EveryChange;
    }
}
