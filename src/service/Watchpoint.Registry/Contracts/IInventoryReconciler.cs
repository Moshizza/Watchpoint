using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Status of a discovered item in the discovery inventory.
/// </summary>
public enum DiscoveredStatus
{
    /// <summary>Item was newly discovered and awaits review or import.</summary>
    New,
    /// <summary>Item was ignored by administrator and will not be auto-imported.</summary>
    Ignored,
    /// <summary>Item was imported into active registry configuration.</summary>
    Imported
}

/// <summary>
/// Inventory report containing servers and devices reported by an integration connector.
/// </summary>
/// <param name="Servers">Servers in the inventory report.</param>
/// <param name="Devices">Devices in the inventory report.</param>
/// <param name="IsComplete">Whether this is a complete snapshot of the system.</param>
public sealed record InventoryReport(
    IReadOnlyList<InventoryServer> Servers,
    IReadOnlyList<InventoryDevice> Devices,
    bool IsComplete);

/// <summary>
/// Server item reported in an inventory report.
/// </summary>
/// <param name="NativeId">Native vendor identifier of the server.</param>
/// <param name="ParentNativeId">Native identifier of the parent server, or null.</param>
/// <param name="Type">Type reference of the server.</param>
/// <param name="VendorName">Vendor-reported display name.</param>
/// <param name="VendorAttributes">Vendor-managed attributes.</param>
public sealed record InventoryServer(
    string NativeId,
    string? ParentNativeId,
    TypeRef Type,
    string VendorName,
    IReadOnlyDictionary<string, WpValue> VendorAttributes);

/// <summary>
/// Device item reported in an inventory report.
/// </summary>
/// <param name="NativeId">Native vendor identifier of the device.</param>
/// <param name="ServerNativeId">Native identifier of the server managing this device.</param>
/// <param name="ParentNativeId">Native identifier of the parent device, or null.</param>
/// <param name="Type">Type reference of the device.</param>
/// <param name="VendorName">Vendor-reported display name.</param>
/// <param name="VendorAttributes">Vendor-managed attributes.</param>
/// <param name="UnsupportedOptionalCapabilities">Optional capabilities unsupported by this physical device.</param>
/// <param name="Links">Links to other devices reported by native identifiers.</param>
public sealed record InventoryDevice(
    string NativeId,
    string ServerNativeId,
    string? ParentNativeId,
    TypeRef Type,
    string VendorName,
    IReadOnlyDictionary<string, WpValue> VendorAttributes,
    IReadOnlySet<CapabilityId> UnsupportedOptionalCapabilities,
    IReadOnlyList<InventoryLink> Links);

/// <summary>
/// Link between devices defined by native identifiers.
/// </summary>
/// <param name="Kind">Role or kind of link.</param>
/// <param name="ToNativeId">Native identifier of the target device.</param>
/// <param name="ToServerNativeId">Native identifier of the target server, or null if same server.</param>
public sealed record InventoryLink(
    string Kind,
    string ToNativeId,
    string? ToServerNativeId = null);

/// <summary>
/// Options controlling inventory reconciliation behavior.
/// </summary>
/// <param name="AutoImportNew">Whether newly discovered devices should be automatically imported.</param>
/// <param name="DefaultLocation">Default location to place auto-imported devices or servers.</param>
public sealed record ReconcileOptions(
    bool AutoImportNew,
    LocationId DefaultLocation);

/// <summary>
/// Warning produced during inventory reconciliation.
/// </summary>
/// <param name="Code">Warning code identifier.</param>
/// <param name="NativeId">Native identifier of entity generating the warning.</param>
/// <param name="Args">Warning arguments and context details.</param>
public sealed record ReconcileWarning(
    string Code,
    string NativeId,
    IReadOnlyDictionary<string, string> Args);

/// <summary>
/// Result statistics and warnings of an inventory reconciliation run.
/// </summary>
/// <param name="Unchanged">Count of existing items that were unchanged.</param>
/// <param name="Updated">Count of existing items that were updated.</param>
/// <param name="Orphaned">Count of existing items marked orphaned.</param>
/// <param name="Restored">Count of previously orphaned items that were restored.</param>
/// <param name="NewDiscovered">Count of new devices added to discovered list.</param>
/// <param name="Imported">Count of newly auto-imported devices.</param>
/// <param name="Warnings">Warnings produced during reconciliation.</param>
public sealed record ReconciliationResult(
    int Unchanged,
    int Updated,
    int Orphaned,
    int Restored,
    int NewDiscovered,
    int Imported,
    IReadOnlyList<ReconcileWarning> Warnings);

