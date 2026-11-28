using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Configuration.Internal.Storage;

/// <summary>A row of <c>wp_config.drafts</c> (spec CFG §11). <see cref="Version"/> is the draft's compare-and-swap version.</summary>
internal sealed class DraftRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "{}";

    public Guid Owner { get; set; }

    public Guid[] Collaborators { get; set; } = [];

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset? PublishAt { get; set; }

    public string? Approval { get; set; }

    public bool Hidden { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long Version { get; set; }
}

/// <summary>A row of <c>wp_config.draft_entries</c>.</summary>
internal sealed class DraftEntryRow
{
    public Guid Id { get; set; }

    public Guid DraftId { get; set; }

    public int Position { get; set; }

    public string Type { get; set; } = string.Empty;

    public Guid ObjectId { get; set; }

    public string Operation { get; set; } = string.Empty;

    public long? BaseVersion { get; set; }

    public string? Content { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A row of <c>wp_config.draft_problems</c> (rewritten on each validation).</summary>
internal sealed class DraftProblemRow
{
    public Guid DraftId { get; set; }

    public int Position { get; set; }

    public string Severity { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Args { get; set; } = "{}";

    public string? ObjectType { get; set; }

    public Guid? ObjectId { get; set; }

    public string? Path { get; set; }

    public string Fixes { get; set; } = "[]";
}

/// <summary>A row of <c>wp_config.draft_secrets</c>: the vault references a draft holds (CFG §6.3).</summary>
internal sealed class DraftSecretRow
{
    public Guid DraftId { get; set; }

    public Guid EntryId { get; set; }

    public string FieldPath { get; set; } = string.Empty;

    public Guid SecretId { get; set; }
}

/// <summary>A row of <c>wp_config.versions</c>.</summary>
internal sealed class VersionRow
{
    public long Version { get; set; }

    public DateTimeOffset PublishedAt { get; set; }

    public Guid Author { get; set; }

    public string? Comment { get; set; }

    public Guid DraftId { get; set; }

    public Guid ChangeSetId { get; set; }

    public string Summary { get; set; } = "{}";

    public string Inverse { get; set; } = "[]";

    public string Diff { get; set; } = "[]";
}

/// <summary>A row of <c>wp_config.version_entries</c>.</summary>
internal sealed class VersionEntryRow
{
    public long Version { get; set; }

    public int Position { get; set; }

    public string Type { get; set; } = string.Empty;

    public Guid ObjectId { get; set; }

    public string Operation { get; set; } = string.Empty;

    public long NewVersion { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}

/// <summary>The tables of the configuration module (schema <c>wp_config</c>, spec CFG §11).</summary>
internal class ConfigDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    /// <summary>The storage module name: schema <c>wp_config</c>.</summary>
    public const string ModuleName = "Config";

    public DbSet<DraftRow> Drafts => Set<DraftRow>();

    public DbSet<DraftEntryRow> DraftEntries => Set<DraftEntryRow>();

    public DbSet<DraftProblemRow> DraftProblems => Set<DraftProblemRow>();

    public DbSet<DraftSecretRow> DraftSecrets => Set<DraftSecretRow>();

    public DbSet<VersionRow> Versions => Set<VersionRow>();

    public DbSet<VersionEntryRow> VersionEntries => Set<VersionEntryRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        var draft = modelBuilder.Entity<DraftRow>();
        draft.ToTable("drafts");
        draft.HasKey(d => d.Id).HasName("pk_drafts");
        draft.Property(d => d.Name).HasColumnType("jsonb");
        draft.Property(d => d.Approval).HasColumnType("jsonb");
        draft.Property(d => d.Status).HasMaxLength(32);
        draft.HasIndex(d => d.Owner).HasDatabaseName("drafts_owner");
        draft.HasIndex(d => d.Status).HasDatabaseName("drafts_status");

        var entry = modelBuilder.Entity<DraftEntryRow>();
        entry.ToTable("draft_entries");
        entry.HasKey(e => e.Id).HasName("pk_draft_entries");
        entry.Property(e => e.Type).HasMaxLength(128);
        entry.Property(e => e.Operation).HasMaxLength(16);
        entry.Property(e => e.Content).HasColumnType("jsonb");
        entry.HasIndex(e => new { e.DraftId, e.Type, e.ObjectId }).IsUnique().HasDatabaseName("draft_entries_object");

        var problem = modelBuilder.Entity<DraftProblemRow>();
        problem.ToTable("draft_problems");
        problem.HasKey(p => new { p.DraftId, p.Position }).HasName("pk_draft_problems");
        problem.Property(p => p.Args).HasColumnType("jsonb");
        problem.Property(p => p.Fixes).HasColumnType("jsonb");

        var secret = modelBuilder.Entity<DraftSecretRow>();
        secret.ToTable("draft_secrets");
        secret.HasKey(s => new { s.DraftId, s.SecretId }).HasName("pk_draft_secrets");

        var version = modelBuilder.Entity<VersionRow>();
        version.ToTable("versions");
        version.HasKey(v => v.Version).HasName("pk_versions");
        version.Property(v => v.Version).ValueGeneratedNever();
        version.Property(v => v.Summary).HasColumnType("jsonb");
        version.Property(v => v.Inverse).HasColumnType("jsonb");
        version.Property(v => v.Diff).HasColumnType("jsonb");

        var versionEntry = modelBuilder.Entity<VersionEntryRow>();
        versionEntry.ToTable("version_entries");
        versionEntry.HasKey(e => new { e.Version, e.Type, e.ObjectId }).HasName("pk_version_entries");
        versionEntry.Property(e => e.Before).HasColumnType("jsonb");
        versionEntry.Property(e => e.After).HasColumnType("jsonb");
        versionEntry.HasIndex(e => new { e.Type, e.ObjectId, e.Version }).HasDatabaseName("version_entries_object");
    }
}

internal sealed class ConfigPostgresDbContext(DbContextOptions options, StorageContextInfo info) : ConfigDbContext(options, info);

[DbContext(typeof(ConfigPostgresDbContext))]
[Migration("20261128000000_InitialConfig")]
internal sealed class InitialConfigMigration : Migration
{
    private const string Schema = "wp_config";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable(
            name: "drafts",
            schema: Schema,
            columns: t => new
            {
                id = t.Column<Guid>(type: "uuid", nullable: false),
                name = t.Column<string>(type: "jsonb", nullable: false),
                owner = t.Column<Guid>(type: "uuid", nullable: false),
                collaborators = t.Column<Guid[]>(type: "uuid[]", nullable: false),
                status = t.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                publish_at = t.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                approval = t.Column<string>(type: "jsonb", nullable: true),
                hidden = t.Column<bool>(type: "boolean", nullable: false),
                created_at = t.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = t.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                version = t.Column<long>(type: "bigint", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_drafts", x => x.id));
        migrationBuilder.CreateIndex(name: "drafts_owner", schema: Schema, table: "drafts", column: "owner");
        migrationBuilder.CreateIndex(name: "drafts_status", schema: Schema, table: "drafts", column: "status");

        migrationBuilder.CreateTable(
            name: "draft_entries",
            schema: Schema,
            columns: t => new
            {
                id = t.Column<Guid>(type: "uuid", nullable: false),
                draft_id = t.Column<Guid>(type: "uuid", nullable: false),
                position = t.Column<int>(type: "integer", nullable: false),
                type = t.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                object_id = t.Column<Guid>(type: "uuid", nullable: false),
                operation = t.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                base_version = t.Column<long>(type: "bigint", nullable: true),
                content = t.Column<string>(type: "jsonb", nullable: true),
                updated_at = t.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_draft_entries", x => x.id));
        migrationBuilder.CreateIndex(name: "draft_entries_object", schema: Schema, table: "draft_entries", columns: ["draft_id", "type", "object_id"], unique: true);

        migrationBuilder.CreateTable(
            name: "draft_problems",
            schema: Schema,
            columns: t => new
            {
                draft_id = t.Column<Guid>(type: "uuid", nullable: false),
                position = t.Column<int>(type: "integer", nullable: false),
                severity = t.Column<string>(type: "text", nullable: false),
                code = t.Column<string>(type: "text", nullable: false),
                args = t.Column<string>(type: "jsonb", nullable: false),
                object_type = t.Column<string>(type: "text", nullable: true),
                object_id = t.Column<Guid>(type: "uuid", nullable: true),
                path = t.Column<string>(type: "text", nullable: true),
                fixes = t.Column<string>(type: "jsonb", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_draft_problems", x => new { x.draft_id, x.position }));

        migrationBuilder.CreateTable(
            name: "draft_secrets",
            schema: Schema,
            columns: t => new
            {
                draft_id = t.Column<Guid>(type: "uuid", nullable: false),
                entry_id = t.Column<Guid>(type: "uuid", nullable: false),
                field_path = t.Column<string>(type: "text", nullable: false),
                secret_id = t.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_draft_secrets", x => new { x.draft_id, x.secret_id }));

        migrationBuilder.CreateTable(
            name: "versions",
            schema: Schema,
            columns: t => new
            {
                version = t.Column<long>(type: "bigint", nullable: false),
                published_at = t.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                author = t.Column<Guid>(type: "uuid", nullable: false),
                comment = t.Column<string>(type: "text", nullable: true),
                draft_id = t.Column<Guid>(type: "uuid", nullable: false),
                change_set_id = t.Column<Guid>(type: "uuid", nullable: false),
                summary = t.Column<string>(type: "jsonb", nullable: false),
                inverse = t.Column<string>(type: "jsonb", nullable: false),
                diff = t.Column<string>(type: "jsonb", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_versions", x => x.version));

        migrationBuilder.CreateTable(
            name: "version_entries",
            schema: Schema,
            columns: t => new
            {
                version = t.Column<long>(type: "bigint", nullable: false),
                position = t.Column<int>(type: "integer", nullable: false),
                type = t.Column<string>(type: "text", nullable: false),
                object_id = t.Column<Guid>(type: "uuid", nullable: false),
                operation = t.Column<string>(type: "text", nullable: false),
                new_version = t.Column<long>(type: "bigint", nullable: false),
                before = t.Column<string>(type: "jsonb", nullable: true),
                after = t.Column<string>(type: "jsonb", nullable: true),
            },
            constraints: t => t.PrimaryKey("pk_version_entries", x => new { x.version, x.type, x.object_id }));
        migrationBuilder.CreateIndex(name: "version_entries_object", schema: Schema, table: "version_entries", columns: ["type", "object_id", "version"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "version_entries", "versions", "draft_secrets", "draft_problems", "draft_entries", "drafts" })
        {
            migrationBuilder.DropTable(name: table, schema: Schema);
        }
    }
}
