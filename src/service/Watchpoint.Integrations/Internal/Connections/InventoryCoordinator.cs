using System.Globalization;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Integrations.Internal.Connections;

/// <summary>
/// Converts a connector's <see cref="InventorySnapshot"/> to the registry's <see cref="InventoryReport"/> (spec IRT §7.4)
/// and reconciles it. Items that cannot be mapped (unknown type, invalid capability id) are skipped with the warning
/// <c>irt.inventory.type_unknown</c>; a report with a skipped item is sent as partial, so the registry never orphans
/// an existing server or device because the runtime could not map it.
/// </summary>
internal sealed class InventoryCoordinator(IInventoryReconciler reconciler, ITypeCatalog types, ILogger<InventoryCoordinator> logger)
{
    /// <summary>The report and the warnings of the items skipped.</summary>
    internal sealed record Mapped(InventoryReport Report, IReadOnlyList<ReconcileWarning> Warnings);

    public Result<Mapped> Map(ConnectionId connection, InventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var valid = snapshot.Validate();
        if (!valid.IsSuccess)
        {
            return Error.Validation(IntegrationErrors.InventoryInvalid, valid.Error.FieldErrors ?? []);
        }

        var warnings = new List<ReconcileWarning>();
        var servers = new List<InventoryServer>(snapshot.Servers.Count);
        foreach (var item in snapshot.Servers)
        {
            if (Resolve(connection, item.NativeId, item.TypeId, item.TypeVersion, warnings) is { } type)
            {
                servers.Add(new InventoryServer(item.NativeId, item.ParentNativeId, type, item.VendorName, item.VendorAttributes));
            }
        }

        var devices = new List<InventoryDevice>(snapshot.Devices.Count);
        foreach (var item in snapshot.Devices)
        {
            if (Resolve(connection, item.NativeId, item.TypeId, item.TypeVersion, warnings) is not { } type)
            {
                continue;
            }

            var capabilities = new HashSet<CapabilityId>();
            var invalid = item.UnsupportedOptionalCapabilities.FirstOrDefault(c => !CapabilityId.Parse(c).TryGetValue(out _, out _));
            if (invalid is not null)
            {
                warnings.Add(Warning(item.NativeId, item.TypeId, item.TypeVersion, ("capability", invalid)));
                IntegrationLog.InventoryTypeUnknown(logger, connection.Value, item.NativeId, item.TypeId, item.TypeVersion);
                continue;
            }

            capabilities.UnionWith(item.UnsupportedOptionalCapabilities.Select(CapabilityId.FromTrusted));
            devices.Add(new InventoryDevice(item.NativeId, item.ServerNativeId, item.ParentNativeId, type, item.VendorName, item.VendorAttributes,
                capabilities, [.. item.Links.Select(l => new InventoryLink(l.Kind, l.ToNativeId, l.ToServerNativeId))]));
        }

        // A skipped item must not read as "missing": the report is then partial (REG §8 never orphans on a partial report).
        return new Mapped(new InventoryReport(servers, devices, snapshot.IsComplete && warnings.Count == 0), warnings);
    }

    /// <summary>Maps the snapshot and reconciles it with the connection's options; the skipped items are added to the warnings.</summary>
    public async Task<Result<ReconciliationResult>> ReconcileAsync(Connection connection, InventorySnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!Map(connection.Id, snapshot).TryGetValue(out var mapped, out var error))
        {
            IntegrationLog.ReconcileFailed(logger, connection.Id.Value, error.Code.Value);
            return error;
        }

        var reconciled = await reconciler.ReconcileAsync(connection.Id, mapped.Report, new ReconcileOptions(connection.AutoImport, connection.LocationId), ct)
            .ConfigureAwait(false);
        if (!reconciled.TryGetValue(out var result, out error))
        {
            IntegrationLog.ReconcileFailed(logger, connection.Id.Value, error.Code.Value);
            return error;
        }

        result = result with { Warnings = [.. mapped.Warnings, .. result.Warnings] };
        if (logger.IsEnabled(LogLevel.Information))
        {
            var summary = string.Create(CultureInfo.InvariantCulture,
            $"unchanged {result.Unchanged}, updated {result.Updated}, orphaned {result.Orphaned}, restored {result.Restored}, new {result.NewDiscovered}, imported {result.Imported}, warnings {result.Warnings.Count}{(mapped.Report.IsComplete ? string.Empty : " (partial)")}");
            IntegrationLog.Reconciled(logger, connection.Id.Value, summary);
        }

        return result;
    }

    private TypeRef? Resolve(ConnectionId connection, string nativeId, string typeId, string typeVersion, List<ReconcileWarning> warnings)
    {
        if (TypeId.Parse(typeId).TryGetValue(out var id, out _))
        {
            if (SemVersion.TryParse(typeVersion, out var exact) && types.Get(new TypeRef(id, exact)).TryGetValue(out var definition, out _))
            {
                return definition.Ref;
            }

            // Connectors name versions loosely ("1.0", "1"): the latest version of that major.
            var major = typeVersion.Split('.')[0];
            if (int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && types.GetLatest(id, m).TryGetValue(out definition, out _))
            {
                return definition.Ref;
            }
        }

        warnings.Add(Warning(nativeId, typeId, typeVersion));
        IntegrationLog.InventoryTypeUnknown(logger, connection.Value, nativeId, typeId, typeVersion);
        return null;
    }

    private static ReconcileWarning Warning(string nativeId, string typeId, string typeVersion, params (string Key, string Value)[] extra)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["typeId"] = typeId, ["typeVersion"] = typeVersion };
        foreach (var (key, value) in extra)
        {
            args[key] = value;
        }

        return new ReconcileWarning(IntegrationErrors.InventoryTypeUnknown, nativeId, args);
    }
}
