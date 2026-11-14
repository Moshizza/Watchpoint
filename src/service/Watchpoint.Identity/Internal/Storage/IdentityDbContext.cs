using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Kernel.Ids;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal.Storage;

/// <summary>EF Core entity for user record (spec IDN §7.3 table <c>users</c>).</summary>
internal sealed class UserEntity : IVersioned, ITimestamped
{
    public UserId Id { get; set; }

    public string Login { get; set; } = string.Empty;

    public string LoginNorm { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string Language { get; set; } = "en";

    public string? TimeZone { get; set; }

    public string Kind { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool Administrator { get; set; }

    public string AuthSource { get; set; } = string.Empty;

    public string? ExternalId { get; set; }

    public string? ExternalProvider { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    public string AllowedClientsJson { get; set; } = "[]";

    public string AllowedWorkstationsJson { get; set; } = "[]";

    public string SessionPolicyJson { get; set; } = "{}";

    public string MfaPolicy { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; set; }

    public bool MustChangePassword { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public long RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>EF Core entity for password history (spec IDN §7.3 table <c>password_history</c>).</summary>
internal sealed class PasswordHistoryEntity
{
    public Guid Id { get; set; }

    public UserId UserId { get; set; }

    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>EF Core entity for session (spec IDN §7.3 table <c>sessions</c>).</summary>
internal sealed class SessionEntity
{
    public SessionId Id { get; set; }

    public UserId UserId { get; set; }

    public string ClientKind { get; set; } = string.Empty;

    public string ClientInstanceId { get; set; } = string.Empty;

    public string? WorkstationId { get; set; }

    public string? RemoteIp { get; set; }

    public string? UserAgent { get; set; }

    public string? AppVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    public bool WorkstationVerified { get; set; }
}

/// <summary>EF Core entity for a TOTP enrolment (spec IDN §10 table <c>mfa_totp</c>): the secret lives in the vault, only its id is here.</summary>
internal sealed class TotpEntity
{
    public UserId UserId { get; set; }

    public Guid SecretId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    // Last accepted RFC 6238 step: a code of this step or an earlier one is a replay. Concurrency token, so two nodes
    // cannot both accept the same code.
    public long LastUsedStep { get; set; } = -1;
}

/// <summary>EF Core entity for a recovery code (spec IDN §10 table <c>mfa_recovery_codes</c>): Argon2 hash, single use.</summary>
internal sealed class RecoveryCodeEntity
{
    public Guid Id { get; set; }

    public UserId UserId { get; set; }

    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    // Concurrency token: a code used twice at once on two nodes is accepted once.
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>EF Core entity for a WebAuthn credential (spec IDN §10 table <c>webauthn_credentials</c>).</summary>
internal sealed class WebAuthnCredentialEntity
{
    public Guid Id { get; set; }

    public UserId UserId { get; set; }

    public byte[] CredentialId { get; set; } = [];

    public byte[] PublicKey { get; set; } = [];

    // Concurrency token: an assertion is counted once even when two nodes verify it at the same time.
    public long SignCount { get; set; }

    public string TransportsJson { get; set; } = "[]";

    public string Name { get; set; } = string.Empty;

    public Guid AaGuid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>EF Core entity for refresh token (spec IDN §7.3 table <c>refresh_tokens</c>).</summary>
internal sealed class RefreshTokenEntity
{
    public string TokenHash { get; set; } = string.Empty;

    public SessionId SessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }

    public string? ReplacedByHash { get; set; }
}

/// <summary>EF Core entity for lockout state (spec IDN §7.3 table <c>lockouts</c>).</summary>
internal sealed class LockoutEntity
{
    public UserId UserId { get; set; }

    public string FailuresJson { get; set; } = "[]";

    public DateTimeOffset? LockedUntil { get; set; }

    public int LockCount24h { get; set; }
}

/// <summary>Configuration-store context of the identity module (schema <c>wp_identity</c>).</summary>
internal class IdentityDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Identity";

    public DbSet<UserEntity> Users => Set<UserEntity>();

    public DbSet<PasswordHistoryEntity> PasswordHistory => Set<PasswordHistoryEntity>();

    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    public DbSet<LockoutEntity> Lockouts => Set<LockoutEntity>();

    public DbSet<TotpEntity> Totp => Set<TotpEntity>();

    public DbSet<RecoveryCodeEntity> RecoveryCodes => Set<RecoveryCodeEntity>();

    public DbSet<WebAuthnCredentialEntity> WebAuthnCredentials => Set<WebAuthnCredentialEntity>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<UserEntity>(b =>
        {
            b.ToTable("users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).HasColumnName("id");
            b.Property(u => u.Login).HasColumnName("login").HasMaxLength(64).IsRequired();
            b.Property(u => u.LoginNorm).HasColumnName("login_norm").HasMaxLength(64).IsRequired();
            b.HasIndex(u => u.LoginNorm).IsUnique().HasFilter("deleted_at IS NULL");
            b.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
            b.Property(u => u.Email).HasColumnName("email");
            b.Property(u => u.Phone).HasColumnName("phone");
            b.Property(u => u.Language).HasColumnName("language");
            b.Property(u => u.TimeZone).HasColumnName("time_zone");
            b.Property(u => u.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
            b.Property(u => u.Enabled).HasColumnName("enabled");
            b.Property(u => u.Administrator).HasColumnName("administrator");
            b.Property(u => u.AuthSource).HasColumnName("auth_source").HasMaxLength(32).IsRequired();
            b.Property(u => u.ExternalId).HasColumnName("external_id");
            b.Property(u => u.ExternalProvider).HasColumnName("external_provider");
            b.Property(u => u.ValidFrom).HasColumnName("valid_from");
            b.Property(u => u.ValidUntil).HasColumnName("valid_until");
            b.Property(u => u.AllowedClientsJson).HasColumnName("allowed_clients").HasColumnType("text");
            b.Property(u => u.AllowedWorkstationsJson).HasColumnName("allowed_workstations").HasColumnType("text");
            b.Property(u => u.SessionPolicyJson).HasColumnName("session_policy").HasColumnType("text");
            b.Property(u => u.MfaPolicy).HasColumnName("mfa_policy").HasMaxLength(32).IsRequired();
            b.Property(u => u.PasswordHash).HasColumnName("password_hash");
            b.Property(u => u.PasswordChangedAt).HasColumnName("password_changed_at");
            b.Property(u => u.MustChangePassword).HasColumnName("must_change_password");
            b.Property(u => u.LastSignInAt).HasColumnName("last_sign_in_at");
            b.Property(u => u.Notes).HasColumnName("notes");
            b.Property(u => u.DeletedAt).HasColumnName("deleted_at");
            b.Property(u => u.RowVersion).HasColumnName("row_version");
            b.Property(u => u.CreatedAt).HasColumnName("created_at");
            b.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<PasswordHistoryEntity>(b =>
        {
            b.ToTable("password_history");
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasColumnName("id");
            b.Property(p => p.UserId).HasColumnName("user_id");
            b.Property(p => p.Hash).HasColumnName("hash");
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
            b.HasIndex(p => new { p.UserId, p.CreatedAt });
        });

        modelBuilder.Entity<SessionEntity>(b =>
        {
            b.ToTable("sessions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).HasColumnName("id");
            b.Property(s => s.UserId).HasColumnName("user_id");
            b.Property(s => s.ClientKind).HasColumnName("client_kind");
            b.Property(s => s.ClientInstanceId).HasColumnName("client_instance_id");
            b.Property(s => s.WorkstationId).HasColumnName("workstation_id");
            b.Property(s => s.RemoteIp).HasColumnName("remote_ip");
            b.Property(s => s.UserAgent).HasColumnName("user_agent");
            b.Property(s => s.AppVersion).HasColumnName("app_version");
            b.Property(s => s.CreatedAt).HasColumnName("created_at");
            b.Property(s => s.LastActivityAt).HasColumnName("last_activity_at");
            b.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            b.Property(s => s.RevokedAt).HasColumnName("revoked_at");
            b.Property(s => s.RevokedReason).HasColumnName("revoked_reason");
            b.Property(s => s.WorkstationVerified).HasColumnName("workstation_verified").HasDefaultValue(false);
            b.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<TotpEntity>(b =>
        {
            b.ToTable("mfa_totp");
            b.HasKey(t => t.UserId);
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.SecretId).HasColumnName("secret_id");
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
            b.Property(t => t.ConfirmedAt).HasColumnName("confirmed_at");
            b.Property(t => t.LastUsedStep).HasColumnName("last_used_step").IsConcurrencyToken();
        });

        modelBuilder.Entity<RecoveryCodeEntity>(b =>
        {
            b.ToTable("mfa_recovery_codes");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).HasColumnName("id");
            b.Property(r => r.UserId).HasColumnName("user_id");
            b.Property(r => r.Hash).HasColumnName("hash");
            b.Property(r => r.CreatedAt).HasColumnName("created_at");
            b.Property(r => r.UsedAt).HasColumnName("used_at").IsConcurrencyToken();
            b.HasIndex(r => r.UserId);
        });

        modelBuilder.Entity<WebAuthnCredentialEntity>(b =>
        {
            b.ToTable("webauthn_credentials");
            b.HasKey(w => w.Id);
            b.Property(w => w.Id).HasColumnName("id");
            b.Property(w => w.UserId).HasColumnName("user_id");
            b.Property(w => w.CredentialId).HasColumnName("credential_id").IsRequired();
            b.HasIndex(w => w.CredentialId).IsUnique();
            b.Property(w => w.PublicKey).HasColumnName("public_key").IsRequired();
            b.Property(w => w.SignCount).HasColumnName("sign_count").IsConcurrencyToken();
            b.Property(w => w.TransportsJson).HasColumnName("transports").HasColumnType("text");
            b.Property(w => w.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            b.Property(w => w.AaGuid).HasColumnName("aaguid");
            b.Property(w => w.CreatedAt).HasColumnName("created_at");
            b.Property(w => w.LastUsedAt).HasColumnName("last_used_at");
            b.HasIndex(w => w.UserId);
        });

        modelBuilder.Entity<RefreshTokenEntity>(b =>
        {
            b.ToTable("refresh_tokens");
            b.HasKey(r => r.TokenHash);
            b.Property(r => r.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
            b.Property(r => r.SessionId).HasColumnName("session_id");
            b.Property(r => r.CreatedAt).HasColumnName("created_at");
            b.Property(r => r.ExpiresAt).HasColumnName("expires_at");
            b.Property(r => r.RotatedAt).HasColumnName("rotated_at").IsConcurrencyToken();
            b.Property(r => r.ReplacedByHash).HasColumnName("replaced_by_hash");
            b.HasIndex(r => r.SessionId);
        });

        modelBuilder.Entity<LockoutEntity>(b =>
        {
            b.ToTable("lockouts");
            b.HasKey(l => l.UserId);
            b.Property(l => l.UserId).HasColumnName("user_id");
            b.Property(l => l.FailuresJson).HasColumnName("failures").HasColumnType("text");
            b.Property(l => l.LockedUntil).HasColumnName("locked_until");
            b.Property(l => l.LockCount24h).HasColumnName("lock_count_24h");
        });
    }
}

/// <summary>PostgreSQL-specific context for migrations.</summary>
internal sealed class IdentityPostgresDbContext(DbContextOptions options, StorageContextInfo info) : IdentityDbContext(options, info)
{
    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModuleModelCreating(modelBuilder);

        // PostgreSQL-specific JSONB mapping
        modelBuilder.Entity<UserEntity>(b =>
        {
            b.Property(u => u.SessionPolicyJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<LockoutEntity>(b =>
        {
            b.Property(l => l.FailuresJson).HasColumnType("jsonb");
        });
    }
}

[DbContext(typeof(IdentityPostgresDbContext))]
[Migration("20261109000000_InitialIdentity")]
internal sealed class InitialIdentityMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_identity");

        migrationBuilder.CreateTable(
            name: "users",
            schema: "wp_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                login = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                login_norm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                email = table.Column<string>(type: "text", nullable: true),
                phone = table.Column<string>(type: "text", nullable: true),
                language = table.Column<string>(type: "text", nullable: false),
                time_zone = table.Column<string>(type: "text", nullable: true),
                kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                administrator = table.Column<bool>(type: "boolean", nullable: false),
                auth_source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                external_id = table.Column<string>(type: "text", nullable: true),
                external_provider = table.Column<string>(type: "text", nullable: true),
                valid_from = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                valid_until = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                allowed_clients = table.Column<string>(type: "text", nullable: false),
                allowed_workstations = table.Column<string>(type: "text", nullable: false),
                session_policy = table.Column<string>(type: "jsonb", nullable: false),
                mfa_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                password_hash = table.Column<string>(type: "text", nullable: true),
                password_changed_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                last_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                notes = table.Column<string>(type: "text", nullable: true),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_users", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ix_users_login_norm",
            schema: "wp_identity",
            table: "users",
            column: "login_norm",
            unique: true,
            filter: "deleted_at IS NULL");

        migrationBuilder.CreateTable(
            name: "password_history",
            schema: "wp_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                hash = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_password_history", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ix_password_history_user_id_created_at",
            schema: "wp_identity",
            table: "password_history",
            columns: ["user_id", "created_at"]);

        migrationBuilder.CreateTable(
            name: "sessions",
            schema: "wp_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_kind = table.Column<string>(type: "character varying(32)", nullable: false),
                client_instance_id = table.Column<string>(type: "character varying(64)", nullable: false),
                workstation_id = table.Column<string>(type: "text", nullable: true),
                remote_ip = table.Column<string>(type: "text", nullable: true),
                user_agent = table.Column<string>(type: "text", nullable: true),
                app_version = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                last_activity_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                revoked_reason = table.Column<string>(type: "text", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_sessions", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ix_sessions_user_id",
            schema: "wp_identity",
            table: "sessions",
            column: "user_id");

        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            schema: "wp_identity",
            columns: table => new
            {
                token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                rotated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                replaced_by_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_refresh_tokens", x => x.token_hash));

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_session_id",
            schema: "wp_identity",
            table: "refresh_tokens",
            column: "session_id");

        migrationBuilder.CreateTable(
            name: "lockouts",
            schema: "wp_identity",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                failures = table.Column<string>(type: "jsonb", nullable: false),
                locked_until = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                lock_count_24h = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_lockouts", x => x.user_id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "lockouts", schema: "wp_identity");
        migrationBuilder.DropTable(name: "refresh_tokens", schema: "wp_identity");
        migrationBuilder.DropTable(name: "sessions", schema: "wp_identity");
        migrationBuilder.DropTable(name: "password_history", schema: "wp_identity");
        migrationBuilder.DropTable(name: "users", schema: "wp_identity");
    }
}

[DbContext(typeof(IdentityPostgresDbContext))]
[Migration("20261114120000_MfaAndClientBinding")]
internal sealed class MfaAndClientBindingMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "workstation_verified",
            schema: "wp_identity",
            table: "sessions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "mfa_totp",
            schema: "wp_identity",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                confirmed_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
                last_used_step = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_mfa_totp", x => x.user_id));

        migrationBuilder.CreateTable(
            name: "mfa_recovery_codes",
            schema: "wp_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                hash = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                used_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_mfa_recovery_codes", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ix_mfa_recovery_codes_user_id",
            schema: "wp_identity",
            table: "mfa_recovery_codes",
            column: "user_id");

        migrationBuilder.CreateTable(
            name: "webauthn_credentials",
            schema: "wp_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_id = table.Column<byte[]>(type: "bytea", nullable: false),
                public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                sign_count = table.Column<long>(type: "bigint", nullable: false),
                transports = table.Column<string>(type: "text", nullable: false),
                name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                aaguid = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                last_used_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_webauthn_credentials", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ix_webauthn_credentials_credential_id",
            schema: "wp_identity",
            table: "webauthn_credentials",
            column: "credential_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_webauthn_credentials_user_id",
            schema: "wp_identity",
            table: "webauthn_credentials",
            column: "user_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "webauthn_credentials", schema: "wp_identity");
        migrationBuilder.DropTable(name: "mfa_recovery_codes", schema: "wp_identity");
        migrationBuilder.DropTable(name: "mfa_totp", schema: "wp_identity");
        migrationBuilder.DropColumn(name: "workstation_verified", schema: "wp_identity", table: "sessions");
    }
}
