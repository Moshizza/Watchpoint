using Microsoft.EntityFrameworkCore;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Rights.Internal.Storage;

internal class RightsDbContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public const string ModuleName = "Rights";

    public DbSet<GrantRecord> Grants => Set<GrantRecord>();

    public DbSet<UserGroupRecord> UserGroups => Set<UserGroupRecord>();

    public DbSet<UserGroupMemberRecord> UserGroupMembers => Set<UserGroupMemberRecord>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("wp_rights");
        var postgres = Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        modelBuilder.Entity<GrantRecord>(b =>
        {
            b.ToTable("grants");
            b.HasKey(e => e.Id);
            b.Property(e => e.Permission).HasMaxLength(128).IsRequired();
            b.Property(e => e.IdentityHash).HasMaxLength(64).IsRequired();
            if (postgres)
            {
                b.Property(e => e.Scope).HasColumnType("jsonb");
                b.Property(e => e.Constraints).HasColumnType("jsonb");
                b.Property(e => e.Source).HasColumnType("jsonb");
            }

            b.HasIndex(e => e.UserId);
            b.HasIndex(e => new { e.UserId, e.IdentityHash }).IsUnique();
        });

        modelBuilder.Entity<UserGroupRecord>(b =>
        {
            b.ToTable("user_groups");
            b.HasKey(e => e.Id);
            if (postgres)
            {
                b.Property(e => e.Name).HasColumnType("jsonb");
                b.Property(e => e.Managers).HasColumnType("jsonb");
            }
        });

        modelBuilder.Entity<UserGroupMemberRecord>(b =>
        {
            b.ToTable("user_group_members");
            b.HasKey(e => new { e.GroupId, e.UserId });
        });
    }
}

internal sealed class RightsPostgresDbContext(DbContextOptions options, StorageContextInfo info) : RightsDbContext(options, info);

internal sealed class GrantRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Permission { get; set; } = string.Empty;

    public string Scope { get; set; } = "{}";

    public string? ScheduleCalendar { get; set; }

    public string Constraints { get; set; } = "{}";

    public string Source { get; set; } = "{}";

    public string IdentityHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Guid CreatedBy { get; set; }
}

internal sealed class UserGroupRecord
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "{}";

    public string? Description { get; set; }

    public string Managers { get; set; } = "[]";

    public long Version { get; set; }
}

internal sealed class UserGroupMemberRecord
{
    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }
}
