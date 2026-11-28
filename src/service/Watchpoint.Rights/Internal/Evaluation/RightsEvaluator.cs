using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Rights.Contracts;
using Watchpoint.Scheduling;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>
/// The rights evaluator (spec RGT §6): permission × scope × schedule × constraints, answered from per-(user, permission)
/// cache entries that stay valid until the next schedule transition and are rebuilt when grants, registry, calendars,
/// users or post-filters change. <c>rights.changed</c> is published only for users whose effective rights differ.
/// </summary>
internal sealed class RightsEvaluator : IRightsEvaluator, IDisposable
{
    private readonly IGrantStore _grants;
    private readonly IRightsUserLookup _users;
    private readonly IPermissionCatalogue _catalogue;
    private readonly IRegistryReader _registry;
    private readonly ITypeCatalog? _types;
    private readonly ICalendarService _calendars;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IVisibleScopePostFilter> _filters;
    private readonly IRightsChangedSink? _sink;
    private readonly ILogger<RightsEvaluator> _logger;
    private readonly ConcurrentDictionary<(UserId User, string Code), Entry> _entries = new();
    private readonly object _gate = new();
    private ScopeIndex? _index;
    private long _version;

    public RightsEvaluator(IGrantStore grants, IRightsUserLookup users, IPermissionCatalogue catalogue, IRegistryReader registry, ITypeCatalog? types,
        ICalendarService calendars, TimeProvider time, IEnumerable<IVisibleScopePostFilter> filters, IRightsChangedSink? sink, ILogger<RightsEvaluator> logger)
    {
        _grants = grants;
        _users = users;
        _catalogue = catalogue;
        _registry = registry;
        _types = types;
        _calendars = calendars;
        _time = time;
        _filters = [.. filters];
        _sink = sink;
        _logger = logger;
        _grants.Changed += OnGrantsChanged;
        _registry.Changed += OnRegistryChanged;
        _calendars.Changed += OnCalendarChanged;
        _users.UserChanged += OnUserChanged;
        foreach (var f in _filters)
        {
            f.Changed += OnFilterChanged;
        }
    }

    /// <summary>Gets the number of cached (user, permission) entries.</summary>
    public int CachedEntries => _entries.Count;

    /// <summary>Gets the bytes used by the cached device bitsets.</summary>
    public long CachedBitsetBytes => _entries.Values.Sum(e => e.Scope?.Bits?.Bytes ?? 0L);

    public void Dispose()
    {
        _grants.Changed -= OnGrantsChanged;
        _registry.Changed -= OnRegistryChanged;
        _calendars.Changed -= OnCalendarChanged;
        _users.UserChanged -= OnUserChanged;
        foreach (var f in _filters)
        {
            f.Changed -= OnFilterChanged;
        }
    }

    public bool HasPermission(UserId user, PermissionCode permission, DateTimeOffset now)
    {
        var u = _users.Find(user);
        if (u is null || !u.CanHoldRights(now))
        {
            return false;
        }

        if (u.Administrator)
        {
            return true;
        }

        var entry = GetEntry(user, permission, now);
        for (var i = 0; i < entry.Grants.Length; i++)
        {
            if (entry.Active[i])
            {
                return true;
            }
        }

        return false;
    }

