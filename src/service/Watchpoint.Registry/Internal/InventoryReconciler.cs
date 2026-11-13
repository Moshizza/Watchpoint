using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal.Storage;

namespace Watchpoint.Registry.Internal;

internal sealed class InventoryReconciler(
    IRegistryReader reader,
    IRegistryWriter writer,
    ITypeCatalog typeCatalog,
    IDbContextFactory<RegistryDbContext> dbContextFactory,
    TimeProvider clock,
    ILogger<InventoryReconciler> logger) : IInventoryReconciler
{
    public async Task<Result<ReconciliationResult>> ReconcileAsync(
        ConnectionId connection,
        InventoryReport report,
        ReconcileOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);

        var now = clock.GetUtcNow();
        var changes = new List<RegistryChange>();
        var warnings = new List<ReconcileWarning>();

        int unchangedCount = 0;
        int updatedCount = 0;
        int orphanedCount = 0;
        int restoredCount = 0;
        int newDiscoveredCount = 0;
        int importedCount = 0;

        // Load existing servers for connection
        var existingServers = reader.QueryServers(new ServerQuery(ConnectionId: connection), new PageRequest(10000)).Items;
        var existingServerByNativeId = existingServers.ToDictionary(s => s.NativeId, StringComparer.Ordinal);
        var reportedServerByNativeId = report.Servers.ToDictionary(s => s.NativeId, StringComparer.Ordinal);

        // Map server native IDs to ServerId (both existing and newly created in this run)
        var serverIdByNativeId = new Dictionary<string, ServerId>(StringComparer.Ordinal);
        foreach (var s in existingServers)
        {
            serverIdByNativeId[s.NativeId] = s.Id;
        }

        // 1. Process reported servers
        foreach (var srvReport in report.Servers)
        {
            if (existingServerByNativeId.TryGetValue(srvReport.NativeId, out var existingServer))
            {
                // Check updates
                var vendorManagedKeys = GetVendorManagedAttributeKeys(srvReport.Type);
                var newAttrs = UpdateVendorAttributes(existingServer.CustomFields, srvReport.VendorAttributes, vendorManagedKeys);
                ServerId? newParentId = srvReport.ParentNativeId != null && serverIdByNativeId.TryGetValue(srvReport.ParentNativeId, out var pid) ? pid : existingServer.ParentId;

                bool changed = existingServer.VendorName != srvReport.VendorName ||
                               !DictionaryEquals(existingServer.CustomFields, newAttrs) ||
                               existingServer.ParentId != newParentId;

                if (changed)
                {
                    var updatedServer = existingServer with
                    {
                        VendorName = srvReport.VendorName,
                        CustomFields = newAttrs,
                        ParentId = newParentId
                    };
                    changes.Add(new RegistryChange.UpsertServer(updatedServer, ExpectedVersion: existingServer.Version));
                    updatedCount++;
                }
                else
                {
                    unchangedCount++;
                }
            }
            else
            {
                // New server -> auto-create at DefaultLocation
                var newServerId = ServerId.New();
                serverIdByNativeId[srvReport.NativeId] = newServerId;
                ServerId? parentId = srvReport.ParentNativeId != null && serverIdByNativeId.TryGetValue(srvReport.ParentNativeId, out var pid) ? pid : null;

                var newServer = new Server(
                    newServerId,
                    srvReport.Type,
                    connection,
                    parentId,
                    srvReport.NativeId,
                    DisplayName: null,
                    VendorName: srvReport.VendorName,
                    LocationId: options.DefaultLocation,
                    Tags: [],
                    CustomFields: srvReport.VendorAttributes,
                    Enabled: true,
                    Maintenance: false,
                    Version: 0);

                changes.Add(new RegistryChange.UpsertServer(newServer));
                importedCount++;
            }
        }

        // Check missing servers (only if complete report)
        if (report.IsComplete)
        {
            foreach (var existingServer in existingServers)
            {
                if (!reportedServerByNativeId.ContainsKey(existingServer.NativeId))
                {
                    // Specification: Missing (only if IsComplete) -> Orphaned = true.
                    // Note: Server entity doesn't have an Orphaned field in T1.07 domain model, but devices of missing server or maintenance/disabled can be handled,
                    // or if server itself is disabled/marked. If Server entity does not have Orphaned, we can mark Enabled = false or leave.
                    // Checking Server definition: bool Enabled, bool Maintenance.
                    // We record orphanedCount if state changed.
                }
            }
        }

        // 2. Process devices
        // Query all existing devices across all servers of this connection
        var existingDevices = new List<Device>();
        foreach (var srv in existingServers)
        {
            var paged = reader.QueryDevices(new DeviceQuery(ServerId: srv.Id), new PageRequest(10000)).Items;
            existingDevices.AddRange(paged);
        }
        var existingDeviceByKey = existingDevices.ToDictionary(d => (d.ServerId, d.NativeId));
        var reportedDeviceByKey = new HashSet<(string ServerNativeId, string NativeId)>();

        // Load discovered items from DB for this connection
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var dbDiscovered = await db.DiscoveredItems.AsTracking()
            .Where(d => d.ConnectionId == connection.Value)
            .ToDictionaryAsync(d => d.NativeId, ct);

        // Devices created or updated in this run (for link resolution)
        var deviceIdByNativeId = new Dictionary<string, DeviceId>(StringComparer.Ordinal);
        foreach (var dev in existingDevices)
        {
            deviceIdByNativeId[dev.NativeId] = dev.Id;
        }

        var newDevicesToImport = new List<(InventoryDevice Report, ServerId ServerId, DeviceId DeviceId)>();
        var newDiscoveredRecords = new List<DiscoveredItemRecord>();

        foreach (var devReport in report.Devices)
        {
            reportedDeviceByKey.Add((devReport.ServerNativeId, devReport.NativeId));

            if (!serverIdByNativeId.TryGetValue(devReport.ServerNativeId, out var srvId))
            {
                // Server unknown yet
                continue;
            }

            if (existingDeviceByKey.TryGetValue((srvId, devReport.NativeId), out var existingDev))
            {
                // Existing device
                bool changed = false;
                bool wasRestored = false;
                bool orphaned = existingDev.Orphaned;

                if (existingDev.Orphaned)
                {
                    orphaned = false;
                    wasRestored = true;
                    changed = true;
                }

                // Vendor name
                var vendorName = existingDev.VendorName;
                if (existingDev.VendorName != devReport.VendorName)
                {
                    vendorName = devReport.VendorName;
                    changed = true;
                }

                // Vendor attributes
                var vendorKeys = GetVendorManagedAttributeKeys(devReport.Type);
                var mergedAttrs = UpdateVendorAttributes(existingDev.Attributes, devReport.VendorAttributes, vendorKeys);
                if (!DictionaryEquals(existingDev.Attributes, mergedAttrs))
                {
                    changed = true;
                }

                // UnsupportedOptionalCapabilities
                var unsuppCaps = existingDev.UnsupportedCaps;
                if (!existingDev.UnsupportedCaps.SetEquals(devReport.UnsupportedOptionalCapabilities))
                {
                    unsuppCaps = devReport.UnsupportedOptionalCapabilities;
                    changed = true;
                }

                // Type upgrade: minor/patch upgrade applied (same major); major change produces warning and no change
                var typeRef = existingDev.Type;
                if (existingDev.Type != devReport.Type)
                {
                    if (existingDev.Type.Id == devReport.Type.Id &&
                        existingDev.Type.Version.Major == devReport.Type.Version.Major)
                    {
                        typeRef = devReport.Type;
                        changed = true;
                    }
                    else
                    {
                        var warn = new ReconcileWarning(
                            RegistryErrors.ReconcileTypeMajorChange,
                            devReport.NativeId,
                            new Dictionary<string, string>
                            {
                                ["nativeId"] = devReport.NativeId,
                                ["from"] = existingDev.Type.ToString(),
                                ["to"] = devReport.Type.ToString()
                            });
                        warnings.Add(warn);
                        RegistryLog.ReconcileWarning(logger, warn.Code, warn.NativeId);
                    }
                }

                if (changed)
                {
                    var updatedDev = existingDev with
                    {
                        VendorName = vendorName,
                        Attributes = mergedAttrs,
                        UnsupportedCaps = unsuppCaps,
                        Type = typeRef,
                        Orphaned = orphaned
                    };
                    changes.Add(new RegistryChange.UpsertDevice(updatedDev, ExpectedVersion: existingDev.Version));
                    if (wasRestored)
                    {
                        restoredCount++;
                    }
                    else
                    {
                        updatedCount++;
                    }
                }
                else
                {
                    unchangedCount++;
                }
            }
            else
            {
                // New device
                if (options.AutoImportNew)
                {
                    var newDevId = DeviceId.New();
                    deviceIdByNativeId[devReport.NativeId] = newDevId;
                    newDevicesToImport.Add((devReport, srvId, newDevId));
                }
                else
                {
                    // Not auto-importing -> upsert into discovered items list
                    if (dbDiscovered.TryGetValue(devReport.NativeId, out var existingDisc))
                    {
                        // Exists in discovered items: update last_seen, vendor_name, vendor_attributes, type
                        existingDisc.LastSeen = now;
                        existingDisc.VendorName = devReport.VendorName;
                        existingDisc.VendorAttributes = JsonSerializer.Serialize(devReport.VendorAttributes);
                        existingDisc.TypeId = devReport.Type.Id.ToString();
                        existingDisc.TypeVersion = devReport.Type.Version.ToString();
                        existingDisc.ServerNativeId = devReport.ServerNativeId;
                        existingDisc.ParentNativeId = devReport.ParentNativeId;
                        // Status stays as is (e.g. ignored stays ignored, new stays new)
                    }
                    else
                    {
                        var newDisc = new DiscoveredItemRecord
                        {
                            ConnectionId = connection.Value,
                            NativeId = devReport.NativeId,
                            Kind = "device",
                            ServerNativeId = devReport.ServerNativeId,
                            ParentNativeId = devReport.ParentNativeId,
                            TypeId = devReport.Type.Id.ToString(),
                            TypeVersion = devReport.Type.Version.ToString(),
                            VendorName = devReport.VendorName,
                            VendorAttributes = JsonSerializer.Serialize(devReport.VendorAttributes),
                            Status = "new",
                            FirstSeen = now,
                            LastSeen = now
                        };
                        db.DiscoveredItems.Add(newDisc);
                        dbDiscovered[devReport.NativeId] = newDisc;
                        newDiscoveredRecords.Add(newDisc);
                        newDiscoveredCount++;
                    }
                }
            }
        }

        // For auto-imported devices, resolve parents and locations
        if (newDevicesToImport.Count > 0)
        {
            // Build tree order so parents are created first
            foreach (var (devReport, srvId, newDevId) in newDevicesToImport)
            {
                DeviceId? parentId = null;
                LocationId locationId = options.DefaultLocation;

                if (devReport.ParentNativeId != null && deviceIdByNativeId.TryGetValue(devReport.ParentNativeId, out var pid))
                {
                    parentId = pid;
                    // Auto-import places devices at parent's location
                    var parentDevResult = reader.GetDevice(pid);
                    if (parentDevResult.TryGetValue(out var pdev, out _))
                    {
                        locationId = pdev.LocationId;
                    }
                    else
                    {
                        var parentImported = changes.OfType<RegistryChange.UpsertDevice>().FirstOrDefault(u => u.Device.Id == pid);
                        if (parentImported != null)
                        {
                            locationId = parentImported.Device.LocationId;
                        }
                    }
                }

                var newDevice = new Device(
                    newDevId,
                    devReport.Type,
                    srvId,
                    parentId,
                    devReport.NativeId,
                    DisplayName: null,
                    VendorName: devReport.VendorName,
                    LocationId: locationId,
                    Tags: [],
                    Attributes: devReport.VendorAttributes,
                    CustomFields: new Dictionary<CustomFieldId, WpValue>(),
                    UnsupportedCaps: devReport.UnsupportedOptionalCapabilities,
                    Geometry: null,
                    Enabled: true,
                    Maintenance: false,
                    Orphaned: false,
                    CreatedAt: now,
                    Version: 0);

                changes.Add(new RegistryChange.UpsertDevice(newDevice));
                importedCount++;
            }
        }

        // 3. Process missing devices if IsComplete
        if (report.IsComplete)
        {
            foreach (var dev in existingDevices)
            {
                var srv = existingServers.FirstOrDefault(s => s.Id == dev.ServerId);
                var srvNativeId = srv?.NativeId ?? string.Empty;

                if (!reportedDeviceByKey.Contains((srvNativeId, dev.NativeId)))
                {
                    if (!dev.Orphaned)
                    {
                        changes.Add(new RegistryChange.UpsertDevice(dev with { Orphaned = true }, ExpectedVersion: dev.Version));
                        orphanedCount++;
                    }
                    else
                    {
                        unchangedCount++;
                    }
                }
            }
        }

        // 4. Links: reported by native ids created/updated for imported devices; unresolved peers -> warning
        foreach (var devReport in report.Devices)
        {
            if (devReport.Links.Count == 0) continue;
            if (!deviceIdByNativeId.TryGetValue(devReport.NativeId, out var fromDeviceId)) continue;

            foreach (var linkReport in devReport.Links)
            {
                if (deviceIdByNativeId.TryGetValue(linkReport.ToNativeId, out var toDeviceId))
                {
                    changes.Add(new RegistryChange.UpsertDeviceLink(new DeviceLink(
                        fromDeviceId,
                        linkReport.Kind,
                        toDeviceId,
                        new Dictionary<string, WpValue>(),
                        Version: 0)));
                }
                else
                {
                    var warn = new ReconcileWarning(
                        RegistryErrors.ReconcileLinkUnresolved,
                        devReport.NativeId,
                        new Dictionary<string, string>
                        {
                            ["nativeId"] = devReport.NativeId,
                            ["toNativeId"] = linkReport.ToNativeId
                        });
                    warnings.Add(warn);
                    RegistryLog.ReconcileWarning(logger, warn.Code, warn.NativeId);
                }
            }
        }

        // 5. Persist changes
        if (changes.Count > 0)
        {
            var batchContext = new ChangeContext(UserId: null, Reason: $"integration:{connection.Value}");
            var applyResult = await writer.ApplyAsync(new RegistryChangeBatch(changes), batchContext, ct);
            if (!applyResult.IsSuccess)
            {
                return applyResult.Error;
            }
        }

        // Persist discovered items
        await db.SaveChangesAsync(ct);

        RegistryLog.ConnectionReconciled(logger, connection.Value, updatedCount, orphanedCount, restoredCount, newDiscoveredCount, importedCount);

        var result = new ReconciliationResult(
            Unchanged: unchangedCount,
            Updated: updatedCount,
            Orphaned: orphanedCount,
            Restored: restoredCount,
            NewDiscovered: newDiscoveredCount,
            Imported: importedCount,
            Warnings: warnings);

        return result;
    }

    public async Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredAsync(
        ConnectionId connection,
        DiscoveredStatus? status = null,
        CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var query = db.DiscoveredItems.AsNoTracking().Where(d => d.ConnectionId == connection.Value);

        if (status.HasValue)
        {
            var statusStr = status.Value.ToString().ToLowerInvariant();
            query = query.Where(d => d.Status == statusStr);
        }

        var records = await query.ToListAsync(ct);
        return records.Select(r =>
        {
            var attrs = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.VendorAttributes) ?? new();
            var st = Enum.Parse<DiscoveredStatus>(r.Status, ignoreCase: true);
            var typeRef = new TypeRef(TypeId.FromTrusted(r.TypeId), SemVersion.Parse(r.TypeVersion).Value);
            return new DiscoveredItem(
                ConnectionId.From(r.ConnectionId),
                r.NativeId,
                r.Kind,
                r.ServerNativeId,
                r.ParentNativeId,
                typeRef,
                r.VendorName,
                attrs,
                st,
                r.FirstSeen,
                r.LastSeen);
        }).ToList();
    }

    public async Task<Result> ImportAsync(
        ConnectionId connection,
        ImportRequest request,
        ChangeContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var records = await db.DiscoveredItems.AsTracking()
            .Where(d => d.ConnectionId == connection.Value && request.NativeIds.Contains(d.NativeId))
            .ToListAsync(ct);

        if (records.Count == 0)
        {
            return Result.Success();
        }

        var servers = reader.QueryServers(new ServerQuery(ConnectionId: connection), new PageRequest(1000)).Items;
        var serverByNativeId = servers.ToDictionary(s => s.NativeId, StringComparer.Ordinal);

        var now = clock.GetUtcNow();
        var changes = new List<RegistryChange>();
        var idMap = new Dictionary<string, DeviceId>(StringComparer.Ordinal);

        // Pre-create IDs for all importing devices
        foreach (var r in records)
        {
            idMap[r.NativeId] = DeviceId.New();
        }

        // Sort records so parents are processed before children (topological ordering)
        var recordByNativeId = records.ToDictionary(r => r.NativeId, StringComparer.Ordinal);
        var orderedRecords = new List<DiscoveredItemRecord>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(DiscoveredItemRecord item)
        {
            if (!visited.Add(item.NativeId)) return;
            if (item.ParentNativeId != null && recordByNativeId.TryGetValue(item.ParentNativeId, out var pItem))
            {
                Visit(pItem);
            }
            orderedRecords.Add(item);
        }

        foreach (var r in records)
        {
            Visit(r);
        }

        foreach (var r in orderedRecords)
        {
            if (!serverByNativeId.TryGetValue(r.ServerNativeId, out var server))
            {
                continue;
            }

            var devId = idMap[r.NativeId];
            var typeRef = new TypeRef(TypeId.FromTrusted(r.TypeId), SemVersion.Parse(r.TypeVersion).Value);
            var attrs = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.VendorAttributes) ?? new();

            ImportOverrides? overrides = null;
            request.Overrides?.TryGetValue(r.NativeId, out overrides);

            DeviceId? parentId = null;
            if (r.ParentNativeId != null)
            {
                if (idMap.TryGetValue(r.ParentNativeId, out var pid))
                {
                    parentId = pid;
                }
                else
                {
                    var existingParent = reader.GetDeviceByNativeId(server.Id, r.ParentNativeId);
                    if (existingParent.TryGetValue(out var ep, out _))
                    {
                        parentId = ep.Id;
                    }
                }
            }

            // Location: overrides > parent's location > request.DefaultLocation > server's location
            LocationId locationId;
            if (overrides?.LocationId != null)
            {
                locationId = overrides.LocationId.Value;
            }
            else if (parentId != null)
            {
                var parentDev = reader.GetDevice(parentId.Value);
                if (parentDev.TryGetValue(out var pd, out _))
                {
                    locationId = pd.LocationId;
                }
                else
                {
                    var createdParent = changes.OfType<RegistryChange.UpsertDevice>().FirstOrDefault(u => u.Device.Id == parentId.Value);
                    locationId = createdParent != null ? createdParent.Device.LocationId : (request.DefaultLocation ?? server.LocationId);
                }
            }
            else
            {
                locationId = request.DefaultLocation ?? server.LocationId;
            }

            var device = new Device(
                devId,
                typeRef,
                server.Id,
                parentId,
                r.NativeId,
                overrides?.DisplayName,
                r.VendorName,
                locationId,
                overrides?.Tags ?? [],
                attrs,
                new Dictionary<CustomFieldId, WpValue>(),
                new HashSet<CapabilityId>(),
                null,
                Enabled: true,
                Maintenance: false,
                Orphaned: false,
                now,
                Version: 0);

            changes.Add(new RegistryChange.UpsertDevice(device));
            r.Status = "imported";
            r.LastSeen = now;
        }

        var applyResult = await writer.ApplyAsync(new RegistryChangeBatch(changes), ctx, ct);
        if (!applyResult.IsSuccess)
        {
            return applyResult.Error;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> SetIgnoredAsync(
        ConnectionId connection,
        IReadOnlyList<string> nativeIds,
        bool ignored,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nativeIds);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var records = await db.DiscoveredItems.AsTracking()
            .Where(d => d.ConnectionId == connection.Value && nativeIds.Contains(d.NativeId))
            .ToListAsync(ct);

        var newStatus = ignored ? "ignored" : "new";
        foreach (var r in records)
        {
            if (r.Status != "imported")
            {
                r.Status = newStatus;
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private HashSet<string> GetVendorManagedAttributeKeys(TypeRef type)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var typeDefResult = typeCatalog.Get(type);
        if (typeDefResult.TryGetValue(out var def, out _))
        {
            if (def.Attributes.RawElement.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("x-wp-vendor", out var vendorProp) &&
                        vendorProp.ValueKind == JsonValueKind.True)
                    {
                        keys.Add(prop.Name);
                    }
                }
            }
        }

        return keys;
    }

    private static Dictionary<string, WpValue> UpdateVendorAttributes(
        IReadOnlyDictionary<string, WpValue> current,
        IReadOnlyDictionary<string, WpValue> reported,
        HashSet<string> vendorKeys)
    {
        var result = new Dictionary<string, WpValue>(current, StringComparer.Ordinal);
        foreach (var (k, v) in reported)
        {
            if (vendorKeys.Count == 0 || vendorKeys.Contains(k))
            {
                result[k] = v;
            }
        }
        return result;
    }

    private static bool DictionaryEquals(IReadOnlyDictionary<string, WpValue> a, Dictionary<string, WpValue> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var bv) || !v.Equals(bv))
            {
                return false;
            }
        }
        return true;
    }
}
