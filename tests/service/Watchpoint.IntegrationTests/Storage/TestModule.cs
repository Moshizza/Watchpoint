using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.IntegrationTests.Storage;

/// <summary>A module "TestMod" with one table, migrated like a real module (schema <c>wp_testmod</c>).</summary>
internal class TestModContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // The hand-written test migration has no model snapshot.
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Widget>().ToTable("widgets");
}

internal sealed class TestModPostgresContext(DbContextOptions options, StorageContextInfo info) : TestModContext(options, info);

internal sealed class Widget
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[DbContext(typeof(TestModPostgresContext))]
[Migration(Id)]
internal sealed class InitialTestModMigration : Migration
{
    public const string Id = "20260101000000_Initial";

    /// <summary>Makes the migration long enough for two nodes to overlap (STO-I-004).</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(750);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_testmod");
        migrationBuilder.CreateTable(
            name: "widgets",
            schema: "wp_testmod",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_widgets", x => x.id));
        migrationBuilder.Sql($"SELECT pg_sleep({Duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "widgets", schema: "wp_testmod");
}
