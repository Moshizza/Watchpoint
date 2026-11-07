using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Watchpoint.Kernel.Text;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Certification;

/// <summary>
/// Module "Cert" used by the certification suite. Each provider derives a context owning a migration that creates
/// <c>items</c> (id, name, at, label, row_version, created_at, updated_at) in the module schema.
/// </summary>
public class CertificationContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    /// <summary>Module name.</summary>
    public const string ModuleName = "Cert";

    /// <summary>Gets the items.</summary>
    public DbSet<CertItem> Items => Set<CertItem>();

    /// <inheritdoc/>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // Provider migrations of the suite are hand-written and have no model snapshot.
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    /// <inheritdoc/>
    protected override void OnModuleModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<CertItem>().ToTable("items");
}

/// <summary>An entity using every convention of the base context.</summary>
public sealed class CertItem : IVersioned, ITimestamped
{
    /// <summary>Gets or sets the id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a time (precision test).</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Gets or sets a translatable label.</summary>
    public LocalizedName? Label { get; set; }

    /// <inheritdoc/>
    public long RowVersion { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset UpdatedAt { get; set; }
}
