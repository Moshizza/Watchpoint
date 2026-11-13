using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Watchpoint.Registry.Internal.Storage;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Registry;

/// <summary>
/// Service collection extensions for registering Watchpoint Registry module.
/// </summary>
public static class RegistryModule
{
    /// <summary>
    /// Adds Watchpoint Registry services, including DbContext, in-memory reader, and writer.
    /// </summary>
    public static IServiceCollection AddWatchpointRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddModuleStorage<RegistryDbContext>(
            RegistryDbContext.ModuleName,
            providers => providers.Add<RegistryPostgresDbContext>("postgres"));

        services.TryAddSingleton<NoOpRegistryChangePublisher>();
        services.TryAddSingleton<IRegistryChangePublisher>(sp => sp.GetRequiredService<NoOpRegistryChangePublisher>());
        services.TryAddSingleton<DeviceEnrichmentCache>();

        // Snapshot holder
        RegistrySnapshot? currentSnapshot = null;
        services.AddSingleton<Func<RegistrySnapshot>>(sp => () =>
        {
            if (currentSnapshot != null)
                return currentSnapshot;

            var factory = sp.GetRequiredService<IDbContextFactory<RegistryDbContext>>();
            using var db = factory.CreateDbContext();
            currentSnapshot = LoadSnapshot(db);
            return currentSnapshot;
        });

        services.AddSingleton<Action<RegistrySnapshot>>(sp => snap =>
        {
            currentSnapshot = snap;
        });

        services.TryAddSingleton<IRegistryReader, RegistryReader>();
        services.TryAddSingleton<IRegistryWriter, RegistryWriter>();
        services.TryAddSingleton<IInventoryReconciler, InventoryReconciler>();

        return services;
    }

    private static RegistrySnapshot LoadSnapshot(RegistryDbContext db)
    {
        var siteRecord = db.Sites.AsNoTracking().FirstOrDefault();
        Site site;
        if (siteRecord == null)
        {
            site = new Site(SiteId.New(), new LocalizedName("Default Site", new Dictionary<string, string>()), "UTC", 0);
        }
        else
        {
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(siteRecord.NameTranslations) ?? new();
            site = new Site(SiteId.From(siteRecord.Id), new LocalizedName(siteRecord.NameDefault, translations), siteRecord.DefaultTimezone, siteRecord.RowVersion);
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

/// <summary>
/// Default no-op change publisher for when PIP is not registered.
/// </summary>
public sealed class NoOpRegistryChangePublisher : IRegistryChangePublisher
{
    /// <inheritdoc />
    public Task PublishAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default) =>
        Task.CompletedTask;
}
