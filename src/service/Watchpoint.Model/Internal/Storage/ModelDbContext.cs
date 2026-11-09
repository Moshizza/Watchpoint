using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Model.Internal.Storage;

/// <summary>
/// Persisted document entity mapping table <c>wp_model.documents</c> (spec MOD §6).
/// </summary>
internal sealed class DocumentRecord
{
    public string Kind { get; set; } = string.Empty; // 'capability' | 'type'

    public string Id { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty; // 'standard' or package id

    public string SourceVersion { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty; // JSON

    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAt { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>Configuration-store context of the model module (schema <c>wp_model</c>).</summary>
internal class ModelDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Model";

    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        var doc = modelBuilder.Entity<DocumentRecord>();
        doc.ToTable("documents");
        doc.HasKey(d => new { d.Kind, d.Id, d.Version });
        doc.Property(d => d.Sha256).HasMaxLength(64).IsFixedLength();
    }
}

/// <summary>PostgreSQL context of the model module (owns the migrations).</summary>
internal sealed class ModelPostgresDbContext(DbContextOptions options, StorageContextInfo info) : ModelDbContext(options, info);

[DbContext(typeof(ModelPostgresDbContext))]
[Migration("20261109000000_InitialModel")]
internal sealed class InitialModelMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_model");
        migrationBuilder.CreateTable(
            name: "documents",
            schema: "wp_model",
            columns: table => new
            {
                kind = table.Column<string>(type: "text", nullable: false),
                id = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                source_version = table.Column<string>(type: "text", nullable: false),
                content = table.Column<string>(type: "jsonb", nullable: false),
                sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                registered_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                removed_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_documents", x => new { x.kind, x.id, x.version }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "documents", schema: "wp_model");
}
