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
/// Kind of semantic link between two devices (REG §4).
/// </summary>
public enum LinkKind
{
    /// <summary>A camera device views the target device.</summary>
    CameraViews,
    /// <summary>The source is a member of the target (e.g. a reader of a door).</summary>
    MemberOf,
    /// <summary>The source controls the target.</summary>
    Controls,
    /// <summary>The source powers the target.</summary>
    Powers,
    /// <summary>The source is part of the target.</summary>
    PartOf
}

/// <summary>
/// Direction filter for <see cref="IRegistryReader.GetLinks"/>.
/// </summary>
public enum LinkDirection
{
    /// <summary>Links where the device is the source and the target.</summary>
    Both,
    /// <summary>Links where the device is the source (<see cref="DeviceLink.From"/>).</summary>
    Outgoing,
    /// <summary>Links where the device is the target (<see cref="DeviceLink.To"/>).</summary>
    Incoming
}

/// <summary>
/// Directed semantic link between two devices (REG §4).
/// </summary>
public sealed record DeviceLink(
    LinkId Id,
    LinkKind Kind,
    DeviceId From,
    DeviceId To,
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
/// Enrichment of a device for streaming pipelines (REG §5, §7.2): the slowly-changing fields a consumer needs per
/// observation without querying the registry on every record. Cached by <see cref="IRegistryReader.GetEnrichment"/>
/// and invalidated per affected device when the location, tags or links of its subtree change.
/// </summary>
/// <param name="Id">The device id.</param>
/// <param name="DisplayNameDefault">The device's display name in the default culture (user override or vendor name).</param>
/// <param name="ServerId">The device's server.</param>
/// <param name="LocationId">The device's location.</param>
/// <param name="LocationPath">The location's path.</param>
/// <param name="LocationNames">The names of the location and its ancestors, default culture, root first.</param>
/// <param name="TagKeys">The keys of the device's tags.</param>
/// <param name="Type">The device's type.</param>
/// <param name="LinkedCameras">Devices linked to this one with <see cref="LinkKind.CameraViews"/> (the cameras viewing it).</param>
/// <param name="Enabled">Whether the device is enabled.</param>
/// <param name="Maintenance">Whether the device is in maintenance.</param>
public sealed record DeviceEnrichment(
    DeviceId Id,
    string DisplayNameDefault,
    ServerId ServerId,
    LocationId LocationId,
    string LocationPath,
    IReadOnlyList<string> LocationNames,
    IReadOnlyList<string> TagKeys,
    TypeRef Type,
    IReadOnlyList<DeviceId> LinkedCameras,
    bool Enabled,
    bool Maintenance);

/// <summary>
/// Discriminated union of atomic mutations in the registry (REG §6).
/// </summary>
public abstract record RegistryChange
{
    private RegistryChange() { }

    /// <summary>Update site configuration.</summary>
    public sealed record UpdateSite(Site Site, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a location node (Path/Depth are computed by the writer).</summary>
    public sealed record UpsertLocation(Location Location, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>
    /// Delete a location node. A location with children or objects requires <paramref name="ReassignTo"/>
    /// (children and objects move there), else <c>registry.location.not_empty</c>; the root location cannot be
    /// deleted (<c>registry.location.root_immutable</c>).
    /// </summary>
    public sealed record DeleteLocation(LocationId Id, LocationId? ReassignTo = null, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a tag.</summary>
    public sealed record UpsertTag(Tag Tag, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a tag.</summary>
    public sealed record DeleteTag(TagId Id) : RegistryChange;

    /// <summary>Upsert a tag family.</summary>
    public sealed record UpsertTagFamily(TagFamily Family, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a tag family (additive to the spec sketch: no code references it yet).</summary>
    public sealed record DeleteTagFamily(TagFamilyId Id, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>Upsert a server instance.</summary>
    public sealed record UpsertServer(Server Server, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>
    /// Delete a server instance. <paramref name="DeleteDevices"/> <see langword="false"/> with devices still on the
    /// server fails with <c>registry.server.not_empty</c>; <see langword="true"/> cascades to its devices and their links.
    /// </summary>
    public sealed record DeleteServer(ServerId Id, bool DeleteDevices) : RegistryChange;

    /// <summary>Upsert a device instance.</summary>
    public sealed record UpsertDevice(Device Device, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Patch device properties (bulk edits).</summary>
    public sealed record PatchDevice(DeviceId Id, DevicePatch Patch, long? ExpectedVersion = null) : RegistryChange;

    /// <summary>
    /// Delete a device instance. <paramref name="DeleteChildren"/> <see langword="false"/> with child devices fails
    /// with <c>registry.device.has_children</c>; <see langword="true"/> cascades to the subtree. Links of deleted
    /// devices are deleted.
    /// </summary>
    public sealed record DeleteDevice(DeviceId Id, bool DeleteChildren) : RegistryChange;

    /// <summary>Upsert a device link.</summary>
    public sealed record UpsertLink(DeviceLink Link) : RegistryChange;
    /// <summary>Delete a device link.</summary>
    public sealed record DeleteLink(LinkId Id) : RegistryChange;

    /// <summary>Upsert a custom field definition.</summary>
    public sealed record UpsertCustomField(CustomFieldDefinition Definition, long? ExpectedVersion = null) : RegistryChange;
    /// <summary>Delete a custom field definition (additive to the spec sketch: no code references it yet).</summary>
    public sealed record DeleteCustomField(CustomFieldId Id, long? ExpectedVersion = null) : RegistryChange;
}

/// <summary>
/// Atomic batch of mutations.
/// </summary>
/// <param name="Changes">The list of mutations to apply atomically.</param>
public sealed record RegistryChangeBatch(IReadOnlyList<RegistryChange> Changes);

/// <summary>
/// Context metadata for a change batch execution (REG §6).
/// </summary>
/// <param name="Actor">The user who initiated the change, or <see langword="null"/> for a system-initiated change.</param>
/// <param name="Origin">
/// Where the change came from: <c>"api"</c>, <c>"integration:&lt;connectionId&gt;"</c>, <c>"seed"</c> or
/// <c>"publish:&lt;changeSetId&gt;"</c> (CFG §7, AUD §4 key on these values).
/// </param>
/// <param name="ChangeSet">The configuration change set this batch came from (CFG), when <paramref name="Origin"/> is a <c>publish:…</c> batch.</param>
/// <param name="Comment">Optional free-text audit comment.</param>
public sealed record ChangeContext(UserId? Actor, string Origin, ChangeSetId? ChangeSet = null, string? Comment = null);

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
