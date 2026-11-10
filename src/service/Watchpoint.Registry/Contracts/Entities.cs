using System.Diagnostics.CodeAnalysis;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Root site entity representing the supervised deployment.
/// </summary>
/// <param name="Id">The unique site identifier.</param>
/// <param name="Name">Localized name.</param>
/// <param name="DefaultTimezone">Default IANA timezone string.</param>
/// <param name="Version">Optimistic concurrency row version.</param>
public sealed record Site(
    SiteId Id,
    LocalizedName Name,
    string DefaultTimezone,
    long Version);

/// <summary>
/// Kind of location in the hierarchy.
/// </summary>
public enum LocationKind
{
    /// <summary>Site level.</summary>
    Site,
    /// <summary>Building.</summary>
    Building,
    /// <summary>Floor.</summary>
    Floor,
    /// <summary>Zone.</summary>
    Zone,
    /// <summary>Room.</summary>
    Room,
    /// <summary>Outdoor area.</summary>
    Outdoor
}

/// <summary>
/// Hierarchical location node.
/// </summary>
public sealed record Location(
    LocationId Id,
    LocationId? ParentId,
    LocalizedName Name,
    LocationKind Kind,
    string Path,
    int Depth,
    string? Tz,
    string? Address,
    string? Notes,
    int SortOrder,
    long Version);

/// <summary>
/// Tag cardinality rule.
/// </summary>
public enum TagCardinality
{
    /// <summary>At most one tag from this family per target.</summary>
    ZeroOrOne,
    /// <summary>Any number of tags from this family per target.</summary>
    ZeroOrMore
}

/// <summary>
/// Tag family grouping related tags.
/// </summary>
public sealed record TagFamily(
    TagFamilyId Id,
    string Key,
    LocalizedName Name,
    string? Color,
    TagCardinality Cardinality,
    long Version);

/// <summary>
/// Tag entity assigned to entities.
/// </summary>
public sealed record Tag(
    TagId Id,
    TagFamilyId FamilyId,
    string Key,
    LocalizedName Name,
    string? Color,
    long Version);

/// <summary>
/// Server entity representing an integration server instance.
/// </summary>
public sealed record Server(
    ServerId Id,
    TypeRef Type,
    ConnectionId ConnectionId,
    ServerId? ParentId,
    string NativeId,
    LocalizedName? DisplayName,
    string VendorName,
    LocationId LocationId,
    IReadOnlyList<TagId> Tags,
    IReadOnlyDictionary<string, WpValue> CustomFields,
    bool Enabled,
    bool Maintenance,
    long Version);

/// <summary>
/// Geometric position and area of a device on a map or floorplan.
/// </summary>
public sealed record DeviceGeometry(
    string Type,
    IReadOnlyList<double> Coordinates);

/// <summary>
/// Device entity supervised in the system.
/// </summary>
public sealed record Device(
    DeviceId Id,
    TypeRef Type,
    ServerId ServerId,
    DeviceId? ParentId,
    string NativeId,
    LocalizedName? DisplayName,
    string VendorName,
    LocationId LocationId,
    IReadOnlyList<TagId> Tags,
    IReadOnlyDictionary<string, WpValue> Attributes,
    IReadOnlyDictionary<CustomFieldId, WpValue> CustomFields,
    IReadOnlySet<CapabilityId> UnsupportedCaps,
    DeviceGeometry? Geometry,
    bool Enabled,
    bool Maintenance,
    bool Orphaned,
    DateTimeOffset CreatedAt,
    long Version);

/// <summary>
/// Directed semantic link between two devices.
/// </summary>
public sealed record DeviceLink(
    DeviceId SourceDeviceId,
    string Role,
    DeviceId TargetDeviceId,
    IReadOnlyDictionary<string, WpValue> Metadata,
    long Version);

/// <summary>
/// Target entity type for custom field definitions.
/// </summary>
public enum CustomFieldTarget
{
    /// <summary>Custom field on device.</summary>
    Device,
    /// <summary>Custom field on server.</summary>
    Server,
    /// <summary>Custom field on location.</summary>
    Location
}

/// <summary>
/// Custom field definition.
/// </summary>
public sealed record CustomFieldDefinition(
    CustomFieldId Id,
    CustomFieldTarget Target,
    string Key,
    LocalizedName Label,
    WpValueKind Kind,
    bool Required,
    WpValue? DefaultValue,
    long Version);

/// <summary>
/// Filter criteria for querying locations.
/// </summary>
public sealed record LocationQuery(
    LocationId? ParentId = null,
    string? PathPrefix = null,
    string? SearchText = null);