    public AuthorizationDecision Authorize(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now, ClientKind? client = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var u = _users.Find(user);
            if (u is null || !u.CanHoldRights(now))
            {
                return Deny(RightsDenial.Forbidden);
            }

            if (u.Administrator)
            {
                return new AuthorizationDecision(true, GrantConstraints.None, null);
            }

            if (target is AuthorizationTarget.Device excludedCheck && IsExcluded(excludedCheck.Id))
            {
                return Deny(RightsDenial.Forbidden);
            }

            var entry = GetEntry(user, permission, now);
            var systemKind = target is AuthorizationTarget.System && _catalogue.Get(permission).TryGetValue(out var def, out _) && def.ScopeKind == PermissionScopeKind.System;
            var outside = false;
            GrantConstraints? combined = null;
            for (var i = 0; i < entry.Grants.Length; i++)
            {
                var g = entry.Grants[i];
                if (!systemKind && !Covers(g, target))
                {
                    continue;
                }

                if (!entry.Active[i])
                {
                    outside = true;
                    continue;
                }

                combined = combined is null ? g.Constraints : Combine(combined, g.Constraints);
            }

            if (combined is null)
            {
                return Deny(outside ? RightsDenial.OutsideSchedule : RightsDenial.Forbidden);
            }

            if (client is { } kind && combined.ClientKinds is { } allowed && !allowed.Contains(kind))
            {
                return new AuthorizationDecision(false, combined, RightsErrors.ClientKindCode);
            }

            return new AuthorizationDecision(true, combined, null);
        }
        finally
        {
            if (RightsMetrics.AuthorizeDuration.Enabled)
            {
                RightsMetrics.AuthorizeDuration.Record(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
        }
    }

    public IVisibleScope GetVisibleScope(UserId user, PermissionCode permission, DateTimeOffset now)
    {
        var u = _users.Find(user);
        var index = Index();
        if (u is null || !u.CanHoldRights(now))
        {
            return VisibleScope.Nothing(index, DateTimeOffset.MaxValue);
        }

        if (u.Administrator)
        {
            return VisibleScope.All(u.ValidUntil ?? DateTimeOffset.MaxValue);
        }

        var entry = GetEntry(user, permission, now);
        return entry.Scope!;
    }

    public IReadOnlyList<GrantExplanation> Explain(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        var result = new List<GrantExplanation>();
        foreach (var g in _grants.GetGrants(user))
        {
            if (!_catalogue.Matches(g.Permission, permission))
            {
                result.Add(new GrantExplanation(g, false, "permission"));
            }
            else if (!Covers(g, target))
            {
                result.Add(new GrantExplanation(g, false, "scope"));
            }
            else if (g.ScheduleCalendar is { } cal && !_calendars.IsActive(cal, now))
            {
                result.Add(new GrantExplanation(g, false, "schedule"));
            }
            else
            {
                result.Add(new GrantExplanation(g, true, "matched"));
            }
        }

        return result;
    }

    private static AuthorizationDecision Deny(string code) => new(false, GrantConstraints.None, code);

    /// <summary>Least restrictive combination over matching grants (spec RGT §6 step 5).</summary>
    private static GrantConstraints Combine(GrantConstraints a, GrantConstraints b) => new(
        a.RequireReason && b.RequireReason,
        a.RequireFourEyes && b.RequireFourEyes,
        a.RequireStepUp && b.RequireStepUp,
        a.MaxDuration is { } da && b.MaxDuration is { } db ? (da > db ? da : db) : null,
        a.Priorities is { } pa && b.Priorities is { } pb ? new PriorityRange(Math.Min(pa.Min, pb.Min), Math.Max(pa.Max, pb.Max)) : null,
        a.ClientKinds is { } ka && b.ClientKinds is { } kb ? new HashSet<ClientKind>(ka.Union(kb)) : null,
        a.MaxLookback is { } la && b.MaxLookback is { } lb ? (la > lb ? la : lb) : null);

    private ScopeIndex Index()
    {
        var index = _index;
        if (index is not null)
        {
            return index;
        }

        lock (_gate)
        {
            return _index ??= new ScopeIndex(_registry, _types);
        }
    }

    private bool IsExcluded(DeviceId id)
    {
        foreach (var f in _filters)
        {
            if (f.Excludes(id))
            {
                return true;
            }
        }

        return false;
    }

    private bool Covers(RightGrant g, AuthorizationTarget target)
    {
        var scope = g.Scope;
        if (scope.Everything)
        {
            return true;
        }

        switch (target)
        {
            case AuthorizationTarget.Device d:
                if (scope.Devices?.Contains(d.Id) == true)
                {
                    return true;
                }

                var index = Index();
                return index.TryIndexOf(d.Id, out var i) && index.Resolve(scope)[i];
            case AuthorizationTarget.Location l:
                return Index().CoversLocation(scope, l.Id);
            default:
                return false;
        }
    }

    private Entry GetEntry(UserId user, PermissionCode permission, DateTimeOffset now)
    {
        var key = (user, permission.Value);
        if (_entries.TryGetValue(key, out var cached) && cached.ComputedAt <= now && now < cached.ValidUntil)
        {
            RightsMetrics.CacheHits.Add(1);
            return cached;
        }

        var entry = BuildEntry(user, permission, now);
        _entries[key] = entry;
        return entry;
    }

    private Entry BuildEntry(UserId user, PermissionCode permission, DateTimeOffset now)
    {
        RightsMetrics.ScopeRecompute.Add(1);
        var u = _users.Find(user);
        if (u is null || !u.CanHoldRights(now))
        {
            return new Entry([], [], now, u?.ValidFrom is { } from && from > now ? from : DateTimeOffset.MaxValue, "none");
        }

        if (u.Administrator)
        {
            return new Entry([], [], now, u.ValidUntil ?? DateTimeOffset.MaxValue, "admin");
        }

        var grants = _grants.GetGrants(user).Where(g => _catalogue.Matches(g.Permission, permission)).ToArray();
        var active = new bool[grants.Length];
        var until = DateTimeOffset.MaxValue;
        for (var i = 0; i < grants.Length; i++)
        {
            if (grants[i].ScheduleCalendar is not { } cal)
            {
                active[i] = true;
                continue;
            }

            active[i] = _calendars.IsActive(cal, now);
            if (_calendars.NextTransition(cal, now) is { } next && next < until)
            {
                until = next;
            }
        }

        if (u.ValidUntil is { } expires && expires > now && expires < until)
        {
            until = expires;
        }

        var entry = new Entry(grants, active, now, until, string.Empty);
        var scope = BuildScope(entry);
        entry.Scope = scope;
        var constraints = string.Join('~', grants.Where((_, i) => active[i]).Select(GrantIdentity.Canonical).Order(StringComparer.Ordinal));
        entry.Fingerprint = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(scope.Everything ? "all" : scope.Bits!.Hash().ToString("x", System.Globalization.CultureInfo.InvariantCulture))}:{scope.Locations?.Count}:{constraints}");
        return entry;
    }

