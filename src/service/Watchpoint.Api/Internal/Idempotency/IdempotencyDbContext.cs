using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Api.Internal.Idempotency;

/// <summary>A row of <c>wp_api.idempotency</c> (spec API §5.5); <see cref="Status"/> 0 while the first request runs.</summary>
internal sealed class IdempotencyRow
{
    public Guid UserId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public string BodySha256 { get; set; } = string.Empty;

    public int Status { get; set; }

    public byte[]? ResponseBody { get; set; }

    public string? ResponseHeaders { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

internal class IdempotencyDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Api";

    public DbSet<IdempotencyRow> Idempotency => Set<IdempotencyRow>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<IdempotencyRow>();
        row.ToTable("idempotency");
        row.HasKey(r => new { r.UserId, r.Key }).HasName("pk_idempotency");
        row.Property(r => r.Key).HasMaxLength(64);
        row.Property(r => r.ResponseHeaders).HasColumnType("jsonb");
        row.HasIndex(r => r.CreatedAt).HasDatabaseName("idempotency_created_at");
    }
}

internal sealed class IdempotencyPostgresDbContext(DbContextOptions options, StorageContextInfo info) : IdempotencyDbContext(options, info);

[DbContext(typeof(IdempotencyPostgresDbContext))]
[Migration("20261126000000_InitialApi")]
internal sealed class InitialApiMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_api");
        migrationBuilder.CreateTable(
            name: "idempotency",
            schema: "wp_api",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                method = table.Column<string>(type: "text", nullable: false),
                route = table.Column<string>(type: "text", nullable: false),
                body_sha256 = table.Column<string>(type: "text", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                response_body = table.Column<byte[]>(type: "bytea", nullable: true),
                response_headers = table.Column<string>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_idempotency", x => new { x.user_id, x.key }));
        migrationBuilder.CreateIndex(name: "idempotency_created_at", schema: "wp_api", table: "idempotency", column: "created_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "idempotency", schema: "wp_api");
}
