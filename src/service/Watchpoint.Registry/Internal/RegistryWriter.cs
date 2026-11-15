using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal.Storage;

namespace Watchpoint.Registry.Internal;

internal sealed class RegistryWriter : IRegistryWriter, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDbContextFactory<RegistryDbContext> _dbFactory;
    private readonly ITypeCatalog _typeCatalog;
    private readonly RegistryOutbox _outbox;
    private readonly TimeProvider _clock;
    private readonly ILogger<RegistryWriter> _logger;
    private readonly Action<RegistrySnapshot> _snapshotUpdater;
    private readonly Func<RegistrySnapshot> _snapshotProvider;

    public RegistryWriter(
        IDbContextFactory<RegistryDbContext> dbFactory,
        ITypeCatalog typeCatalog,
        RegistryOutbox outbox,
        TimeProvider clock,
        ILogger<RegistryWriter> logger,
        Action<RegistrySnapshot> snapshotUpdater,
        Func<RegistrySnapshot> snapshotProvider)
    {
        _dbFactory = dbFactory;
        _typeCatalog = typeCatalog;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
        _snapshotUpdater = snapshotUpdater;
        _snapshotProvider = snapshotProvider;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>The entities named by a batch, with what happened to each compared with the snapshot before the batch.</summary>
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
                RegistryChange.UpsertDeviceLink c => new EntityChange(
                    "link",
                    c.Link.SourceDeviceId.Value,
                    Upsert(before.Links.Any(l => l.SourceDeviceId == c.Link.SourceDeviceId && l.Role == c.Link.Role && l.TargetDeviceId == c.Link.TargetDeviceId))),
                RegistryChange.DeleteDeviceLink c => new EntityChange("link", c.SourceDeviceId.Value, ChangeKind.Deleted),
                RegistryChange.UpsertCustomField c => new EntityChange("customField", c.CustomField.Id.Value, Upsert(before.CustomFields.ContainsKey(c.CustomField.Id))),
                RegistryChange.DeleteCustomField c => new EntityChange("customField", c.Id.Value, ChangeKind.Deleted),
                _ => throw new InvalidOperationException($"Unknown registry change {change.GetType().Name}."),
            });
        }

        return changes;
    }

    public event EventHandler<RegistryChangedEventArgs>? Changed;

    public async Task<Result> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default)
    {
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
                    touchedLocationPaths);

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

            await PersistChangesAsync(db, batch, site, locations, tagFamilies, tags, servers, devices, links, customFields, touchedLocationPaths, ct);

            // The revision and the registry.changed record are committed with the batch (transactional outbox, REG §6 step 4):
            // a node stop or a log outage after this point loses nothing, and the revision never restarts or repeats.
            var revision = await NextRevisionAsync(db, ct);
            var changes = DescribeChanges(batch, currentSnapshot);
            RegistryOutbox.Add(db, revision, _clock.UtcNowMs(), context, changes);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // 5. Build new immutable snapshot and atomic swap
            var newSnapshot = new RegistrySnapshot(
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

            // 6. Metrics & Logging
            var duration = _clock.GetElapsedTime(startTimestamp);
            RegistryMetrics.BatchesCommitted.Add(1);
            RegistryMetrics.ChangesApplied.Add(batch.Changes.Count);
            RegistryMetrics.BatchApplyDuration.Record(duration.TotalSeconds);
            RegistryMetrics.RevisionCommitted(revision);
            RegistryLog.BatchApplied(_logger, context.Reason ?? "unknown", batch.Changes.Count, duration.TotalMilliseconds, revision);

            // 7. Wake the outbox publisher and fire the in-process Changed event
            _outbox.Signal();
            Changed?.Invoke(this, new RegistryChangedEventArgs(batch, context));

            return Result.Success();
        }
        finally
        {
            _gate.Release();
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
        Dictionary<LocationId, (string OldPath, string NewPath)> touchedLocationPaths)
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
                if (currentLoc != null)
                {
                    if (ul.ExpectedVersion.HasValue && currentLoc.Version != ul.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("location", loc.Id.Value.ToString(), currentLoc.Version, ul.ExpectedVersion);

                    // Compute path & depth
                    string newPath;
                    int newDepth;
                    if (loc.ParentId != null)
                    {
                        var parent = locations.GetValueOrDefault(loc.ParentId.Value);
                        if (parent == null)
                            return RegistryErrors.InvariantViolation("R2", $"Parent location {loc.ParentId} not found.");
                        newPath = LocationPaths.BuildPath(parent.Path, loc.Name.Default);
                        newDepth = parent.Depth + 1;
                    }
                    else
                    {
                        newPath = "/" + loc.Name.Default;
                        newDepth = 0;
                    }

                    if (currentLoc.Path != newPath)
                    {
                        touchedLocationPaths[loc.Id] = (currentLoc.Path, newPath);
                        // Cascade descendant location path updates
                        CascadePathUpdates(loc.Id, currentLoc.Path, newPath, locations, touchedLocationPaths);
                    }

                    locations[loc.Id] = loc with { Path = newPath, Depth = newDepth, Version = currentLoc.Version + 1 };
                }
                else
                {
                    string newPath;
                    int newDepth;
                    if (loc.ParentId != null)
                    {
                        var parent = locations.GetValueOrDefault(loc.ParentId.Value);
                        if (parent == null)
                            return RegistryErrors.InvariantViolation("R2", $"Parent location {loc.ParentId} not found.");
                        newPath = LocationPaths.BuildPath(parent.Path, loc.Name.Default);
                        newDepth = parent.Depth + 1;
                    }
                    else
                    {
                        newPath = "/" + loc.Name.Default;
                        newDepth = 0;
                    }
                    locations[loc.Id] = loc with { Path = newPath, Depth = newDepth, Version = 1 };
                }
                break;

            case RegistryChange.DeleteLocation dl:
                var existingLoc = locations.GetValueOrDefault(dl.Id);
                if (existingLoc == null)
                    return RegistryErrors.EntityNotFound("location", dl.Id.Value.ToString());
                if (dl.ExpectedVersion.HasValue && existingLoc.Version != dl.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("location", dl.Id.Value.ToString(), existingLoc.Version, dl.ExpectedVersion);
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
                    return RegistryErrors.EntityNotFound("tag_family", dtf.Id.Value.ToString());
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
                var existingTag = tags.GetValueOrDefault(dt.Id);
                if (existingTag == null)
                    return RegistryErrors.EntityNotFound("tag", dt.Id.Value.ToString());
                if (dt.ExpectedVersion.HasValue && existingTag.Version != dt.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("tag", dt.Id.Value.ToString(), existingTag.Version, dt.ExpectedVersion);
                tags.Remove(dt.Id);
                break;

            case RegistryChange.UpsertServer usrv:
                var srv = usrv.Server;
                var currentSrv = servers.GetValueOrDefault(srv.Id);
                if (currentSrv != null)
                {
                    if (usrv.ExpectedVersion.HasValue && currentSrv.Version != usrv.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("server", srv.Id.Value.ToString(), currentSrv.Version, usrv.ExpectedVersion);
                    servers[srv.Id] = srv with { Version = currentSrv.Version + 1 };
                }
                else
                {
                    servers[srv.Id] = srv with { Version = 1 };
                }
                break;

            case RegistryChange.DeleteServer dsrv:
                var existingSrv = servers.GetValueOrDefault(dsrv.Id);
                if (existingSrv == null)
                    return RegistryErrors.EntityNotFound("server", dsrv.Id.Value.ToString());
                if (dsrv.ExpectedVersion.HasValue && existingSrv.Version != dsrv.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("server", dsrv.Id.Value.ToString(), existingSrv.Version, dsrv.ExpectedVersion);
                servers.Remove(dsrv.Id);
                break;

            case RegistryChange.UpsertDevice udev:
                var dev = udev.Device;
                var currentDev = devices.GetValueOrDefault(dev.Id);
                if (currentDev != null)
                {
                    if (udev.ExpectedVersion.HasValue && currentDev.Version != udev.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("device", dev.Id.Value.ToString(), currentDev.Version, udev.ExpectedVersion);
                    devices[dev.Id] = dev with { Version = currentDev.Version + 1, CreatedAt = currentDev.CreatedAt };
                }
                else
                {
                    devices[dev.Id] = dev with { Version = 1, CreatedAt = dev.CreatedAt == default ? _clock.GetUtcNow() : dev.CreatedAt };
                }
                break;

            case RegistryChange.PatchDevice pdev:
                var targetDev = devices.GetValueOrDefault(pdev.Id);
                if (targetDev == null)
                    return RegistryErrors.EntityNotFound("device", pdev.Id.Value.ToString());
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
                var existingDev = devices.GetValueOrDefault(ddev.Id);
                if (existingDev == null)
                    return RegistryErrors.EntityNotFound("device", ddev.Id.Value.ToString());
                if (ddev.ExpectedVersion.HasValue && existingDev.Version != ddev.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("device", ddev.Id.Value.ToString(), existingDev.Version, ddev.ExpectedVersion);
                devices.Remove(ddev.Id);
                break;

            case RegistryChange.UpsertDeviceLink udl:
                var link = udl.Link;
                var existingLinkIndex = links.FindIndex(l => l.SourceDeviceId == link.SourceDeviceId && l.Role == link.Role && l.TargetDeviceId == link.TargetDeviceId);
                if (existingLinkIndex >= 0)
                {
                    var cur = links[existingLinkIndex];
                    if (udl.ExpectedVersion.HasValue && cur.Version != udl.ExpectedVersion.Value)
                        return RegistryErrors.VersionConflict("device_link", $"{link.SourceDeviceId}:{link.Role}:{link.TargetDeviceId}", cur.Version, udl.ExpectedVersion);
                    links[existingLinkIndex] = link with { Version = cur.Version + 1 };
                }
                else
                {
                    links.Add(link with { Version = 1 });
                }
                break;

            case RegistryChange.DeleteDeviceLink ddl:
                var delIndex = links.FindIndex(l => l.SourceDeviceId == ddl.SourceDeviceId && l.Role == ddl.Role && l.TargetDeviceId == ddl.TargetDeviceId);
                if (delIndex < 0)
                    return RegistryErrors.EntityNotFound("device_link", $"{ddl.SourceDeviceId}:{ddl.Role}:{ddl.TargetDeviceId}");
                var curDel = links[delIndex];
                if (ddl.ExpectedVersion.HasValue && curDel.Version != ddl.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("device_link", $"{ddl.SourceDeviceId}:{ddl.Role}:{ddl.TargetDeviceId}", curDel.Version, ddl.ExpectedVersion);
                links.RemoveAt(delIndex);
                break;

            case RegistryChange.UpsertCustomField ucf:
                var cf = ucf.CustomField;
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
                    return RegistryErrors.EntityNotFound("custom_field", dcf.Id.Value.ToString());
                if (dcf.ExpectedVersion.HasValue && exCf.Version != dcf.ExpectedVersion.Value)
                    return RegistryErrors.VersionConflict("custom_field", dcf.Id.Value.ToString(), exCf.Version, dcf.ExpectedVersion);
                customFields.Remove(dcf.Id);
                break;
        }

        return Result.Success();
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
        // R1: Exactly one site, valid IANA tz
        if (string.IsNullOrWhiteSpace(site.DefaultTimezone))
            return RegistryErrors.InvariantViolation("R1", "DefaultTimezone must not be empty.");
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(site.DefaultTimezone);
        }
        catch
        {
            return RegistryErrors.InvariantViolation("R1", $"Invalid IANA timezone '{site.DefaultTimezone}'.");
        }

        // R2: Exactly one root location, no cycles, valid parents, max depth <= 16
        var rootCount = locations.Values.Count(l => l.ParentId == null);
        if (rootCount != 1)
            return RegistryErrors.InvariantViolation("R2", $"Expected exactly one root location, found {rootCount}.");

        foreach (var loc in locations.Values)
        {
            if (loc.ParentId != null && !locations.ContainsKey(loc.ParentId.Value))
                return RegistryErrors.InvariantViolation("R2", $"Location {loc.Id} references non-existent parent {loc.ParentId}.");

            if (loc.Depth > 16)
                return RegistryErrors.InvariantViolation("R2", $"Location {loc.Id} exceeds max depth 16.");

            // Cycle detection
            var curr = loc;
            var visited = new HashSet<LocationId> { curr.Id };
            while (curr.ParentId != null)
            {
                if (visited.Contains(curr.ParentId.Value))
                    return RegistryErrors.InvariantViolation("R2", $"Cycle detected at location {loc.Id}.");
                visited.Add(curr.ParentId.Value);
                if (!locations.TryGetValue(curr.ParentId.Value, out var parent))
                    break;
                curr = parent;
            }
        }

        // R3: Tag.Key unique; tag in an exclusive family holds at most one tag of that family
        var zeroOrOneFamilies = tagFamilies.Values.Where(f => f.Cardinality == TagCardinality.ZeroOrOne).Select(f => f.Id).ToHashSet();
        foreach (var dev in devices.Values)
        {
            var familyCounts = new Dictionary<TagFamilyId, int>();
            foreach (var tid in dev.Tags)
            {
                if (!tags.TryGetValue(tid, out var t))
                    return RegistryErrors.InvariantViolation("R3", $"Device {dev.Id} references non-existent tag {tid}.");

                if (zeroOrOneFamilies.Contains(t.FamilyId))
                {
                    familyCounts[t.FamilyId] = familyCounts.GetValueOrDefault(t.FamilyId) + 1;
                    if (familyCounts[t.FamilyId] > 1)
                        return RegistryErrors.InvariantViolation("R3", $"Tag family {t.FamilyId} allows at most one tag per device.");
                }
            }
        }

        // R4: Server.Type and Device.Type resolve in MOD with role server / device
        foreach (var srv in servers.Values)
        {
            var dtRes = _typeCatalog.Get(srv.Type);
            if (!dtRes.IsSuccess)
                return RegistryErrors.InvariantViolation("R4", $"Server type '{srv.Type.Id}@{srv.Type.Version}' not found in catalog.");
        }
        foreach (var dev in devices.Values)
        {
            var dtRes = _typeCatalog.Get(dev.Type);
            if (!dtRes.IsSuccess)
                return RegistryErrors.InvariantViolation("R4", $"Device type '{dev.Type.Id}@{dev.Type.Version}' not found in catalog.");
        }

        // R5: (ServerId, NativeId) unique per device; (ConnectionId, NativeId) unique per server
        var serverSeen = new HashSet<(ConnectionId, string)>();
        foreach (var srv in servers.Values)
        {
            if (!serverSeen.Add((srv.ConnectionId, srv.NativeId)))
                return RegistryErrors.InvariantViolation("R5", $"Duplicate server native ID '{srv.NativeId}' for connection {srv.ConnectionId}.");
        }
        var devSeen = new HashSet<(ServerId, string)>();
        foreach (var dev in devices.Values)
        {
            if (!devSeen.Add((dev.ServerId, dev.NativeId)))
                return RegistryErrors.InvariantViolation("R5", $"Duplicate device native ID '{dev.NativeId}' for server {dev.ServerId}.");
        }

        // R6: Device parent belongs to same server; parent AllowedChildTypes; no cycles; depth <= 4
        foreach (var dev in devices.Values)
        {
            if (dev.ParentId != null)
            {
                if (!devices.TryGetValue(dev.ParentId.Value, out var parentDev))
                    return RegistryErrors.InvariantViolation("R6", $"Parent device {dev.ParentId} not found.");

                if (parentDev.ServerId != dev.ServerId)
                    return RegistryErrors.InvariantViolation("R6", "Parent device belongs to a different server.");

                // cycle check & depth
                var cur = dev;
                var depth = 0;
                var visited = new HashSet<DeviceId> { cur.Id };
                while (cur.ParentId != null)
                {
                    depth++;
                    if (depth > 4)
                        return RegistryErrors.InvariantViolation("R6", "Device nesting exceeds depth 4.");
                    if (visited.Contains(cur.ParentId.Value))
                        return RegistryErrors.InvariantViolation("R6", "Cycle detected in device parents.");
                    visited.Add(cur.ParentId.Value);
                    if (!devices.TryGetValue(cur.ParentId.Value, out var nextParent))
                        break;
                    cur = nextParent;
                }
            }
        }

        // R7: Server parent belongs to same connection; depth <= 3
        foreach (var srv in servers.Values)
        {
            if (srv.ParentId != null)
            {
                if (!servers.TryGetValue(srv.ParentId.Value, out var parentSrv))
                    return RegistryErrors.InvariantViolation("R7", $"Parent server {srv.ParentId} not found.");

                if (parentSrv.ConnectionId != srv.ConnectionId)
                    return RegistryErrors.InvariantViolation("R7", "Parent server belongs to a different connection.");

                var cur = srv;
                var depth = 0;
                var visited = new HashSet<ServerId> { cur.Id };
                while (cur.ParentId != null)
                {
                    depth++;
                    if (depth > 3)
                        return RegistryErrors.InvariantViolation("R7", "Server nesting exceeds depth 3.");
                    if (visited.Contains(cur.ParentId.Value))
                        return RegistryErrors.InvariantViolation("R7", "Cycle detected in server parents.");
                    visited.Add(cur.ParentId.Value);
                    if (!servers.TryGetValue(cur.ParentId.Value, out var nextParent))
                        break;
                    cur = nextParent;
                }
            }
        }

        // R8: Custom field target, key unique per target, required custom fields
        var cfSeen = new HashSet<(CustomFieldTarget, string)>();
        foreach (var cf in customFields.Values)
        {
            if (!cfSeen.Add((cf.Target, cf.Key)))
                return RegistryErrors.InvariantViolation("R8", $"Duplicate custom field key '{cf.Key}' for target {cf.Target}.");
        }

        var requiredDeviceCfs = customFields.Values.Where(cf => cf.Target == CustomFieldTarget.Device && cf.Required).ToList();
        foreach (var dev in devices.Values)
        {
            foreach (var reqCf in requiredDeviceCfs)
            {
                if (!dev.CustomFields.ContainsKey(reqCf.Id))
                    return RegistryErrors.InvariantViolation("R8", $"Device {dev.Id} missing required custom field '{reqCf.Key}'.");
            }
        }

        // R9: Link endpoints exist; no duplicate (role, from, to)
        var linkSeen = new HashSet<(DeviceId, string, DeviceId)>();
        foreach (var l in links)
        {
            if (l.SourceDeviceId == l.TargetDeviceId)
                return RegistryErrors.InvariantViolation("R9", $"Self-links are forbidden (source == target: {l.SourceDeviceId}).");

            if (!devices.ContainsKey(l.SourceDeviceId))
                return RegistryErrors.InvariantViolation("R9", $"Link source device {l.SourceDeviceId} does not exist.");

            if (!devices.ContainsKey(l.TargetDeviceId))
                return RegistryErrors.InvariantViolation("R9", $"Link target device {l.TargetDeviceId} does not exist.");

            if (!linkSeen.Add((l.SourceDeviceId, l.Role, l.TargetDeviceId)))
                return RegistryErrors.InvariantViolation("R9", $"Duplicate link ({l.SourceDeviceId}, {l.Role}, {l.TargetDeviceId}).");
        }

        // R10: Every device / server has a location
        foreach (var srv in servers.Values)
        {
            if (!locations.ContainsKey(srv.LocationId))
                return RegistryErrors.InvariantViolation("R10", $"Server {srv.Id} references non-existent location {srv.LocationId}.");
        }
        foreach (var dev in devices.Values)
        {
            if (!locations.ContainsKey(dev.LocationId))
                return RegistryErrors.InvariantViolation("R10", $"Device {dev.Id} references non-existent location {dev.LocationId}.");
            if (!servers.ContainsKey(dev.ServerId))
                return RegistryErrors.InvariantViolation("R10", $"Device {dev.Id} references non-existent server {dev.ServerId}.");
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
        Site site,
        Dictionary<LocationId, Location> locations,
        Dictionary<TagFamilyId, TagFamily> tagFamilies,
        Dictionary<TagId, Tag> tags,
        Dictionary<ServerId, Server> servers,
        Dictionary<DeviceId, Device> devices,
        List<DeviceLink> links,
        Dictionary<CustomFieldId, CustomFieldDefinition> customFields,
        Dictionary<LocationId, (string OldPath, string NewPath)> touchedLocationPaths,
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

        // 2. Locations
        foreach (var (locId, paths) in touchedLocationPaths)
        {
            var lRecord = await db.Locations.AsTracking().FirstOrDefaultAsync(r => r.Id == locId.Value, ct);
            if (lRecord != null)
            {
                var loc = locations[locId];
                lRecord.Path = loc.Path;
                lRecord.Depth = loc.Depth;
                lRecord.RowVersion = loc.Version;
                lRecord.UpdatedAt = now;
                db.Locations.Update(lRecord);
            }
        }

        var locationGroups = batch.Changes
            .OfType<RegistryChange.UpsertLocation>()
            .Select(ul => locations[ul.Location.Id])
            .GroupBy(l => l.Depth)
            .OrderBy(g => g.Key);

        foreach (var group in locationGroups)
        {
            foreach (var loc in group)
            {
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

        // 4. Servers
        foreach (var usrv in batch.Changes.OfType<RegistryChange.UpsertServer>())
        {
            var srv = servers[usrv.Server.Id];
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

        // 5. Devices
        foreach (var udev in batch.Changes.OfType<RegistryChange.UpsertDevice>())
        {
            var dev = devices[udev.Device.Id];
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

        foreach (var pdev in batch.Changes.OfType<RegistryChange.PatchDevice>())
        {
            var pDev = devices[pdev.Id];
            var epd = await db.Devices.AsTracking().FirstOrDefaultAsync(r => r.Id == pDev.Id.Value, ct);
            if (epd != null)
            {
                epd.DisplayName = pDev.DisplayName != null ? JsonSerializer.Serialize(pDev.DisplayName) : null;
                epd.LocationId = pDev.LocationId.Value;
                epd.Tags = pDev.Tags.Select(t => t.Value).ToArray();
                epd.Attributes = JsonSerializer.Serialize(pDev.Attributes);
                epd.CustomFields = JsonSerializer.Serialize(pDev.CustomFields);
                epd.UnsupportedCaps = pDev.UnsupportedCaps.Select(c => c.ToString()).ToArray();
                epd.Geometry = pDev.Geometry != null ? JsonSerializer.Serialize(pDev.Geometry) : null;
                epd.Enabled = pDev.Enabled;
                epd.Maintenance = pDev.Maintenance;
                epd.RowVersion = pDev.Version;
                epd.UpdatedAt = now;
                db.Devices.Update(epd);
            }
        }
        await db.SaveChangesAsync(ct);

        // 6. Device Links
        foreach (var udl in batch.Changes.OfType<RegistryChange.UpsertDeviceLink>())
        {
            var link = udl.Link;
            var elink = await db.DeviceLinks.AsTracking().FirstOrDefaultAsync(r =>
                r.SourceDeviceId == link.SourceDeviceId.Value &&
                r.Role == link.Role &&
                r.TargetDeviceId == link.TargetDeviceId.Value, ct);
            if (elink == null)
            {
                db.DeviceLinks.Add(new DeviceLinkRecord
                {
                    SourceDeviceId = link.SourceDeviceId.Value,
                    Role = link.Role,
                    TargetDeviceId = link.TargetDeviceId.Value,
                    Metadata = JsonSerializer.Serialize(link.Metadata),
                    RowVersion = link.Version,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                elink.Metadata = JsonSerializer.Serialize(link.Metadata);
                elink.RowVersion = link.Version;
                elink.UpdatedAt = now;
                db.DeviceLinks.Update(elink);
            }
        }

        // 7. Custom Fields
        foreach (var ucf in batch.Changes.OfType<RegistryChange.UpsertCustomField>())
        {
            var cf = customFields[ucf.CustomField.Id];
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

        // 8. Deletions (reverse dependency order: links, devices, servers, tags, tag families, custom fields, locations)
        foreach (var ddl in batch.Changes.OfType<RegistryChange.DeleteDeviceLink>())
        {
            var delLink = await db.DeviceLinks.AsTracking().FirstOrDefaultAsync(r =>
                r.SourceDeviceId == ddl.SourceDeviceId.Value &&
                r.Role == ddl.Role &&
                r.TargetDeviceId == ddl.TargetDeviceId.Value, ct);
            if (delLink != null) db.DeviceLinks.Remove(delLink);
        }
        foreach (var ddev in batch.Changes.OfType<RegistryChange.DeleteDevice>())
        {
            var delDev = await db.Devices.AsTracking().FirstOrDefaultAsync(r => r.Id == ddev.Id.Value, ct);
            if (delDev != null) db.Devices.Remove(delDev);
        }
        foreach (var dsrv in batch.Changes.OfType<RegistryChange.DeleteServer>())
        {
            var delSrv = await db.Servers.AsTracking().FirstOrDefaultAsync(r => r.Id == dsrv.Id.Value, ct);
            if (delSrv != null) db.Servers.Remove(delSrv);
        }
        foreach (var dt in batch.Changes.OfType<RegistryChange.DeleteTag>())
        {
            var delTag = await db.Tags.AsTracking().FirstOrDefaultAsync(r => r.Id == dt.Id.Value, ct);
            if (delTag != null) db.Tags.Remove(delTag);
        }
        foreach (var dtf in batch.Changes.OfType<RegistryChange.DeleteTagFamily>())
        {
            var delTf = await db.TagFamilies.AsTracking().FirstOrDefaultAsync(r => r.Id == dtf.Id.Value, ct);
            if (delTf != null) db.TagFamilies.Remove(delTf);
        }
        foreach (var dcf in batch.Changes.OfType<RegistryChange.DeleteCustomField>())
        {
            var delCf = await db.CustomFields.AsTracking().FirstOrDefaultAsync(r => r.Id == dcf.Id.Value, ct);
            if (delCf != null) db.CustomFields.Remove(delCf);
        }
        foreach (var dl in batch.Changes.OfType<RegistryChange.DeleteLocation>())
        {
            var delLoc = await db.Locations.AsTracking().FirstOrDefaultAsync(r => r.Id == dl.Id.Value, ct);
            if (delLoc != null) db.Locations.Remove(delLoc);
        }
    }
}
