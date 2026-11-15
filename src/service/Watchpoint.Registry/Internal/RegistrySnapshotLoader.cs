using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal.Storage;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// Builds the in-memory <see cref="RegistrySnapshot"/> from the persisted registry (REG §7.1).
/// </summary>
internal static class RegistrySnapshotLoader
{
    /// <summary>
    /// Loads the snapshot. Without a site row the site is the default site with <paramref name="configuredSiteId"/>;
    /// the row is written with that id by the first <c>UpdateSite</c> (dev seed or API). A stored row with another id
    /// wins and is reported once (event 1504): the stored id is what the installation's data refers to, and a node's
    /// settings file must not rewrite the shared database.
    /// </summary>
    /// <param name="db">The registry context.</param>
    /// <param name="configuredSiteId">The site id of the node settings (<c>Site:Id</c>, HST §4).</param>
    /// <param name="logger">The logger for the mismatch warning.</param>
    /// <returns>The snapshot.</returns>
    public static RegistrySnapshot Load(RegistryDbContext db, SiteId configuredSiteId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        var siteRecord = db.Sites.AsNoTracking().FirstOrDefault();
        Site site;
        if (siteRecord == null)
        {
            site = new Site(configuredSiteId, new LocalizedName("Default Site", new Dictionary<string, string>()), "UTC", 0);
        }
        else
        {
            var storedSiteId = SiteId.From(siteRecord.Id);
            if (storedSiteId != configuredSiteId)
            {
                RegistryLog.SiteIdMismatch(logger, storedSiteId.Value, configuredSiteId.Value);
            }

            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(siteRecord.NameTranslations) ?? new();
            site = new Site(storedSiteId, new LocalizedName(siteRecord.NameDefault, translations), siteRecord.DefaultTimezone, siteRecord.RowVersion);
        }

        var locations = db.Locations.AsNoTracking().ToList().ToDictionary(
            r => LocationId.From(r.Id),
            r =>
            {
                var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(r.NameTranslations) ?? new();
                return new Location(
                    LocationId.From(r.Id),
                    r.ParentId.HasValue ? LocationId.From(r.ParentId.Value) : null,
                    new LocalizedName(r.NameDefault, tr),
                    Enum.Parse<LocationKind>(r.Kind),
                    r.Path,
                    r.Depth,
                    r.Tz,
                    r.Address,
                    r.Notes,
                    r.SortOrder,
                    r.RowVersion);
            });

        var tagFamilies = db.TagFamilies.AsNoTracking().ToList().ToDictionary(
            r => TagFamilyId.From(r.Id),
            r =>
            {
                var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(r.NameTranslations) ?? new();
                return new TagFamily(
                    TagFamilyId.From(r.Id),
                    r.Key,
                    new LocalizedName(r.NameDefault, tr),
                    r.Color,
                    Enum.Parse<TagCardinality>(r.Cardinality),
                    r.RowVersion);
            });

        var tags = db.Tags.AsNoTracking().ToList().ToDictionary(
            r => TagId.From(r.Id),
            r =>
            {
                var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(r.NameTranslations) ?? new();
                return new Tag(
                    TagId.From(r.Id),
                    TagFamilyId.From(r.FamilyId),
                    r.Key,
                    new LocalizedName(r.NameDefault, tr),
                    r.Color,
                    r.RowVersion);
            });

        var servers = db.Servers.AsNoTracking().ToList().ToDictionary(
            r => ServerId.From(r.Id),
            r =>
            {
                var dn = r.DisplayName != null ? JsonSerializer.Deserialize<LocalizedName>(r.DisplayName) : null;
                var cf = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.CustomFields) ?? new();
                return new Server(
                    ServerId.From(r.Id),
                    new TypeRef(TypeId.FromTrusted(r.TypeId), SemVersion.Parse(r.TypeVersion).Value),
                    ConnectionId.From(r.ConnectionId),
                    r.ParentId.HasValue ? ServerId.From(r.ParentId.Value) : null,
                    r.NativeId,
                    dn,
                    r.VendorName,
                    LocationId.From(r.LocationId),
                    r.Tags.Select(TagId.From).ToList(),
                    cf,
                    r.Enabled,
                    r.Maintenance,
                    r.RowVersion);
            });

        var devices = db.Devices.AsNoTracking().ToList().ToDictionary(
            r => DeviceId.From(r.Id),
            r =>
            {
                var dn = r.DisplayName != null ? JsonSerializer.Deserialize<LocalizedName>(r.DisplayName) : null;
                var geom = r.Geometry != null ? JsonSerializer.Deserialize<DeviceGeometry>(r.Geometry) : null;
                var attrs = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.Attributes) ?? new();
                var rawCf = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.CustomFields) ?? new();
                var cfs = rawCf.ToDictionary(kvp => CustomFieldId.From(Guid.Parse(kvp.Key)), kvp => kvp.Value);
                var unsupp = r.UnsupportedCaps.Select(CapabilityId.FromTrusted).ToHashSet();
                return new Device(
                    DeviceId.From(r.Id),
                    new TypeRef(TypeId.FromTrusted(r.TypeId), SemVersion.Parse(r.TypeVersion).Value),
                    ServerId.From(r.ServerId),
                    r.ParentId.HasValue ? DeviceId.From(r.ParentId.Value) : null,
                    r.NativeId,
                    dn,
                    r.VendorName,
                    LocationId.From(r.LocationId),
                    r.Tags.Select(TagId.From).ToList(),
                    attrs,
                    cfs,
                    unsupp,
                    geom,
                    r.Enabled,
                    r.Maintenance,
                    r.Orphaned,
                    r.CreatedAt,
                    r.RowVersion);
            });

        var links = db.DeviceLinks.AsNoTracking().ToList().Select(r =>
        {
            var meta = JsonSerializer.Deserialize<Dictionary<string, WpValue>>(r.Metadata) ?? new();
            return new DeviceLink(
                DeviceId.From(r.SourceDeviceId),
                r.Role,
                DeviceId.From(r.TargetDeviceId),
                meta,
                r.RowVersion);
        }).ToList();

        var customFields = db.CustomFields.AsNoTracking().ToList().ToDictionary(
            r => CustomFieldId.From(r.Id),
            r =>
            {
                var lbl = JsonSerializer.Deserialize<LocalizedName>(r.Label)!;
                var defVal = r.DefaultValue != null ? JsonSerializer.Deserialize<WpValue>(r.DefaultValue) : (WpValue?)null;
                return new CustomFieldDefinition(
                    CustomFieldId.From(r.Id),
                    Enum.Parse<CustomFieldTarget>(r.Target),
                    r.Key,
                    lbl,
                    Enum.Parse<WpValueKind>(r.Kind),
                    r.Required,
                    defVal,
                    r.RowVersion);
            });

        return new RegistrySnapshot(
            site,
            locations,
            tagFamilies,
            tags,
            servers,
            devices,
            links,
            customFields);
    }
}
