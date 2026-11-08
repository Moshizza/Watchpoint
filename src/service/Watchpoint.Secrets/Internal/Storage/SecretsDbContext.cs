using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>A secret row (spec SEC §8): only the latest version is kept.</summary>
internal sealed class SecretRecord : IVersioned, ITimestamped
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string OwnerModule { get; set; } = string.Empty;

    public Guid? OwnerEntity { get; set; }

    public int Version { get; set; }

    public string KeyId { get; set; } = string.Empty;

    public byte[] WrappedDek { get; set; } = [];

    public byte[] DekNonce { get; set; } = [];

    public byte[] Ciphertext { get; set; } = [];

    public byte[] Nonce { get; set; } = [];

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastAccessedAt { get; set; }

    public long RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Configuration-store context of the secrets module (schema <c>wp_secrets</c>).</summary>
internal class SecretsDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Secrets";

    public DbSet<SecretRecord> Secrets => Set<SecretRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        var secret = modelBuilder.Entity<SecretRecord>();
        secret.ToTable("secrets");
        secret.Property(s => s.KeyId).HasMaxLength(8).IsFixedLength();
        secret.HasIndex(s => new { s.OwnerModule, s.OwnerEntity }).HasDatabaseName("secrets_owner");
    }
}

/// <summary>PostgreSQL context of the secrets module (owns the migrations).</summary>
internal sealed class SecretsPostgresDbContext(DbContextOptions options, StorageContextInfo info) : SecretsDbContext(options, info);

[DbContext(typeof(SecretsPostgresDbContext))]
[Migration("20261107000000_InitialSecrets")]
internal sealed class InitialSecretsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_secrets");
        migrationBuilder.CreateTable(
            name: "secrets",
            schema: "wp_secrets",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                owner_module = table.Column<string>(type: "text", nullable: false),
                owner_entity = table.Column<Guid>(type: "uuid", nullable: true),
                version = table.Column<int>(type: "integer", nullable: false),
                key_id = table.Column<string>(type: "character(8)", fixedLength: true, maxLength: 8, nullable: false),
                wrapped_dek = table.Column<byte[]>(type: "bytea", nullable: false),
                dek_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                last_accessed_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_secrets", x => x.id));
        migrationBuilder.CreateIndex(name: "secrets_owner", schema: "wp_secrets", table: "secrets", columns: ["owner_module", "owner_entity"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "secrets", schema: "wp_secrets");
}
