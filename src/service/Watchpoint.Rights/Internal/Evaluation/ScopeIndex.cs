using System.Runtime.CompilerServices;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>
/// Gives every device an integer index and resolves grant scopes to bitsets of devices (spec RGT §6). An index is an immutable
/// snapshot of the registry; the evaluator builds a new one when the registry changes.
/// </summary>
internal sealed class ScopeIndex
{
    private readonly DeviceId[] _ids;
    private readonly Dictionary<DeviceId, int> _indexOf;
    private readonly LocationId[] _location;
    private readonly ServerId[] _server;
    private readonly Category?[] _category;
    private readonly HashSet<TypeId>[] _types;
    private readonly TagId[][] _tags;
    private readonly Dictionary<LocationId, LocationId?> _parent;
    private readonly Dictionary<LocationId, List<LocationId>> _children;
    private readonly Dictionary<LocationId, List<int>> _devicesAt;
    private readonly SiteId _site;
    private readonly ConditionalWeakTable<GrantScope, Bits> _bits = [];

    public ScopeIndex(IRegistryReader registry, ITypeCatalog? types)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _site = registry.Site.Id;
        _parent = [];
        _children = [];
        foreach (var l in registry.GetLocationTree())
        {
            _parent[l.Id] = l.ParentId;
            if (l.ParentId is { } p)
            {
                if (!_children.TryGetValue(p, out var list))
                {
                    _children[p] = list = [];
                }

                list.Add(l.Id);
            }
        }

        var devices = new List<Device>();
        PageRequest page = new(PageRequest.MaxLimit);
        while (true)
        {
            var result = registry.QueryDevices(new DeviceQuery(), page);
            devices.AddRange(result.Items);
            if (result.NextCursor is null)
            {
                break;
            }

            page = page with { Cursor = result.NextCursor };
        }

        _ids = [.. devices.Select(d => d.Id)];
        _indexOf = new Dictionary<DeviceId, int>(_ids.Length);
        _location = new LocationId[_ids.Length];
        _server = new ServerId[_ids.Length];
        _category = new Category?[_ids.Length];
        _types = new HashSet<TypeId>[_ids.Length];
        _tags = new TagId[_ids.Length][];
        _devicesAt = [];
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            _indexOf[d.Id] = i;
            _location[i] = d.LocationId;
            _server[i] = d.ServerId;
            _tags[i] = [.. d.Tags];
            _types[i] = [d.Type.Id];
            if (types is not null && types.Get(d.Type).TryGetValue(out var def, out _))
            {
                _category[i] = def.Category;
                foreach (var t in def.Lineage)
                {
                    _types[i].Add(t.Id);
                }
            }

            if (!_devicesAt.TryGetValue(d.LocationId, out var at))
            {
                _devicesAt[d.LocationId] = at = [];
            }