/// <summary>
/// Filter criteria for querying servers.
/// </summary>
public sealed record ServerQuery(
    ConnectionId? ConnectionId = null,
    LocationId? LocationId = null,
    bool? Enabled = null,
    string? SearchText = null);

/// <summary>
/// Filter criteria for querying devices.
/// </summary>
public sealed record DeviceQuery(
    ServerId? ServerId = null,
    LocationId? LocationId = null,
    TypeId? TypeId = null,
    IReadOnlyList<TagId>? Tags = null,
    bool? Enabled = null,
    bool? Maintenance = null,
    bool? Orphaned = null,
    string? SearchText = null);

/// <summary>
/// Paging request specifying page size and keyset cursor.
/// </summary>
/// <param name="PageSize">Max items per page.</param>
/// <param name="Cursor">Opaque keyset cursor for forward pagination.</param>
public sealed record PageRequest(int PageSize = 50, string? Cursor = null);

/// <summary>
/// Paged collection result with next cursor.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">Items in the page.</param>
/// <param name="NextCursor">Opaque cursor for the next page if more items exist.</param>
/// <param name="TotalCount">Total count matching the filter.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, int TotalCount);

/// <summary>
/// Device patch representation where omitted fields are not updated.
/// </summary>
public sealed record DevicePatch(
    Optional<LocalizedName?> DisplayName = default,
    Optional<LocationId> LocationId = default,
    Optional<IReadOnlyList<TagId>> Tags = default,
    Optional<IReadOnlyDictionary<string, WpValue>> Attributes = default,
    Optional<IReadOnlyDictionary<CustomFieldId, WpValue>> CustomFields = default,
    Optional<IReadOnlySet<CapabilityId>> UnsupportedCaps = default,
    Optional<DeviceGeometry?> Geometry = default,
    Optional<bool> Enabled = default,
    Optional<bool> Maintenance = default);

/// <summary>
/// Discriminated union of atomic mutations in the registry.
/// </summary>
public abstract record RegistryChange
{
    private RegistryChange() { }

    /// <summary>Update site configuration.</summary>
    public sealed record UpdateSite(Site Site, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a location node.</summary>
    public sealed record UpsertLocation(Location Location, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a location node.</summary>
    public sealed record DeleteLocation(LocationId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a tag family.</summary>
    public sealed record UpsertTagFamily(TagFamily Family, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a tag family.</summary>
    public sealed record DeleteTagFamily(TagFamilyId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a tag.</summary>
    public sealed record UpsertTag(Tag Tag, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a tag.</summary>
    public sealed record DeleteTag(TagId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a server instance.</summary>
    public sealed record UpsertServer(Server Server, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a server instance.</summary>
    public sealed record DeleteServer(ServerId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a device instance.</summary>
    public sealed record UpsertDevice(Device Device, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Patch device properties.</summary>
    public sealed record PatchDevice(DeviceId Id, DevicePatch Patch, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a device instance.</summary>
    public sealed record DeleteDevice(DeviceId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a device link.</summary>
    public sealed record UpsertDeviceLink(DeviceLink Link, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a device link.</summary>
    public sealed record DeleteDeviceLink(DeviceId SourceDeviceId, string Role, DeviceId TargetDeviceId, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a custom field definition.</summary>
    public sealed record UpsertCustomField(CustomFieldDefinition CustomField, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a custom field definition.</summary>
    public sealed record DeleteCustomField(CustomFieldId Id, long? ExpectedVersion = null) : RegistryChange;
}

/// <summary>
/// Atomic batch of mutations.
/// </summary>
/// <param name="Changes">The list of mutations to apply atomically.</param>
public sealed record RegistryChangeBatch(IReadOnlyList<RegistryChange> Changes);

/// <summary>
/// Context metadata for a change batch execution.
/// </summary>
/// <param name="UserId">The user who initiated the change, or null for system.</param>
/// <param name="Reason">Optional audit reason.</param>
/// <param name="CorrelationId">Correlation ID for tracing.</param>
public sealed record ChangeContext(string? UserId = null, string? Reason = null, string? CorrelationId = null);

/// <summary>
/// Event arguments for registry changed notification.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Follows .NET EventArgs naming pattern")]
public sealed class RegistryChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of <see cref="RegistryChangedEventArgs"/>.
    /// </summary>
    /// <param name="batch">The batch applied.</param>
    /// <param name="context">The change context.</param>
    public RegistryChangedEventArgs(RegistryChangeBatch batch, ChangeContext context)
    {
        Batch = batch;
        Context = context;
    }

    /// <summary>Gets the batch of changes applied.</summary>
    public RegistryChangeBatch Batch { get; }
    /// <summary>Gets the context in which changes were made.</summary>
    public ChangeContext Context { get; }
}