    private VisibleScope BuildScope(Entry entry)
    {
        var index = Index();
        var until = entry.ValidUntil;
        var everything = false;
        var bits = new Bits(index.Count);
        var locations = new HashSet<LocationId>();
        for (var i = 0; i < entry.Grants.Length; i++)
        {
            if (!entry.Active[i])
            {
                continue;
            }

            var scope = entry.Grants[i].Scope;
            if (scope.Everything)
            {
                everything = true;
                break;
            }

            bits.Or(index.Resolve(scope));
            locations.UnionWith(index.CoveredLocations(scope));
        }

        if (everything && _filters.Count == 0)
        {
            return VisibleScope.All(until);
        }

        if (everything)
        {
            bits = Bits.Full(index.Count);
        }

        foreach (var f in _filters)
        {
            foreach (var i in bits.Indexes().ToArray())
            {
                if (f.Excludes(index.DeviceAt(i)))
                {
                    bits[i] = false;
                }
            }
        }

        locations.UnionWith(index.LocationsOf(bits));
        return new VisibleScope(index, bits, locations, until);
    }

    private void Refresh(Func<(UserId User, string Code), bool> affected)
    {
        var now = _time.GetUtcNow();
        var changed = new HashSet<UserId>();
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            foreach (var key in _entries.Keys.Where(affected).ToArray())
            {
                if (!_entries.TryRemove(key, out var old))
                {
                    continue;
                }

                var fresh = BuildEntry(key.User, PermissionCode.FromTrusted(key.Code), now);
                _entries[key] = fresh;
                if (!string.Equals(old.Fingerprint, fresh.Fingerprint, StringComparison.Ordinal))
                {
                    changed.Add(key.User);
                }
            }
        }

        if (changed.Count > 0)
        {
            RightsLog.RightsChanged(_logger, changed.Count, _version);
            _sink?.RightsChanged(changed);
        }
    }

    private void OnGrantsChanged(object? sender, GrantsChangedEventArgs e)
    {
        var users = e.Users.ToHashSet();
        Refresh(k => users.Contains(k.User));
        // a user with no cached entry may still have gained rights: publish when it has grants or lost them
    }

    private void OnRegistryChanged(object? sender, RegistryChangedEventArgs e)
    {
        lock (_gate)
        {
            _index = null;
        }

        Refresh(_ => true);
    }

    private void OnCalendarChanged(object? sender, CalendarChangedEventArgs e) => Refresh(_ => true);

    private void OnUserChanged(object? sender, UserId user) => Refresh(k => k.User == user);

    private void OnFilterChanged(object? sender, EventArgs e) => Refresh(_ => true);

    private sealed class Entry(RightGrant[] grants, bool[] active, DateTimeOffset computedAt, DateTimeOffset validUntil, string fingerprint)
    {
        public RightGrant[] Grants { get; } = grants;

        public bool[] Active { get; } = active;

        public DateTimeOffset ComputedAt { get; } = computedAt;

        public DateTimeOffset ValidUntil { get; } = validUntil;

        public string Fingerprint { get; set; } = fingerprint;

        public VisibleScope? Scope { get; set; }

    }

    private static class RightsDenial
    {
        public const string Forbidden = "common.forbidden";
        public const string OutsideSchedule = RightsErrors.OutsideScheduleCode;
    }
}