            at.Add(i);
        }
    }

    public int Count => _ids.Length;

    public int Bytes => (_ids.Length + 63) / 64 * sizeof(ulong);

    public DeviceId DeviceAt(int index) => _ids[index];

    public bool TryIndexOf(DeviceId id, out int index) => _indexOf.TryGetValue(id, out index);

    public LocationId LocationOf(int index) => _location[index];

    /// <summary>Whether <paramref name="location"/> is <paramref name="ancestor"/> or inside it.</summary>
    /// <param name="location">The location.</param>
    /// <param name="ancestor">The candidate ancestor.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool LocationWithin(LocationId location, LocationId ancestor)
    {
        LocationId? current = location;
        var guard = 0;
        while (current is { } c && guard++ < 64)
        {
            if (c == ancestor)
            {
                return true;
            }

            current = _parent.TryGetValue(c, out var p) ? p : null;
        }

        return false;
    }

    /// <summary>The location and its ancestors.</summary>
    /// <param name="location">The location.</param>
    /// <returns>The chain, nearest first.</returns>
    public IEnumerable<LocationId> AncestorsOrSelf(LocationId location)
    {
        LocationId? current = location;
        var guard = 0;
        while (current is { } c && guard++ < 64)
        {
            yield return c;
            current = _parent.TryGetValue(c, out var p) ? p : null;
        }
    }

    /// <summary>The location and every location below it.</summary>
    /// <param name="root">The root.</param>
    /// <returns>The subtree.</returns>
    public IEnumerable<LocationId> Subtree(LocationId root)
    {
        var stack = new Stack<LocationId>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var l = stack.Pop();
            yield return l;
            if (_children.TryGetValue(l, out var kids))
            {
                foreach (var k in kids)
                {
                    stack.Push(k);
                }
            }
        }
    }

    /// <summary>The devices a scope covers (cached per scope instance).</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The bitset; do not modify.</returns>
    public Bits Resolve(GrantScope scope) => _bits.GetValue(scope, Compute);

    /// <summary>Whether a grant scope covers a location: the whole location or any device inside it.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="location">The location.</param>
    /// <returns><see langword="true"/> when covered.</returns>
    public bool CoversLocation(GrantScope scope, LocationId location)
    {
        if (scope.Everything)
        {
            return true;
        }

        if (OnlyLocations(scope) && scope.LocationsSubtree!.Any(root => LocationWithin(location, root)))
        {
            return true;
        }

        var bits = Resolve(scope);
        foreach (var l in Subtree(location))
        {
            if (_devicesAt.TryGetValue(l, out var at) && at.Any(i => bits[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The locations a scope covers as a whole (subtrees of a location-only scope).</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The locations.</returns>
    public IEnumerable<LocationId> CoveredLocations(GrantScope scope)
    {
        if (!scope.Everything && OnlyLocations(scope))
        {
            return scope.LocationsSubtree!.SelectMany(Subtree);
        }

        return [];
    }

    /// <summary>The locations of the devices of <paramref name="bits"/> and their ancestors.</summary>
    /// <param name="bits">The devices.</param>
    /// <returns>The locations.</returns>
    public HashSet<LocationId> LocationsOf(Bits bits)
    {
        var result = new HashSet<LocationId>();
        foreach (var i in bits.Indexes())
        {
            if (result.Contains(_location[i]))
            {
                continue;
            }

            foreach (var a in AncestorsOrSelf(_location[i]))
            {
                if (!result.Add(a))
                {
                    break;
                }
            }
        }

        return result;
    }

    private static bool OnlyLocations(GrantScope s) =>
        s.LocationsSubtree is not null && s.Servers is null && s.Categories is null && s.Types is null && s.TagsAny is null
        && s.TagsExclude is null && s.Sites is null;

    private Bits Compute(GrantScope scope)
    {
        if (scope.Everything)
        {
            return Bits.Full(_ids.Length);
        }

        var hasFilter = scope.LocationsSubtree is not null || scope.Servers is not null || scope.Categories is not null || scope.Types is not null
            || scope.TagsAny is not null || scope.TagsExclude is not null || scope.Sites is not null;
        var result = new Bits(_ids.Length);
        if (hasFilter)
        {
            HashSet<LocationId>? roots = scope.LocationsSubtree is { } ls ? [.. ls.SelectMany(Subtree)] : null;
            var siteOk = scope.Sites is null || scope.Sites.Contains(_site);
            for (var i = 0; i < _ids.Length; i++)
            {
                result[i] = siteOk
                    && (roots is null || roots.Contains(_location[i]))
                    && (scope.Servers is null || scope.Servers.Contains(_server[i]))
                    && (scope.Categories is null || (_category[i] is { } c && scope.Categories.Contains(c)))
                    && (scope.Types is null || scope.Types.Any(_types[i].Contains))
                    && (scope.TagsAny is null || scope.TagsAny.Any(t => Array.IndexOf(_tags[i], t) >= 0))
                    && (scope.TagsExclude is null || !scope.TagsExclude.Any(t => Array.IndexOf(_tags[i], t) >= 0));
            }
        }

        if (scope.Devices is not null)
        {
            foreach (var d in scope.Devices)
            {
                if (_indexOf.TryGetValue(d, out var index))
                {
                    result[index] = true;
                }
            }
        }

        return result;
    }
}
