using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal.Storage;

namespace Watchpoint.Registry.Internal;

internal sealed class RegistryWriter : IRegistryWriter, IDisposable
{
    /// <summary>Maximum number of changes per batch (REG §6); the matching record byte-size check is T1.09c.</summary>
    internal const int MaxBatchChanges = 10_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDbContextFactory<RegistryDbContext> _dbFactory;
    private readonly ITypeCatalog _typeCatalog;
    private readonly RegistryOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly ILogger<RegistryWriter> _logger;
    private readonly Action<RegistrySnapshot> _snapshotUpdater;
    private readonly Func<RegistrySnapshot> _snapshotProvider;
    private readonly DeviceEnrichmentCache? _enrichmentCache;
    private readonly RegistryChangeFeed? _feed;

    public RegistryWriter(
        IDbContextFactory<RegistryDbContext> dbFactory,
        ITypeCatalog typeCatalog,
        RegistryOutbox outbox,
        TimeProvider clock,
        ILogger<RegistryWriter> logger,
        Action<RegistrySnapshot> snapshotUpdater,
        Func<RegistrySnapshot> snapshotProvider,
        DeviceEnrichmentCache? enrichmentCache = null,
        RegistryChangeFeed? feed = null)
    {
        _dbFactory = dbFactory;
        _typeCatalog = typeCatalog;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
        _snapshotUpdater = snapshotUpdater;
        _snapshotProvider = snapshotProvider;
        _enrichmentCache = enrichmentCache;
        _feed = feed;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    public event EventHandler<RegistryChangedEventArgs>? Changed
    {
        add { if (_feed != null) _feed.Changed += value; }
        remove { if (_feed != null) _feed.Changed -= value; }
    }

    /// <summary>The entities named by the batch itself (not cascaded side effects), spec REG §6.</summary>
    private static List<EntityChange> DescribeChanges(RegistryChangeBatch batch, RegistrySnapshot before)
    {
        static ChangeKind Upsert(bool existed) => existed ? ChangeKind.Updated : ChangeKind.Created;

        var changes = new List<EntityChange>(batch.Changes.Count);
        foreach (var change in batch.Changes)
        {
            changes.Add(change switch
            {
                RegistryChange.UpdateSite c => new EntityChange("site", c.Site.Id.Value, ChangeKind.Updated),
                RegistryChange.UpsertLocation c => new EntityChange("location", c.Location.Id.Value, Upsert(before.Locations.ContainsKey(c.Location.Id))),
                RegistryChange.DeleteLocation c => new EntityChange("location", c.Id.Value, ChangeKind.Deleted),
                RegistryChange.UpsertTagFamily c => new EntityChange("tagFamily", c.Family.Id.Value, Upsert(before.TagFamilies.ContainsKey(c.Family.Id))),
                RegistryChange.DeleteTagFamily c => new EntityChange("tagFamily", c.Id.Value, ChangeKind.Deleted),
                RegistryChange.UpsertTag c => new EntityChange("tag", c.Tag.Id.Value, Upsert(before.Tags.ContainsKey(c.Tag.Id))),
                RegistryChange.DeleteTag c => new EntityChange("tag", c.Id.Value, ChangeKind.Deleted),
                RegistryChange.UpsertServer c => new EntityChange("server", c.Server.Id.Value, Upsert(before.Servers.ContainsKey(c.Server.Id))),
                RegistryChange.DeleteServer c => new EntityChange("server", c.Id.Value, ChangeKind.Deleted),
                RegistryChange.UpsertDevice c => new EntityChange("device", c.Device.Id.Value, Upsert(before.Devices.ContainsKey(c.Device.Id))),
                RegistryChange.PatchDevice c => new EntityChange("device", c.Id.Value, ChangeKind.Updated),
                RegistryChange.DeleteDevice c => new EntityChange("device", c.Id.Value, ChangeKind.Deleted),
                RegistryChange.UpsertLink c => new EntityChange("link", c.Link.From.Value, Upsert(before.Links.Any(l => l.Id == c.Link.Id))),
                RegistryChange.DeleteLink c => new EntityChange("link", (before.Links.FirstOrDefault(l => l.Id == c.Id)?.From ?? default).Value, ChangeKind.Deleted),
                RegistryChange.UpsertCustomField c => new EntityChange("customField", c.Definition.Id.Value, Upsert(before.CustomFields.ContainsKey(c.Definition.Id))),
                RegistryChange.DeleteCustomField c => new EntityChange("customField", c.Id.Value, ChangeKind.Deleted),
                _ => throw new InvalidOperationException($"Unknown registry change {change.GetType().Name}."),
            });
        }

        return changes;
    }

    public async Task<Result> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default)
    {
        if (batch.Changes.Count > MaxBatchChanges)
        {
            return RegistryErrors.BatchTooLarge(MaxBatchChanges);
        }

        await _gate.WaitAsync(ct);
        var startTimestamp = _clock.GetTimestamp();
        try
        {
            var currentSnapshot = _snapshotProvider();

            // 1. Stage in-memory copy of current snapshot to apply mutations
            var site = currentSnapshot.Site;
            var locations = new Dictionary<LocationId, Location>(currentSnapshot.Locations);
            var tagFamilies = new Dictionary<TagFamilyId, TagFamily>(currentSnapshot.TagFamilies);
            var tags = new Dictionary<TagId, Tag>(currentSnapshot.Tags);
            var servers = new Dictionary<ServerId, Server>(currentSnapshot.Servers);
            var devices = new Dictionary<DeviceId, Device>(currentSnapshot.Devices);
            var links = new List<DeviceLink>(currentSnapshot.Links);
            var customFields = new Dictionary<CustomFieldId, CustomFieldDefinition>(currentSnapshot.CustomFields);

            var touchedLocationPaths = new Dictionary<LocationId, (string OldPath, string NewPath)>();
            var touchedServers = new Dictionary<ServerId, Server>();
            var touchedDevices = new Dictionary<DeviceId, Device>();

            // 2. Validate optimistic concurrency and apply to staged dictionaries
            foreach (var change in batch.Changes)
            {
                var stepResult = ApplyChangeStaged(
                    change,
                    ref site,
                    locations,
                    tagFamilies,
                    tags,
                    servers,
                    devices,
                    links,
                    customFields,
                    touchedLocationPaths,
                    touchedServers,
                    touchedDevices);

                if (!stepResult.IsSuccess)
                {
                    RegistryMetrics.OptimisticConflicts.Add(1);
                    RegistryLog.ConcurrencyConflict(_logger, stepResult.Error.Code.Value);
                    return stepResult;
                }
            }

            // 3. Validate business invariants R1-R10
            var invariantResult = ValidateInvariants(
                site,
                locations,
                tagFamilies,
                tags,
                servers,
                devices,
                links,
                customFields);

            if (!invariantResult.IsSuccess)
            {
                RegistryLog.InvariantViolation(_logger, invariantResult.Error.Code.Value, invariantResult.Error.DeveloperDetail ?? string.Empty);
                return invariantResult;
            }

            // 4. Persist to database in a single transaction
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await PersistChangesAsync(db, batch, currentSnapshot, site, locations, tagFamilies, tags, servers, devices, links, customFields, touchedLocationPaths, touchedServers, touchedDevices, ct);

            // The revision and the registry.changed record are committed with the batch (transactional outbox, REG §6 step 4):
            // a node stop or a log outage after this point loses nothing, and the revision never restarts or repeats.
            var revision = await NextRevisionAsync(db, ct);
            var changes = DescribeChanges(batch, currentSnapshot);
            RegistryOutbox.Add(db, revision, _clock.UtcNowMs(), context, changes);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // 5. Build new immutable snapshot and atomic swap
            var newSnapshot = new RegistrySnapshot(
                revision,
                site,
                locations,
                tagFamilies,
                tags,
                servers,
                devices,
                links,
                customFields);

            var swapStart = _clock.GetTimestamp();
            _snapshotUpdater(newSnapshot);
            var swapDuration = _clock.GetElapsedTime(swapStart);
            RegistryMetrics.SnapshotSwapDuration.Record(swapDuration.TotalSeconds);

            // 5b. Invalidate the enrichment cache of every device the batch could have affected: the entities it
            // named directly, plus every device located in the subtree of a touched location (REG §7.2).
            InvalidateEnrichment(changes, touchedLocationPaths.Keys, newSnapshot);

            // 6. Metrics & Logging
            var duration = _clock.GetElapsedTime(startTimestamp);
            RegistryMetrics.BatchesCommitted.Add(1);
            RegistryMetrics.ChangesApplied.Add(batch.Changes.Count);
            RegistryMetrics.BatchApplyDuration.Record(duration.TotalSeconds);
            RegistryMetrics.RevisionCommitted(revision);
            RegistryLog.BatchApplied(_logger, context.Origin, batch.Changes.Count, duration.TotalMilliseconds, revision);

            // 7. Wake the outbox publisher and fire the in-process Changed event (reader and writer share it)
            _outbox.Signal();
            _feed?.Raise(this, new RegistryChangedEventArgs(batch, context));

            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the cached enrichment of every device the batch could have changed (REG §7.2, REG-U-029).</summary>
    private void InvalidateEnrichment(IReadOnlyList<EntityChange> changes, IEnumerable<LocationId> touchedLocations, RegistrySnapshot newSnapshot)
    {
        if (_enrichmentCache == null)
        {
            return;
        }

        foreach (var change in changes.Where(c => c.Entity == "device"))
        {
            _enrichmentCache.Remove(DeviceId.From(change.Id));
        }

        var touchedPaths = touchedLocations
            .Select(id => newSnapshot.Locations.GetValueOrDefault(id)?.Path)
            .Where(p => p != null)
            .Select(p => p!)
            .ToList();

        if (touchedPaths.Count == 0)
        {
            return;
        }

        foreach (var device in newSnapshot.Devices.Values)
        {
            var loc = newSnapshot.Locations.GetValueOrDefault(device.LocationId);
            if (loc != null && touchedPaths.Any(p => loc.Path == p || loc.Path.StartsWith(p + "/", StringComparison.Ordinal)))
            {
                _enrichmentCache.Remove(device.Id);
            }
        }
    }

    private Result ApplyChangeStaged(
        RegistryChange change,
        ref Site site,
        Dictionary<LocationId, Location> locations,
        Dictionary<TagFamilyId, TagFamily> tagFamilies,
        Dictionary<TagId, Tag> tags,
        Dictionary<ServerId, Server> servers,
        Dictionary<DeviceId, Device> devices,
        List<DeviceLink> links,
        Dictionary<CustomFieldId, CustomFieldDefinition> customFields,
        Dictionary<LocationId, (string OldPath, string NewPath)> touchedLocationPaths,
        Dictionary<ServerId, Server> touchedServers,
        Dictionary<DeviceId, Device> touchedDevices)
    {
        switch (change)
        {
            case RegistryChange.UpdateSite us:
                if (us.ExpectedVersion.HasValue && site.Version != us.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("site", site.Id.Value.ToString(), site.Version, us.ExpectedVersion);
                site = us.Site with { Version = site.Version + 1 };
                break;

            case RegistryChange.UpsertLocation ul:
                var loc = ul.Location;
                var currentLoc = locations.GetValueOrDefault(loc.Id);
                var placement = ComputePlacement(loc.ParentId, loc.Name.Default, locations);
                if (!placement.IsSuccess)
                    return placement.Error;

                if (currentLoc != null)
                {
                    if (ul.ExpectedVersion.HasValue && currentLoc.Version != ul.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("location", loc.Id.Value.ToString(), currentLoc.Version, ul.ExpectedVersion);

                    if (currentLoc.ParentId == null && loc.ParentId != null)
                        return RegistryErrors.LocationRootImmutable();

                    var (newPath, newDepth) = placement.Value;
                    if (currentLoc.Path != newPath)
                    {
                        touchedLocationPaths[loc.Id] = (currentLoc.Path, newPath);
                        CascadePathUpdates(loc.Id, currentLoc.Path, newPath, locations, touchedLocationPaths);
                    }

                    locations[loc.Id] = loc with { Path = newPath, Depth = newDepth, Version = currentLoc.Version + 1 };
                }
                else
                {
                    var (newPath, newDepth) = placement.Value;
                    locations[loc.Id] = loc with { Path = newPath, Depth = newDepth, Version = 1 };
                }
                break;

            case RegistryChange.DeleteLocation dl:
                var existingLoc = locations.GetValueOrDefault(dl.Id);
                if (existingLoc == null)
                    return RegistryErrors.LocationNotFound(dl.Id.Value.ToString());
                if (dl.ExpectedVersion.HasValue && existingLoc.Version != dl.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("location", dl.Id.Value.ToString(), existingLoc.Version, dl.ExpectedVersion);
                if (existingLoc.ParentId == null)
                    return RegistryErrors.LocationRootImmutable();

                var childLocations = locations.Values.Where(l => l.ParentId == dl.Id).ToList();
                var childServers = servers.Values.Where(s => s.LocationId == dl.Id).ToList();
                var childDevices = devices.Values.Where(d => d.LocationId == dl.Id).ToList();
                var objectCount = childServers.Count + childDevices.Count;

                if (childLocations.Count > 0 || objectCount > 0)
                {
                    if (dl.ReassignTo == null)
                        return RegistryErrors.LocationNotEmpty(childLocations.Count, objectCount);
                    if (!locations.TryGetValue(dl.ReassignTo.Value, out var reassignTarget))
                        return RegistryErrors.LocationNotFound(dl.ReassignTo.Value.Value.ToString());

                    foreach (var child in childLocations)
                    {
                        var childPlacement = ComputePlacement(dl.ReassignTo.Value, child.Name.Default, locations);
                        if (!childPlacement.IsSuccess)
                            return childPlacement.Error;

                        var (childNewPath, childNewDepth) = childPlacement.Value;
                        touchedLocationPaths[child.Id] = (child.Path, childNewPath);
                        CascadePathUpdates(child.Id, child.Path, childNewPath, locations, touchedLocationPaths);
                        locations[child.Id] = child with { ParentId = dl.ReassignTo.Value, Path = childNewPath, Depth = childNewDepth, Version = child.Version + 1 };
                    }

                    foreach (var srv in childServers)
                    {
                        var moved = srv with { LocationId = reassignTarget.Id, Version = srv.Version + 1 };
                        servers[srv.Id] = moved;
                        touchedServers[srv.Id] = moved;
                    }

                    foreach (var dev in childDevices)
                    {
                        var moved = dev with { LocationId = reassignTarget.Id, Version = dev.Version + 1 };
                        devices[dev.Id] = moved;
                        touchedDevices[dev.Id] = moved;
                    }
                }

                locations.Remove(dl.Id);
                break;

            case RegistryChange.UpsertTagFamily utf:
                var tf = utf.Family;
                var currentTf = tagFamilies.GetValueOrDefault(tf.Id);
                if (currentTf != null)
                {
                    if (utf.ExpectedVersion.HasValue && currentTf.Version != utf.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("tag_family", tf.Id.Value.ToString(), currentTf.Version, utf.ExpectedVersion);
                    tagFamilies[tf.Id] = tf with { Version = currentTf.Version + 1 };
                }
                else
                {
                    tagFamilies[tf.Id] = tf with { Version = 1 };
                }
                break;

            case RegistryChange.DeleteTagFamily dtf:
                var existingTf = tagFamilies.GetValueOrDefault(dtf.Id);
                if (existingTf == null)
                    return RegistryErrors.TagFamilyNotFound(dtf.Id.Value.ToString());
                if (dtf.ExpectedVersion.HasValue && existingTf.Version != dtf.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("tag_family", dtf.Id.Value.ToString(), existingTf.Version, dtf.ExpectedVersion);
                tagFamilies.Remove(dtf.Id);
                break;

            case RegistryChange.UpsertTag ut:
                var tag = ut.Tag;
                var currentTag = tags.GetValueOrDefault(tag.Id);
                if (currentTag != null)
                {
                    if (ut.ExpectedVersion.HasValue && currentTag.Version != ut.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("tag", tag.Id.Value.ToString(), currentTag.Version, ut.ExpectedVersion);
                    tags[tag.Id] = tag with { Version = currentTag.Version + 1 };
                }
                else
                {
                    tags[tag.Id] = tag with { Version = 1 };
                }
                break;

            case RegistryChange.DeleteTag dt:
                if (!tags.Remove(dt.Id))
                    return RegistryErrors.TagNotFound(dt.Id.Value.ToString());
                break;

            case RegistryChange.UpsertServer usrv:
                var srv2 = usrv.Server;
                var currentSrv = servers.GetValueOrDefault(srv2.Id);
                if (currentSrv != null)
                {
                    if (usrv.ExpectedVersion.HasValue && currentSrv.Version != usrv.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("server", srv2.Id.Value.ToString(), currentSrv.Version, usrv.ExpectedVersion);
                    servers[srv2.Id] = srv2 with { Version = currentSrv.Version + 1 };
                }
                else
                {
                    servers[srv2.Id] = srv2 with { Version = 1 };
                }
                break;

            case RegistryChange.DeleteServer dsrv:
                if (!servers.ContainsKey(dsrv.Id))
                    return RegistryErrors.ServerNotFound(dsrv.Id.Value.ToString());

                var serverDevices = devices.Values.Where(d => d.ServerId == dsrv.Id).ToList();
                if (serverDevices.Count > 0 && !dsrv.DeleteDevices)
                    return RegistryErrors.ServerNotEmpty(serverDevices.Count);

                foreach (var dev in serverDevices)
                {
                    devices.Remove(dev.Id);
                    links.RemoveAll(l => l.From == dev.Id || l.To == dev.Id);
                }

                servers.Remove(dsrv.Id);
                break;

            case RegistryChange.UpsertDevice udev:
                var dev2 = udev.Device;
                var currentDev = devices.GetValueOrDefault(dev2.Id);
                if (currentDev != null)
                {
                    if (udev.ExpectedVersion.HasValue && currentDev.Version != udev.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("device", dev2.Id.Value.ToString(), currentDev.Version, udev.ExpectedVersion);
                    devices[dev2.Id] = dev2 with { Version = currentDev.Version + 1, CreatedAt = currentDev.CreatedAt };
                }
                else
                {
                    devices[dev2.Id] = dev2 with { Version = 1, CreatedAt = dev2.CreatedAt == default ? _clock.GetUtcNow() : dev2.CreatedAt };
                }
                break;

            case RegistryChange.PatchDevice pdev:
                var targetDev = devices.GetValueOrDefault(pdev.Id);
                if (targetDev == null)
                    return RegistryErrors.DeviceNotFound(pdev.Id.Value.ToString());
                if (pdev.ExpectedVersion.HasValue && targetDev.Version != pdev.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("device", pdev.Id.Value.ToString(), targetDev.Version, pdev.ExpectedVersion);

                var p = pdev.Patch;
                devices[pdev.Id] = targetDev with
                {
                    DisplayName = p.DisplayName.IsSpecified ? p.DisplayName.Value : targetDev.DisplayName,
                    LocationId = p.LocationId.IsSpecified ? p.LocationId.Value : targetDev.LocationId,
                    Tags = p.Tags.IsSpecified ? p.Tags.Value! : targetDev.Tags,
                    Attributes = p.Attributes.IsSpecified ? p.Attributes.Value! : targetDev.Attributes,
                    CustomFields = p.CustomFields.IsSpecified ? p.CustomFields.Value! : targetDev.CustomFields,
                    UnsupportedCaps = p.UnsupportedCaps.IsSpecified ? p.UnsupportedCaps.Value! : targetDev.UnsupportedCaps,
                    Geometry = p.Geometry.IsSpecified ? p.Geometry.Value : targetDev.Geometry,
                    Enabled = p.Enabled.IsSpecified ? p.Enabled.Value : targetDev.Enabled,
                    Maintenance = p.Maintenance.IsSpecified ? p.Maintenance.Value : targetDev.Maintenance,
                    Version = targetDev.Version + 1
                };
                break;

            case RegistryChange.DeleteDevice ddev:
                if (!devices.TryGetValue(ddev.Id, out var existingDev))
                    return RegistryErrors.DeviceNotFound(ddev.Id.Value.ToString());

                var childDevs = devices.Values.Where(d => d.ParentId == ddev.Id).ToList();
                if (childDevs.Count > 0 && !ddev.DeleteChildren)
                    return RegistryErrors.DeviceHasChildren(childDevs.Count);

                var subtree = new List<Device> { existingDev };
                if (ddev.DeleteChildren)
                {
                    CollectSubtree(ddev.Id, devices, subtree);
                }

                foreach (var dev in subtree)
                {
                    devices.Remove(dev.Id);
                    links.RemoveAll(l => l.From == dev.Id || l.To == dev.Id);
                }
                break;

            case RegistryChange.UpsertLink ulk:
                var link = ulk.Link;
                if (!devices.ContainsKey(link.From))
                    return RegistryErrors.LinkInvalid("sourceNotFound");
                if (!devices.ContainsKey(link.To))
                    return RegistryErrors.LinkInvalid("targetNotFound");
                if (link.From == link.To)
                    return RegistryErrors.LinkInvalid("selfLink");

                var linkIndex = links.FindIndex(l => l.Id == link.Id);
                if (linkIndex >= 0)
                    links[linkIndex] = link with { Version = links[linkIndex].Version + 1 };
                else
                    links.Add(link with { Version = 1 });
                break;

            case RegistryChange.DeleteLink dlk:
                var deleteIndex = links.FindIndex(l => l.Id == dlk.Id);
                if (deleteIndex < 0)
                    return RegistryErrors.LinkNotFound(dlk.Id.Value.ToString());
                links.RemoveAt(deleteIndex);
                break;

            case RegistryChange.UpsertCustomField ucf:
                var cf = ucf.Definition;
                var curCf = customFields.GetValueOrDefault(cf.Id);
                if (curCf != null)
                {
                    if (ucf.ExpectedVersion.HasValue && curCf.Version != ucf.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("custom_field", cf.Id.Value.ToString(), curCf.Version, ucf.ExpectedVersion);
                    customFields[cf.Id] = cf with { Version = curCf.Version + 1 };
                }
                else
                {
                    customFields[cf.Id] = cf with { Version = 1 };
                }
                break;

            case RegistryChange.DeleteCustomField dcf:
                var exCf = customFields.GetValueOrDefault(dcf.Id);
                if (exCf == null)
                    return RegistryErrors.CustomFieldNotFound(dcf.Id.Value.ToString());
                if (dcf.ExpectedVersion.HasValue && exCf.Version != dcf.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("custom_field", dcf.Id.Value.ToString(), exCf.Version, dcf.ExpectedVersion);
                customFields.Remove(dcf.Id);
                break;
        }

        return Result.Success();
    }

    private static void CollectSubtree(DeviceId parentId, Dictionary<DeviceId, Device> devices, List<Device> into)
    {
        foreach (var child in devices.Values.Where(d => d.ParentId == parentId).ToList())
        {
            into.Add(child);
            CollectSubtree(child.Id, devices, into);
        }
    }

    /// <summary>Computes the path and depth a location would have under <paramref name="parentId"/> (REG §7.3).</summary>
    private static Result<(string Path, int Depth)> ComputePlacement(LocationId? parentId, string name, Dictionary<LocationId, Location> locations)
    {
        if (parentId == null)
        {
            return ("/" + name, 0);
        }

        if (!locations.TryGetValue(parentId.Value, out var parent))
        {
            return RegistryErrors.LocationNotFound(parentId.Value.Value.ToString());
        }

        return (LocationPaths.BuildPath(parent.Path, name), parent.Depth + 1);
    }

    private static void CascadePathUpdates(
        LocationId movedId,
        string oldPath,
        string newPath,
        Dictionary<LocationId, Location> locations,
        Dictionary<LocationId, (string OldPath, string NewPath)> touchedLocationPaths)
    {
        foreach (var (id, loc) in locations.ToList())
        {
            if (id == movedId) continue;
            if (loc.Path.StartsWith(oldPath + "/", StringComparison.Ordinal))
            {
                var descendantNewPath = LocationPaths.RewriteDescendantPath(loc.Path, oldPath, newPath);
                touchedLocationPaths[id] = (loc.Path, descendantNewPath);
                var segs = descendantNewPath.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                locations[id] = loc with { Path = descendantNewPath, Depth = segs.Length - 1, Version = loc.Version + 1 };
            }
        }
    }

    private Result ValidateInvariants(
        Site site,
        Dictionary<LocationId, Location> locations,
        Dictionary<TagFamilyId, TagFamily> tagFamilies,
        Dictionary<TagId, Tag> tags,
        Dictionary<ServerId, Server> servers,
        Dictionary<DeviceId, Device> devices,
        List<DeviceLink> links,
        Dictionary<CustomFieldId, CustomFieldDefinition> customFields)
    {
        // R1: valid IANA time zone (site identity itself is enforced structurally: Site is a single field, not a
        // collection, and the stored row's id is reconciled against Site:Id by the readiness check, REG §4).
        if (string.IsNullOrWhiteSpace(site.DefaultTimezone))
            return CommonErrors.InvalidFormat("IANA time zone identifier");
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(site.DefaultTimezone);
        }
        catch (TimeZoneNotFoundException)
        {
            return CommonErrors.InvalidFormat("IANA time zone identifier");
        }
        catch (InvalidTimeZoneException)
        {
            return CommonErrors.InvalidFormat("IANA time zone identifier");
        }

        // R2: exactly one root location, no cycles, valid parents, max depth, unique sibling names.
        var rootCount = locations.Values.Count(l => l.ParentId == null);
        if (rootCount != 1)
            return RegistryErrors.LocationCycle();

        foreach (var siblingGroup in locations.Values.GroupBy(l => l.ParentId))
        {
            var seen = new Dictionary<string, LocationId>();
            foreach (var sibling in siblingGroup)
            {
                var normalized = TextSearchNormalizer.Normalize(sibling.Name.Default);
                if (seen.TryGetValue(normalized, out var existingId) && existingId != sibling.Id)
                    return RegistryErrors.LocationDuplicateName(sibling.Name.Default);
                seen[normalized] = sibling.Id;
            }
        }

        foreach (var loc in locations.Values)
        {
            if (loc.ParentId != null && !locations.ContainsKey(loc.ParentId.Value))
                return RegistryErrors.LocationNotFound(loc.ParentId.Value.Value.ToString());

            if (loc.Depth > 16)
                return RegistryErrors.LocationTooDeep(16);

            var curr = loc;
            var visited = new HashSet<LocationId> { curr.Id };
            while (curr.ParentId != null)
            {
                if (visited.Contains(curr.ParentId.Value))
                    return RegistryErrors.LocationCycle();
                visited.Add(curr.ParentId.Value);
                if (!locations.TryGetValue(curr.ParentId.Value, out var parent))
                    break;
                curr = parent;
            }
        }

        // R3: Tag.Key unique; tag in an exclusive family holds at most one tag of that family.
        foreach (var keyGroup in tags.Values.GroupBy(t => t.Key, StringComparer.Ordinal))
        {
            if (keyGroup.Count() > 1)
                return RegistryErrors.TagDuplicateKey(keyGroup.Key);
        }

        var exclusiveFamilies = tagFamilies.Values.Where(f => f.Cardinality == TagCardinality.ZeroOrOne).ToDictionary(f => f.Id);
        foreach (var dev in devices.Values)
        {
            var familyCounts = new Dictionary<TagFamilyId, int>();
            foreach (var tid in dev.Tags)
            {
                if (!tags.TryGetValue(tid, out var t))
                    return RegistryErrors.TagNotFound(tid.Value.ToString());

                if (exclusiveFamilies.TryGetValue(t.FamilyId, out var family))
                {
                    familyCounts[t.FamilyId] = familyCounts.GetValueOrDefault(t.FamilyId) + 1;
                    if (familyCounts[t.FamilyId] > 1)
                        return RegistryErrors.TagFamilyExclusive(family.Key);
                }
            }
        }

        // R4: Server.Type and Device.Type resolve in MOD with the matching role.
        foreach (var srv in servers.Values)
        {
            var dtRes = _typeCatalog.Get(srv.Type);
            if (!dtRes.IsSuccess)
                return dtRes.Error;
            if (dtRes.Value.Role != TypeRole.Server)
                return RegistryErrors.TypeInvalidRole(srv.Type.Id.Value, "server");
        }

        foreach (var dev in devices.Values)
        {
            var dtRes = _typeCatalog.Get(dev.Type);
            if (!dtRes.IsSuccess)
                return dtRes.Error;
            if (dtRes.Value.Role != TypeRole.Device)
                return RegistryErrors.TypeInvalidRole(dev.Type.Id.Value, "device");
        }

        // R5: (ServerId, NativeId) unique per device; (ConnectionId, NativeId) unique per server.
        var serverSeen = new HashSet<(ConnectionId, string)>();
        foreach (var srv in servers.Values)
        {
            if (!serverSeen.Add((srv.ConnectionId, srv.NativeId)))
                return RegistryErrors.ServerDuplicateNativeId(srv.NativeId);
        }

        var devSeen = new HashSet<(ServerId, string)>();
        foreach (var dev in devices.Values)
        {
            if (!devSeen.Add((dev.ServerId, dev.NativeId)))
                return RegistryErrors.DeviceDuplicateNativeId(dev.NativeId);
        }

        // R6: Device parent belongs to same server; parent AllowedChildTypes; no cycles; depth <= 4.
        foreach (var dev in devices.Values)
        {
            if (dev.ParentId == null)
                continue;

            if (!devices.TryGetValue(dev.ParentId.Value, out var parentDev))
                return RegistryErrors.DeviceNotFound(dev.ParentId.Value.Value.ToString());

            if (parentDev.ServerId != dev.ServerId)
                return RegistryErrors.DeviceParentInvalid("otherServer");

            var parentTypeResult = _typeCatalog.Get(parentDev.Type);
            if (parentTypeResult.IsSuccess && parentTypeResult.Value.AllowedChildTypes.Count > 0
                && !parentTypeResult.Value.AllowedChildTypes.Contains(dev.Type.Id.Value, StringComparer.Ordinal))
                return RegistryErrors.DeviceParentInvalid("typeNotAllowed");

            var cur = dev;
            var depth = 0;
            var visited = new HashSet<DeviceId> { cur.Id };
            while (cur.ParentId != null)
            {
                depth++;
                if (depth > 4)
                    return RegistryErrors.DeviceParentInvalid("tooDeep");
                if (visited.Contains(cur.ParentId.Value))
                    return RegistryErrors.DeviceParentInvalid("cycle");
                visited.Add(cur.ParentId.Value);
                if (!devices.TryGetValue(cur.ParentId.Value, out var nextParent))
                    break;
                cur = nextParent;
            }
        }

        // R7: Server parent belongs to same connection; depth <= 3.
        foreach (var srv in servers.Values)
        {
            if (srv.ParentId == null)
                continue;

            if (!servers.TryGetValue(srv.ParentId.Value, out var parentSrv))
                return RegistryErrors.ServerNotFound(srv.ParentId.Value.Value.ToString());

            if (parentSrv.ConnectionId != srv.ConnectionId)
                return RegistryErrors.ServerParentInvalid("otherConnection");

            var cur = srv;
            var depth = 0;
            var visited = new HashSet<ServerId> { cur.Id };
            while (cur.ParentId != null)
            {
                depth++;
                if (depth > 3)
                    return RegistryErrors.ServerParentInvalid("tooDeep");
                if (visited.Contains(cur.ParentId.Value))
                    return RegistryErrors.ServerParentInvalid("cycle");
                visited.Add(cur.ParentId.Value);
                if (!servers.TryGetValue(cur.ParentId.Value, out var nextParent))
                    break;
                cur = nextParent;
            }
        }

        // R8: Custom field target+key unique; required custom fields present on devices.
        var cfSeen = new HashSet<(CustomFieldTarget, string)>();
        foreach (var cf in customFields.Values)
        {
            if (!cfSeen.Add((cf.Target, cf.Key)))
                return Error.Create(ErrorKind.Validation, CommonErrorCodes.ValueDuplicate.Value, ("value", cf.Key));
        }

        var requiredDeviceCfs = customFields.Values.Where(cf => cf.Target == CustomFieldTarget.Device && cf.Required).ToList();
        foreach (var dev in devices.Values)
        {
            foreach (var reqCf in requiredDeviceCfs)
            {
                if (!dev.CustomFields.ContainsKey(reqCf.Id))
                    return Error.Create(ErrorKind.Validation, CommonErrorCodes.ValueRequired.Value, ("field", reqCf.Key));
            }
        }

        // R9: Link endpoints exist (checked when staged); no duplicate (kind, from, to).
        var linkSeen = new HashSet<(LinkKind, DeviceId, DeviceId)>();
        foreach (var l in links)
        {
            if (!linkSeen.Add((l.Kind, l.From, l.To)))
                return RegistryErrors.LinkInvalid("duplicate");
        }

        // R10: Every device / server has a location.
        foreach (var srv in servers.Values)
        {
            if (!locations.ContainsKey(srv.LocationId))
                return RegistryErrors.LocationNotFound(srv.LocationId.Value.ToString());
        }

        foreach (var dev in devices.Values)
        {
            if (!locations.ContainsKey(dev.LocationId))
                return RegistryErrors.LocationNotFound(dev.LocationId.Value.ToString());
            if (!servers.ContainsKey(dev.ServerId))
                return RegistryErrors.ServerNotFound(dev.ServerId.Value.ToString());
        }

        return Result.Success();
    }

    /// <summary>
    /// Increments the persisted revision inside the current transaction and returns the new value. The row is created on
    /// first use (databases created without the migration, e.g. the unit tests' SQLite). A concurrent writer on another
    /// node fails on the concurrency token instead of reusing a revision.
    /// </summary>
    private static async Task<long> NextRevisionAsync(RegistryDbContext db, CancellationToken ct)
    {
        var row = await db.Revision.AsTracking().FirstOrDefaultAsync(r => r.Id == RevisionRecord.SingletonId, ct);
        if (row == null)
        {
            row = new RevisionRecord { Id = RevisionRecord.SingletonId, Value = 0 };
            db.Revision.Add(row);
        }

        row.Value++;
        return row.Value;
    }

    private async Task PersistChangesAsync(
        RegistryDbContext db,
        RegistryChangeBatch batch,
        RegistrySnapshot before,
        Site site,
        Dictionary<LocationId, Location> locations,
        Dictionary<TagFamilyId, TagFamily> tagFamilies,
        Dictionary<TagId, Tag> tags,
        Dictionary<ServerId, Server> servers,
        Dictionary<DeviceId, Device> devices,
        List<DeviceLink> links,
        Dictionary<CustomFieldId, CustomFieldDefinition> customFields,
        Dictionary<LocationId, (string OldPath, string NewPath)> touchedLocationPaths,
        Dictionary<ServerId, Server> touchedServers,
        Dictionary<DeviceId, Device> touchedDevices,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // 1. Sites
        var siteChanges = batch.Changes.OfType<RegistryChange.UpdateSite>().ToList();
        if (siteChanges.Count > 0)
        {
            var existingSite = await db.Sites.AsTracking().FirstOrDefaultAsync(ct);
            if (existingSite == null)
            {
                db.Sites.Add(new SiteRecord
                {
                    Id = site.Id.Value,
                    NameDefault = site.Name.Default,
                    NameTranslations = JsonSerializer.Serialize(site.Name.Translations),
                    DefaultTimezone = site.DefaultTimezone,
                    RowVersion = site.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                existingSite.NameDefault = site.Name.Default;
                existingSite.NameTranslations = JsonSerializer.Serialize(site.Name.Translations);
                existingSite.DefaultTimezone = site.DefaultTimezone;
                existingSite.RowVersion = site.Version;
                existingSite.UpdatedAt = now;
                db.Sites.Update(existingSite);
            }
        }

        // 2. Locations: explicit upserts, plus every location whose path changed (moves, renames, reassignment cascades)
        var upsertedLocationIds = batch.Changes.OfType<RegistryChange.UpsertLocation>().Select(c => c.Location.Id);
        var locationIdsToSave = upsertedLocationIds.Concat(touchedLocationPaths.Keys).Distinct().OrderBy(id => locations[id].Depth);
        foreach (var locId in locationIdsToSave)
        {
            var loc = locations[locId];
            var el = await db.Locations.AsTracking().FirstOrDefaultAsync(r => r.Id == loc.Id.Value, ct);
            if (el == null)
            {
                db.Locations.Add(new LocationRecord
                {
                    Id = loc.Id.Value,
                    ParentId = loc.ParentId?.Value,
                    NameDefault = loc.Name.Default,
                    NameTranslations = JsonSerializer.Serialize(loc.Name.Translations),
                    Kind = loc.Kind.ToString(),
                    Path = loc.Path,
                    Depth = loc.Depth,
                    Tz = loc.Tz,
                    Address = loc.Address,
                    Notes = loc.Notes,
                    SortOrder = loc.SortOrder,
                    RowVersion = loc.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                el.ParentId = loc.ParentId?.Value;
                el.NameDefault = loc.Name.Default;
                el.NameTranslations = JsonSerializer.Serialize(loc.Name.Translations);
                el.Kind = loc.Kind.ToString();
                el.Path = loc.Path;
                el.Depth = loc.Depth;
                el.Tz = loc.Tz;
                el.Address = loc.Address;
                el.Notes = loc.Notes;
                el.SortOrder = loc.SortOrder;
                el.RowVersion = loc.Version;
                el.UpdatedAt = now;
                db.Locations.Update(el);
            }

            await db.SaveChangesAsync(ct);
        }

        // 3. Tag Families & Tags
        foreach (var utf in batch.Changes.OfType<RegistryChange.UpsertTagFamily>())
        {
            var tf = tagFamilies[utf.Family.Id];
            var etf = await db.TagFamilies.AsTracking().FirstOrDefaultAsync(r => r.Id == tf.Id.Value, ct);
            if (etf == null)
            {
                db.TagFamilies.Add(new TagFamilyRecord
                {
                    Id = tf.Id.Value,
                    Key = tf.Key,
                    NameDefault = tf.Name.Default,
                    NameTranslations = JsonSerializer.Serialize(tf.Name.Translations),
                    Color = tf.Color,
                    Cardinality = tf.Cardinality.ToString(),
                    RowVersion = tf.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                etf.Key = tf.Key;
                etf.NameDefault = tf.Name.Default;
                etf.NameTranslations = JsonSerializer.Serialize(tf.Name.Translations);
                etf.Color = tf.Color;
                etf.Cardinality = tf.Cardinality.ToString();
                etf.RowVersion = tf.Version;
                etf.UpdatedAt = now;
                db.TagFamilies.Update(etf);
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var ut in batch.Changes.OfType<RegistryChange.UpsertTag>())
        {
            var tag = tags[ut.Tag.Id];
            var et = await db.Tags.AsTracking().FirstOrDefaultAsync(r => r.Id == tag.Id.Value, ct);
            if (et == null)
            {
                db.Tags.Add(new TagRecord
                {
                    Id = tag.Id.Value,
                    FamilyId = tag.FamilyId.Value,
                    Key = tag.Key,
                    NameDefault = tag.Name.Default,
                    NameTranslations = JsonSerializer.Serialize(tag.Name.Translations),
                    Color = tag.Color,
                    RowVersion = tag.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                et.FamilyId = tag.FamilyId.Value;
                et.Key = tag.Key;
                et.NameDefault = tag.Name.Default;
                et.NameTranslations = JsonSerializer.Serialize(tag.Name.Translations);
                et.Color = tag.Color;
                et.RowVersion = tag.Version;
                et.UpdatedAt = now;
                db.Tags.Update(et);
            }
        }
        await db.SaveChangesAsync(ct);

        // 4. Servers: explicit upserts plus servers reassigned to another location by a location delete.
        var serverIdsToSave = batch.Changes.OfType<RegistryChange.UpsertServer>().Select(c => c.Server.Id).Concat(touchedServers.Keys).Distinct();
        foreach (var srvId in serverIdsToSave)
        {
            var srv = servers[srvId];
            var es = await db.Servers.AsTracking().FirstOrDefaultAsync(r => r.Id == srv.Id.Value, ct);
            if (es == null)
            {
                db.Servers.Add(new ServerRecord
                {
                    Id = srv.Id.Value,
                    TypeId = srv.Type.Id.ToString(),
                    TypeVersion = srv.Type.Version.ToString(),
                    ConnectionId = srv.ConnectionId.Value,
                    ParentId = srv.ParentId?.Value,
                    NativeId = srv.NativeId,
                    DisplayName = srv.DisplayName != null ? JsonSerializer.Serialize(srv.DisplayName) : null,
                    VendorName = srv.VendorName,
                    LocationId = srv.LocationId.Value,
                    Tags = srv.Tags.Select(t => t.Value).ToArray(),
                    CustomFields = JsonSerializer.Serialize(srv.CustomFields),
                    Enabled = srv.Enabled,
                    Maintenance = srv.Maintenance,
                    RowVersion = srv.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                es.TypeId = srv.Type.Id.ToString();
                es.TypeVersion = srv.Type.Version.ToString();
                es.ConnectionId = srv.ConnectionId.Value;
                es.ParentId = srv.ParentId?.Value;
                es.NativeId = srv.NativeId;
                es.DisplayName = srv.DisplayName != null ? JsonSerializer.Serialize(srv.DisplayName) : null;
                es.VendorName = srv.VendorName;
                es.LocationId = srv.LocationId.Value;
                es.Tags = srv.Tags.Select(t => t.Value).ToArray();
                es.CustomFields = JsonSerializer.Serialize(srv.CustomFields);
                es.Enabled = srv.Enabled;
                es.Maintenance = srv.Maintenance;
                es.RowVersion = srv.Version;
                es.UpdatedAt = now;
                db.Servers.Update(es);
            }
        }
        await db.SaveChangesAsync(ct);

        // 5. Devices: explicit upserts/patches plus devices reassigned to another location by a location delete.
        var deviceIdsToSave = batch.Changes.OfType<RegistryChange.UpsertDevice>().Select(c => c.Device.Id)
            .Concat(batch.Changes.OfType<RegistryChange.PatchDevice>().Select(c => c.Id))
            .Concat(touchedDevices.Keys)
            .Distinct();
        foreach (var devId in deviceIdsToSave)
        {
            var dev = devices[devId];
            var ed = await db.Devices.AsTracking().FirstOrDefaultAsync(r => r.Id == dev.Id.Value, ct);
            if (ed == null)
            {
                db.Devices.Add(new DeviceRecord
                {
                    Id = dev.Id.Value,
                    TypeId = dev.Type.Id.ToString(),
                    TypeVersion = dev.Type.Version.ToString(),
                    ServerId = dev.ServerId.Value,
                    ParentId = dev.ParentId?.Value,
                    NativeId = dev.NativeId,
                    DisplayName = dev.DisplayName != null ? JsonSerializer.Serialize(dev.DisplayName) : null,
                    VendorName = dev.VendorName,
                    LocationId = dev.LocationId.Value,
                    Geometry = dev.Geometry != null ? JsonSerializer.Serialize(dev.Geometry) : null,
                    Attributes = JsonSerializer.Serialize(dev.Attributes),
                    Tags = dev.Tags.Select(t => t.Value).ToArray(),
                    CustomFields = JsonSerializer.Serialize(dev.CustomFields),
                    UnsupportedCaps = dev.UnsupportedCaps.Select(c => c.ToString()).ToArray(),
                    Enabled = dev.Enabled,
                    Maintenance = dev.Maintenance,
                    Orphaned = dev.Orphaned,
                    RowVersion = dev.Version,
                    CreatedAt = dev.CreatedAt,
                    UpdatedAt = now
                });
            }
            else
            {
                ed.TypeId = dev.Type.Id.ToString();
                ed.TypeVersion = dev.Type.Version.ToString();
                ed.ServerId = dev.ServerId.Value;
                ed.ParentId = dev.ParentId?.Value;
                ed.NativeId = dev.NativeId;
                ed.DisplayName = dev.DisplayName != null ? JsonSerializer.Serialize(dev.DisplayName) : null;
                ed.VendorName = dev.VendorName;
                ed.LocationId = dev.LocationId.Value;
                ed.Geometry = dev.Geometry != null ? JsonSerializer.Serialize(dev.Geometry) : null;
                ed.Attributes = JsonSerializer.Serialize(dev.Attributes);
                ed.Tags = dev.Tags.Select(t => t.Value).ToArray();
                ed.CustomFields = JsonSerializer.Serialize(dev.CustomFields);
                ed.UnsupportedCaps = dev.UnsupportedCaps.Select(c => c.ToString()).ToArray();
                ed.Enabled = dev.Enabled;
                ed.Maintenance = dev.Maintenance;
                ed.Orphaned = dev.Orphaned;
                ed.RowVersion = dev.Version;
                ed.UpdatedAt = now;
                db.Devices.Update(ed);
            }
        }
        await db.SaveChangesAsync(ct);

        // 6. Device Links: explicit upserts.
        foreach (var ulk in batch.Changes.OfType<RegistryChange.UpsertLink>())
        {
            var link = links.First(l => l.Id == ulk.Link.Id);
            var elink = await db.DeviceLinks.AsTracking().FirstOrDefaultAsync(r => r.Id == link.Id.Value, ct);
            if (elink == null)
            {
                db.DeviceLinks.Add(new DeviceLinkRecord
                {
                    Id = link.Id.Value,
                    Kind = link.Kind.ToString(),
                    FromId = link.From.Value,
                    ToId = link.To.Value,
                    RowVersion = link.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                elink.Kind = link.Kind.ToString();
                elink.FromId = link.From.Value;
                elink.ToId = link.To.Value;
                elink.RowVersion = link.Version;
                elink.UpdatedAt = now;
                db.DeviceLinks.Update(elink);
            }
        }

        // 7. Custom Fields
        foreach (var ucf in batch.Changes.OfType<RegistryChange.UpsertCustomField>())
        {
            var cf = customFields[ucf.Definition.Id];
            var ecf = await db.CustomFields.AsTracking().FirstOrDefaultAsync(r => r.Id == cf.Id.Value, ct);
            if (ecf == null)
            {
                db.CustomFields.Add(new CustomFieldRecord
                {
                    Id = cf.Id.Value,
                    Target = cf.Target.ToString(),
                    Key = cf.Key,
                    Label = JsonSerializer.Serialize(cf.Label),
                    Kind = cf.Kind.ToString(),
                    Required = cf.Required,
                    DefaultValue = cf.DefaultValue != null ? JsonSerializer.Serialize(cf.DefaultValue) : null,
                    RowVersion = cf.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                ecf.Target = cf.Target.ToString();
                ecf.Key = cf.Key;
                ecf.Label = JsonSerializer.Serialize(cf.Label);
                ecf.Kind = cf.Kind.ToString();
                ecf.Required = cf.Required;
                ecf.DefaultValue = cf.DefaultValue != null ? JsonSerializer.Serialize(cf.DefaultValue) : null;
                ecf.RowVersion = cf.Version;
                ecf.UpdatedAt = now;
                db.CustomFields.Update(ecf);
            }
        }

        // 8. Deletions, derived from the before/after diff so that cascades (DeleteServer DeleteDevices,
        // DeleteDevice DeleteChildren, device/location deletion dropping their links) are persisted correctly,
        // in dependency order: links, devices, servers, tags, tag families, custom fields, locations.
        var deletedLinkIds = before.Links.Select(l => l.Id).Except(links.Select(l => l.Id)).Select(id => id.Value).ToList();
        if (deletedLinkIds.Count > 0)
        {
            await db.DeviceLinks.Where(r => deletedLinkIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedDeviceIds = before.Devices.Keys.Except(devices.Keys).Select(id => id.Value).ToList();
        if (deletedDeviceIds.Count > 0)
        {
            await db.Devices.Where(r => deletedDeviceIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedServerIds = before.Servers.Keys.Except(servers.Keys).Select(id => id.Value).ToList();
        if (deletedServerIds.Count > 0)
        {
            await db.Servers.Where(r => deletedServerIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedTagIds = before.Tags.Keys.Except(tags.Keys).Select(id => id.Value).ToList();
        if (deletedTagIds.Count > 0)
        {
            await db.Tags.Where(r => deletedTagIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedTagFamilyIds = before.TagFamilies.Keys.Except(tagFamilies.Keys).Select(id => id.Value).ToList();
        if (deletedTagFamilyIds.Count > 0)
        {
            await db.TagFamilies.Where(r => deletedTagFamilyIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedCustomFieldIds = before.CustomFields.Keys.Except(customFields.Keys).Select(id => id.Value).ToList();
        if (deletedCustomFieldIds.Count > 0)
        {
            await db.CustomFields.Where(r => deletedCustomFieldIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        var deletedLocationIds = before.Locations.Keys.Except(locations.Keys).Select(id => id.Value).ToList();
        if (deletedLocationIds.Count > 0)
        {
            await db.Locations.Where(r => deletedLocationIds.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }
    }
}
