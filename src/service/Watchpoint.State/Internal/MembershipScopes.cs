using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Computes the aggregate scopes an entity belongs to (spec STA §6.4): its location and ancestors, its hosting
/// server and ancestor servers, its tags, and the site. Entities remember this set to move or decrement/increment
/// counters without recomputation on every overall-state change.
/// </summary>
internal static class MembershipScopes
{
    /// <summary>Computes the scopes of an entity from the current registry snapshot.</summary>
    /// <param name="reader">The registry reader.</param>
    /// <param name="info">The entity.</param>
    /// <returns>The scopes (location chain, server chain, tags, site).</returns>
    public static IReadOnlyList<AggregateScope> Compute(IRegistryReader reader, EntityInfo info)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var scopes = new List<AggregateScope>();
        scopes.Add(new AggregateScope(AggregateScopeKind.Location, info.LocationId.Value));
        foreach (var ancestor in reader.GetAncestors(info.LocationId))
        {
            scopes.Add(new AggregateScope(AggregateScopeKind.Location, ancestor.Id.Value));
        }

        foreach (var serverId in ServerChain(reader, info.ServerId))
        {
            scopes.Add(new AggregateScope(AggregateScopeKind.Server, serverId.Value));
        }

        foreach (var tag in info.Tags)
        {
            scopes.Add(new AggregateScope(AggregateScopeKind.Tag, tag.Value));
        }

        scopes.Add(new AggregateScope(AggregateScopeKind.Site, reader.Site.Id.Value));
        return scopes;
    }

    /// <summary>Returns a server and its ancestors, closest first; guards against a cycle in the data.</summary>
    public static IReadOnlyList<ServerId> ServerChain(IRegistryReader reader, ServerId start)
    {
        var chain = new List<ServerId>();
        var seen = new HashSet<Guid>();
        var current = start;
        while (seen.Add(current.Value))
        {
            chain.Add(current);
            if (reader.GetServer(current).TryGetValue(out var server, out _) && server.ParentId is { } parent)
            {
                current = parent;
            }
            else
            {
                break;
            }
        }

        return chain;
    }
}
