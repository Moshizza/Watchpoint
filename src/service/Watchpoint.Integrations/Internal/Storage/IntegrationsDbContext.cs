using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Integrations.Internal.Storage;

/// <summary>Row of <c>wp_integrations.packages</c> (spec IRT §6).</summary>
internal sealed class PackageRecord
{
    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? FilePath { get; set; }

    public string Problems { get; set; } = "[]";

    public bool SignedByDev { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset DiscoveredAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Configuration-store context of the integration runtime (schema <c>wp_integrations</c>).</summary>
internal class IntegrationsDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Integrations";

    public DbSet<PackageRecord> Packages => Set<PackageRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        var package = modelBuilder.Entity<PackageRecord>();
        package.ToTable("packages");
        package.HasKey(p => new { p.Id, p.Version });
        if (Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            package.Property(p => p.Problems).HasColumnType("jsonb");
        }
    }
}

/// <summary>PostgreSQL context of the integration runtime (owns the migrations).</summary>
internal sealed class IntegrationsPostgresDbContext(DbContextOptions options, StorageContextInfo info) : IntegrationsDbContext(options, info);

[DbContext(typeof(IntegrationsPostgresDbContext))]
[Migration("20261110000000_InitialIntegrations")]
internal sealed class InitialIntegrationsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_integrations");
        migrationBuilder.CreateTable(
            name: "packages",
            schema: "wp_integrations",
            columns: table => new
            {
                id = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                state = table.Column<string>(type: "text", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                file_path = table.Column<string>(type: "text", nullable: true),
                problems = table.Column<string>(type: "jsonb", nullable: false),
                signed_by_dev = table.Column<bool>(type: "boolean", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                discovered_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_packages", x => new { x.id, x.version }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "packages", schema: "wp_integrations");
}

/// <summary>Package states in the configuration store.</summary>
internal sealed class EfPackageStore(IDbContextFactory<IntegrationsDbContext> factory, TimeProvider time) : IPackageStore
{
    public async Task<IReadOnlyList<PackageRow>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var records = await db.Packages.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. records.Select(r => new PackageRow(r.Id, r.Version, r.State, r.Source, r.FilePath, r.Problems, r.SignedByDev, r.Enabled, r.DiscoveredAt))];
    }

    public async Task SaveAsync(PackageRow row, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.Packages.FirstOrDefaultAsync(p => p.Id == row.Id && p.Version == row.Version, ct).ConfigureAwait(false);
        if (record is null)
        {
            record = new PackageRecord { Id = row.Id, Version = row.Version };
            db.Packages.Add(record);
        }

        record.State = row.State;
        record.Source = row.Source;
        record.FilePath = row.FilePath;
        record.Problems = row.Problems;
        record.SignedByDev = row.SignedByDev;
        record.Enabled = row.Enabled;
        record.DiscoveredAt = row.DiscoveredAt.ToUniversalTime();
        record.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
