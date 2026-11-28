using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>
/// Scope containment (spec RGT §10, tests RGT-U-035…050): whether a granted scope lies within the union of an actor's scopes.
/// Used by the escalation guard and the rights copy. <see cref="GrantScope"/> lists are ANDed, explicit devices are OR'ed.
/// </summary>
internal static class ScopeContainment
{
    /// <summary>Whether <paramref name="granted"/> is within the union of <paramref name="actor"/>.</summary>
    /// <param name="actor">The actor's matching grant scopes.</param>
    /// <param name="granted">The scope to grant.</param>
    /// <param name="deviceInScope">Whether a device belongs to an actor scope (registry resolution); <see langword="null"/> = explicit device lists only.</param>
    /// <returns><see langword="true"/> when contained.</returns>
    public static bool Contains(IReadOnlyList<GrantScope> actor, GrantScope granted, Func<GrantScope, DeviceId, bool>? deviceInScope = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(granted);
        if (actor.Any(a => a.Everything))
        {
            return true;
        }

        if (granted.Everything)
        {
            return false;
        }

        // explicit devices: each must be inside some actor scope
        if (granted.Devices is { } devices && !devices.All(d => actor.Any(a => DeviceIn(a, d, deviceInScope))))
        {
            return false;
        }

        // the filter part: split by location so a union of actor scopes can cover it piecewise
        var filter = granted with { Devices = null };
        if (IsEmptyFilter(filter))
        {
            return true;
        }

        var pieces = filter.LocationsSubtree is { Count: > 1 } locations
            ? locations.Select(l => filter with { LocationsSubtree = [l] })
            : [filter];
        return pieces.All(p => actor.Any(a => FilterWithin(a with { Devices = null }, p)));
    }

    /// <summary>Whether every object of <paramref name="inner"/> is in <paramref name="outer"/> (filter parts only).</summary>
    /// <param name="outer">The wider scope.</param>
    /// <param name="inner">The narrower scope.</param>
    /// <param name="locationWithin">Whether the first location is the second or inside it; <see langword="null"/> = equality.</param>
    /// <returns><see langword="true"/> when contained.</returns>
    public static bool FilterWithin(GrantScope outer, GrantScope inner, Func<LocationId, LocationId, bool>? locationWithin = null)
    {
        if (outer.Everything)
        {
            return true;
        }

        if (inner.Everything)
        {
            return false;
        }

        var within = locationWithin ?? ((a, b) => a == b);
        return (outer.LocationsSubtree is null || (inner.LocationsSubtree is not null && inner.LocationsSubtree.All(l => outer.LocationsSubtree.Any(o => within(l, o)))))
            && Subset(outer.Servers, inner.Servers)
            && Subset(outer.Categories, inner.Categories)
            && Subset(outer.Types, inner.Types)
            && Subset(outer.TagsAny, inner.TagsAny)
            && Subset(outer.Sites, inner.Sites)
            && (outer.TagsExclude is null || (inner.TagsExclude is not null && outer.TagsExclude.All(inner.TagsExclude.Contains)));
    }

    private static bool Subset<T>(IReadOnlyList<T>? outer, IReadOnlyList<T>? inner) =>
        outer is null || (inner is not null && inner.All(outer.Contains));

    private static bool DeviceIn(GrantScope scope, DeviceId device, Func<GrantScope, DeviceId, bool>? resolver) =>
        scope.Everything || scope.Devices?.Contains(device) == true || (resolver?.Invoke(scope, device) ?? false);

    private static bool IsEmptyFilter(GrantScope s) =>
        s.LocationsSubtree is null && s.Servers is null && s.Categories is null && s.Types is null && s.TagsAny is null && s.TagsExclude is null && s.Sites is null;
}