/// <summary>
/// Item stored in the discovered items staging table.
/// </summary>
/// <param name="ConnectionId">Connection that discovered this item.</param>
/// <param name="NativeId">Native identifier of the discovered device.</param>
/// <param name="Kind">Item kind, e.g. "device".</param>
/// <param name="ServerNativeId">Native identifier of the managing server.</param>
/// <param name="ParentNativeId">Native identifier of parent device, or null.</param>
/// <param name="Type">Proposed type reference.</param>
/// <param name="VendorName">Vendor-reported display name.</param>
/// <param name="VendorAttributes">Vendor-managed attributes.</param>
/// <param name="Status">Discovery status.</param>
/// <param name="FirstSeen">Timestamp when this item was first discovered.</param>
/// <param name="LastSeen">Timestamp when this item was last reported.</param>
public sealed record DiscoveredItem(
    ConnectionId ConnectionId,
    string NativeId,
    string Kind,
    string ServerNativeId,
    string? ParentNativeId,
    TypeRef Type,
    string VendorName,
    IReadOnlyDictionary<string, WpValue> VendorAttributes,
    DiscoveredStatus Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// Property overrides applied when importing a discovered item.
/// </summary>
/// <param name="DisplayName">Optional override for the display name.</param>
/// <param name="LocationId">Optional location override.</param>
/// <param name="Tags">Optional tags override.</param>
public sealed record ImportOverrides(
    LocalizedName? DisplayName = null,
    LocationId? LocationId = null,
    IReadOnlyList<TagId>? Tags = null);

/// <summary>
/// Request to import selected discovered items into active registry.
/// </summary>
/// <param name="NativeIds">List of native device IDs to import.</param>
/// <param name="Overrides">Optional per-device property overrides keyed by native ID, or null.</param>
/// <param name="DefaultLocation">Default location if not overridden.</param>
public sealed record ImportRequest(
    IReadOnlyList<string> NativeIds,
    IReadOnlyDictionary<string, ImportOverrides>? Overrides = null,
    LocationId? DefaultLocation = null);

/// <summary>
/// Contract for reconciling connector inventory with the registry.
/// </summary>
public interface IInventoryReconciler
{
    /// <summary>
    /// Reconciles an inventory report against current registry configuration.
    /// </summary>
    /// <param name="connection">The connection identifier.</param>
    /// <param name="report">The inventory report.</param>
    /// <param name="options">Reconciliation options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing reconciliation counts and warnings.</returns>
    Task<Result<ReconciliationResult>> ReconcileAsync(
        ConnectionId connection,
        InventoryReport report,
        ReconcileOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves discovered items for a connection.
    /// </summary>
    /// <param name="connection">The connection identifier.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered items matching filter.</returns>
    Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredAsync(
        ConnectionId connection,
        DiscoveredStatus? status = null,
        CancellationToken ct = default);

    /// <summary>
    /// Imports selected discovered items into active registry configuration.
    /// </summary>
    /// <param name="connection">The connection identifier.</param>
    /// <param name="request">The import request with selected IDs and optional overrides.</param>
    /// <param name="ctx">Change context metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of applying the import batch.</returns>
    Task<Result> ImportAsync(
        ConnectionId connection,
        ImportRequest request,
        ChangeContext ctx,
        CancellationToken ct = default);

    /// <summary>
    /// Sets the ignored status of discovered items.
    /// </summary>
    /// <param name="connection">The connection identifier.</param>
    /// <param name="nativeIds">Native identifiers of items to update.</param>
    /// <param name="ignored">True to ignore, false to set back to new.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of updating items.</returns>
    Task<Result> SetIgnoredAsync(
        ConnectionId connection,
        IReadOnlyList<string> nativeIds,
        bool ignored,
        CancellationToken ct = default);
}
