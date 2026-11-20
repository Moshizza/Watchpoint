using Watchpoint.Hosting;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Host.Dev;

/// <summary>
/// Seed section <c>site</c> (HST §9): names the site and sets its time zone through <see cref="IRegistryWriter"/>. The
/// registry needs exactly one root location in every batch it accepts (REG R2): on an empty registry the seed's locations
/// are created in the same batch (the <c>locations</c> step then finds them).
/// </summary>
public sealed class SiteDevSeedStep(IRegistryReader reader, IRegistryWriter writer, NodeSettings settings) : IDevSeedStep
{
    public string Section => "site";

    /// <summary>The site always exists (the default site, REG R1): it never counts as data.</summary>
    public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct) => Task.FromResult(false);

    public async Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            return Error.Forbidden(DevSeedReader.NotDevelopmentCode);
        }

        var site = reader.Site;
        if (site.Name.Default == seed.Site.Name && site.DefaultTimezone == seed.Site.TimeZone)
        {
            return Result.Success();
        }

        List<RegistryChange> changes = [new RegistryChange.UpdateSite(site with { Name = LocalizedName.Of(seed.Site.Name), DefaultTimezone = seed.Site.TimeZone })];
        if (reader.GetLocationTree().Count == 0)
        {
            if (!LocationsDevSeedStep.Changes(seed, reader).TryGetValue(out var locations, out var error))
            {
                return error;
            }

            changes.AddRange(locations);
        }

        return await writer.ApplyAsync(new RegistryChangeBatch(changes), DevSeed.Context, ct).ConfigureAwait(false);
    }
}

/// <summary>Seed section <c>locations</c>: creates the tree in one batch; a location that exists (same parent, same name) is kept.</summary>
public sealed class LocationsDevSeedStep(IRegistryReader reader, IRegistryWriter writer, NodeSettings settings) : IDevSeedStep
{
    public string Section => "locations";

    public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct) => Task.FromResult(reader.GetLocationTree().Count > 0);

    public async Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            return Error.Forbidden(DevSeedReader.NotDevelopmentCode);
        }

        if (!Changes(seed, reader).TryGetValue(out var changes, out var error))
        {
            return error;
        }

        return changes.Count == 0 ? Result.Success() : await writer.ApplyAsync(new RegistryChangeBatch(changes), DevSeed.Context, ct).ConfigureAwait(false);
    }

    /// <summary>The changes that create the seed's locations missing from the registry (same parent and name = existing).</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="reader">The registry.</param>
    /// <returns>The changes, parents first.</returns>
    public static Result<List<RegistryChange>> Changes(DevSeedDocument seed, IRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(reader);
        var existing = reader.GetLocationTree();
        var ids = new Dictionary<string, LocationId>(StringComparer.Ordinal);
        var changes = new List<RegistryChange>();
        for (var i = 0; i < seed.Locations.Count; i++)
        {
            var location = seed.Locations[i];
            LocationId? parent = location.Parent is null ? null : ids[location.Parent];
            var found = existing.FirstOrDefault(l => l.ParentId == parent && NameRules.AreEquivalent(l.Name.Default, location.Name));
            if (found is not null)
            {
                ids[location.Key] = found.Id;
                continue;
            }

            if (!Enum.TryParse<LocationKind>(location.Kind, ignoreCase: true, out var kind))
            {
                return Error.Validation(DevSeedReader.InvalidCode, [CommonErrors.NotAllowedAt($"/locations/{i}/kind", [.. Enum.GetNames<LocationKind>()])]);
            }

            var id = LocationId.New();
            ids[location.Key] = id;
            changes.Add(new RegistryChange.UpsertLocation(new Location(id, parent, LocalizedName.Of(location.Name), kind, string.Empty, 0, null, null, null, i, 0)));
        }

        return changes;
    }
}

/// <summary>Seed section <c>tags</c>: creates the families (by key) and the tags that do not exist yet.</summary>
public sealed class TagsDevSeedStep(IRegistryReader reader, IRegistryWriter writer, NodeSettings settings) : IDevSeedStep
{
    public string Section => "tags";

    public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var keys = reader.GetTags().Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(seed.Tags.Any(t => keys.Contains(t.Key)));
    }

    public async Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            return Error.Forbidden(DevSeedReader.NotDevelopmentCode);
        }

        var families = reader.GetTagFamilies().ToDictionary(f => f.Key, f => f.Id, StringComparer.Ordinal);
        var tags = reader.GetTags().Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var changes = new List<RegistryChange>();
        foreach (var tag in seed.Tags)
        {
            if (!families.TryGetValue(tag.Family, out var family))
            {
                family = TagFamilyId.New();
                families[tag.Family] = family;
                changes.Add(new RegistryChange.UpsertTagFamily(new TagFamily(family, tag.Family, LocalizedName.Of(tag.Family), null, TagCardinality.ZeroOrMore, 0)));
            }

            if (tags.Add(tag.Key))
            {
                changes.Add(new RegistryChange.UpsertTag(new Tag(TagId.New(), family, tag.Key, LocalizedName.Of(tag.Name), null, 0)));
            }
        }

        return changes.Count == 0 ? Result.Success() : await writer.ApplyAsync(new RegistryChangeBatch(changes), DevSeed.Context, ct).ConfigureAwait(false);
    }
}

/// <summary>Helpers of the seed steps.</summary>
public static class DevSeed
{
    /// <summary>The change context of the seed's registry changes.</summary>
    public static ChangeContext Context { get; } = new(null, "seed");

    /// <summary>Finds the location of a seed key in the registry: the seed's chain of names from the root.</summary>
    public static LocationId? FindLocation(DevSeedDocument seed, string key, IRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(reader);
        var byKey = seed.Locations.ToDictionary(l => l.Key, StringComparer.Ordinal);
        var chain = new Stack<string>();
        for (var current = byKey.GetValueOrDefault(key); current is not null; current = current.Parent is null ? null : byKey.GetValueOrDefault(current.Parent))
        {
            chain.Push(current.Name);
        }

        var tree = reader.GetLocationTree();
        LocationId? parent = null;
        foreach (var name in chain)
        {
            var found = tree.FirstOrDefault(l => l.ParentId == parent && NameRules.AreEquivalent(l.Name.Default, name));
            if (found is null)
            {
                return null;
            }

            parent = found.Id;
        }

        return parent;
    }
}
