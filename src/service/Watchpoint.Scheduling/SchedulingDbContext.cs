using Microsoft.EntityFrameworkCore;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Scheduling;

/// <summary>
/// EF Core entity for calendar persistence (spec SCH §6 table <c>calendars</c>).
/// </summary>
public sealed class CalendarEntity : IVersioned, ITimestamped
{
    /// <summary>Calendar identifier (PK).</summary>
    public CalendarId Id { get; set; }

    /// <summary>Unique key (<c>^[a-z][a-z0-9-]{1,63}$</c>).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Localized name.</summary>
    public LocalizedName Name { get; set; } = LocalizedName.Of("Default");

    /// <summary>IANA time zone id or null.</summary>
    public string? TimeZone { get; set; }

    /// <summary>JSON serialized weekly schedule.</summary>
    public string WeeklyJson { get; set; } = "{}";

    /// <summary>JSON serialized exceptions.</summary>
    public string ExceptionsJson { get; set; } = "[]";

    /// <summary>JSON serialized holiday sets.</summary>
    public string HolidaySetsJson { get; set; } = "[]";

    /// <summary>Validity start date.</summary>
    public DateOnly? ValidFrom { get; set; }

    /// <summary>Validity end date.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <inheritdoc/>
    public long RowVersion { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// EF Core entity for holiday set persistence (spec SCH §6 table <c>holiday_sets</c>).
/// </summary>
public sealed class HolidaySetEntity : IVersioned, ITimestamped
{
    /// <summary>Holiday set identifier (PK).</summary>
    public HolidaySetId Id { get; set; }

    /// <summary>Localized display name.</summary>
    public LocalizedName Name { get; set; } = LocalizedName.Of("Default");

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string? CountryCode { get; set; }

    /// <summary>JSON serialized holidays list.</summary>
    public string HolidaysJson { get; set; } = "[]";

    /// <inheritdoc/>
    public long RowVersion { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// EF Core entity for trigger firing state persistence (spec SCH §6 table <c>trigger_state</c>).
/// </summary>
public sealed class TriggerStateEntity
{
    /// <summary>Owner name (part of composite PK).</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Trigger key (part of composite PK).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Instant when callback completed firing.</summary>
    public DateTimeOffset LastFiredAt { get; set; }
}

/// <summary>
/// Provider-neutral DbContext for scheduling module (schema <c>wp_scheduling</c>).
/// </summary>
public class SchedulingDbContext : ConfigurationDbContextBase
{
    /// <summary>Module name constant.</summary>
    public const string ModuleName = "Scheduling";

    /// <summary>Creates a SchedulingDbContext.</summary>
    public SchedulingDbContext(DbContextOptions options, StorageContextInfo info)
        : base(options, info)
    {
    }

    /// <summary>Gets the Calendars DbSet.</summary>
    public DbSet<CalendarEntity> Calendars => Set<CalendarEntity>();

    /// <summary>Gets the HolidaySets DbSet.</summary>
    public DbSet<HolidaySetEntity> HolidaySets => Set<HolidaySetEntity>();

    /// <summary>Gets the TriggerStates DbSet.</summary>
    public DbSet<TriggerStateEntity> TriggerStates => Set<TriggerStateEntity>();

    /// <inheritdoc/>
    protected override void OnModuleModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<CalendarEntity>(b =>
        {
            b.ToTable("calendars");
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.Key).IsUnique();
            b.Property(c => c.Key).HasMaxLength(64).IsRequired();
            b.Property(c => c.WeeklyJson).HasColumnName("weekly").HasColumnType("jsonb");
            b.Property(c => c.ExceptionsJson).HasColumnName("exceptions").HasColumnType("jsonb");
            b.Property(c => c.HolidaySetsJson).HasColumnName("holiday_sets").HasColumnType("jsonb");
        });

        modelBuilder.Entity<HolidaySetEntity>(b =>
        {
            b.ToTable("holiday_sets");
            b.HasKey(h => h.Id);
            b.Property(h => h.CountryCode).HasMaxLength(2);
            b.Property(h => h.HolidaysJson).HasColumnName("holidays").HasColumnType("jsonb");
        });

        modelBuilder.Entity<TriggerStateEntity>(b =>
        {
            b.ToTable("trigger_state");
            b.HasKey(t => new { t.Owner, t.Key });
            b.Property(t => t.Owner).HasMaxLength(128).IsRequired();
            b.Property(t => t.Key).HasMaxLength(128).IsRequired();
            b.Property(t => t.LastFiredAt).HasColumnName("last_fired_at");
        });
    }
}
