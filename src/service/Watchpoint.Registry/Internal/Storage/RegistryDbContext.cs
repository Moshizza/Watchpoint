using Microsoft.EntityFrameworkCore;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Registry.Internal.Storage;

internal class RegistryDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Registry";

    public DbSet<SiteRecord> Sites => Set<SiteRecord>();
    public DbSet<LocationRecord> Locations => Set<LocationRecord>();
    public DbSet<TagFamilyRecord> TagFamilies => Set<TagFamilyRecord>();
    public DbSet<TagRecord> Tags => Set<TagRecord>();
    public DbSet<ServerRecord> Servers => Set<ServerRecord>();
    public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();
    public DbSet<DeviceLinkRecord> DeviceLinks => Set<DeviceLinkRecord>();
    public DbSet<CustomFieldRecord> CustomFields => Set<CustomFieldRecord>();
    public DbSet<DiscoveredItemRecord> DiscoveredItems => Set<DiscoveredItemRecord>();
    public DbSet<RevisionRecord> Revision => Set<RevisionRecord>();
    public DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("wp_registry");

        modelBuilder.Entity<SiteRecord>(b =>
        {
            b.ToTable("sites");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.NameTranslations).HasColumnType("jsonb");
            }
        });

        modelBuilder.Entity<LocationRecord>(b =>
        {
            b.ToTable("locations");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.NameTranslations).HasColumnType("jsonb");
            }
            b.HasIndex(e => e.ParentId);
            b.HasIndex(e => e.Path);
        });

        modelBuilder.Entity<TagFamilyRecord>(b =>
        {
            b.ToTable("tag_families");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.NameTranslations).HasColumnType("jsonb");
            }
            b.HasIndex(e => e.Key).IsUnique();
        });

        modelBuilder.Entity<TagRecord>(b =>
        {
            b.ToTable("tags");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.NameTranslations).HasColumnType("jsonb");
            }
            b.HasIndex(e => new { e.FamilyId, e.Key }).IsUnique();
        });

        modelBuilder.Entity<ServerRecord>(b =>
        {
            b.ToTable("servers");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.CustomFields).HasColumnType("jsonb");
                b.Property(e => e.Tags).HasColumnType("uuid[]");
            }
            b.HasIndex(e => new { e.ConnectionId, e.NativeId }).IsUnique();
            b.HasIndex(e => e.LocationId);
            b.HasIndex(e => e.ParentId);
        });

        modelBuilder.Entity<DeviceRecord>(b =>
        {
            b.ToTable("devices");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.Geometry).HasColumnType("jsonb");
                b.Property(e => e.Attributes).HasColumnType("jsonb");
                b.Property(e => e.CustomFields).HasColumnType("jsonb");
                b.Property(e => e.Tags).HasColumnType("uuid[]");
                b.Property(e => e.UnsupportedCaps).HasColumnType("text[]");
            }
            b.HasIndex(e => new { e.ServerId, e.NativeId }).IsUnique();
            b.HasIndex(e => e.LocationId);
        });

        modelBuilder.Entity<DeviceLinkRecord>(b =>
        {
            b.ToTable("device_links");
            b.HasKey(e => new { e.SourceDeviceId, e.Role, e.TargetDeviceId });
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.Metadata).HasColumnType("jsonb");
            }
            b.HasIndex(e => e.TargetDeviceId);
        });

        modelBuilder.Entity<CustomFieldRecord>(b =>
        {
            b.ToTable("custom_fields");
            b.HasKey(e => e.Id);
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.Label).HasColumnType("jsonb");
                b.Property(e => e.DefaultValue).HasColumnType("jsonb");
            }
            b.HasIndex(e => new { e.Target, e.Key }).IsUnique();
        });

        modelBuilder.Entity<DiscoveredItemRecord>(b =>
        {
            b.ToTable("discovered_items");
            b.HasKey(e => new { e.ConnectionId, e.NativeId });
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.VendorAttributes).HasColumnType("jsonb");
            }
        });

        modelBuilder.Entity<RevisionRecord>(b =>
        {
            b.ToTable("revision");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();

            // Two writers incrementing the same revision surface as a concurrency conflict, never as a reused revision.
            b.Property(e => e.Value).IsConcurrencyToken();
        });

        modelBuilder.Entity<OutboxRecord>(b =>
        {
            b.ToTable("outbox");
            b.HasKey(e => e.Revision);
            b.Property(e => e.Revision).ValueGeneratedNever();
            if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                b.Property(e => e.Changes).HasColumnType("jsonb");
            }
            b.HasIndex(e => e.SentAt);
        });
    }
}

internal sealed class RegistryPostgresDbContext(DbContextOptions options, StorageContextInfo info) : RegistryDbContext(options, info);
