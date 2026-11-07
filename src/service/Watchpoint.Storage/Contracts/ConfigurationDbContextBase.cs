using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Storage.Contracts;

/// <summary>
/// Base of every module's configuration-store context (spec STO §6.2): default schema from the module name, Kernel id
/// converters, UTC-only <see cref="DateTimeOffset"/> with millisecond precision, <see cref="IVersioned"/> concurrency
/// token and <see cref="ITimestamped"/> columns. Modules derive a provider-neutral context, then one empty subclass per
/// provider that owns that provider's migrations.
/// </summary>
public abstract class ConfigurationDbContextBase : DbContext
{
    /// <summary>Column of the concurrency token.</summary>
    public const string RowVersionColumn = "row_version";

    private static readonly Type[] _entityIdTypes = [.. KernelIdTypes(typeof(IEntityId<>))];
    private static readonly Type[] _stringIdTypes = [.. typeof(TypeId).Assembly.GetExportedTypes()
        .Where(t => t is { IsValueType: true, Namespace: "Watchpoint.Kernel.Ids" }
            && t.GetMethod("FromTrusted", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is not null
            && t.GetProperty("Value")?.PropertyType == typeof(string))];

    /// <summary>Creates the context.</summary>
    /// <param name="options">EF options built by the storage module for the configured provider.</param>
    /// <param name="info">Module name, naming and clock.</param>
    protected ConfigurationDbContextBase(DbContextOptions options, StorageContextInfo info)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Schema = info.Naming.SchemaFor(info.Module);
    }

    /// <summary>Gets the module name, naming and clock.</summary>
    protected StorageContextInfo Info { get; }

    /// <summary>Gets the schema (or table prefix) of the module, e.g. <c>wp_registry</c>.</summary>
    public string Schema { get; }

    /// <inheritdoc/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyConventions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc/>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyConventions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc/>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        foreach (var type in _entityIdTypes)
        {
            configurationBuilder.Properties(type).HaveConversion(typeof(EntityIdConverter<>).MakeGenericType(type));
        }

        foreach (var type in _stringIdTypes)
        {
            configurationBuilder.Properties(type).HaveConversion(typeof(StringIdConverter<>).MakeGenericType(type));
        }

        configurationBuilder.Properties<DateTimeOffset>().HavePrecision(3);
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        if (Info.Naming.UsesSchemas)
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        OnModuleModelCreating(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.ClrType is not null && !e.IsOwned()))
        {
            if (typeof(IVersioned).IsAssignableFrom(entity.ClrType))
            {
                modelBuilder.Entity(entity.ClrType).Property(nameof(IVersioned.RowVersion)).HasColumnName(RowVersionColumn).IsConcurrencyToken();
            }

            if (!Info.Naming.UsesSchemas && entity.GetTableName() is { } table)
            {
                entity.SetTableName(Info.Naming.TableName(Schema, table).Table);
            }
        }
    }

    /// <summary>The module's model; called by <see cref="OnModelCreating"/> before the conventions are applied.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    protected abstract void OnModuleModelCreating(ModelBuilder modelBuilder);

    private void ApplyConventions()
    {
        var now = Info.Clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            EnsureUtc(entry);
            if (entry.Entity is ITimestamped timestamped)
            {
                if (entry.State == EntityState.Added)
                {
                    timestamped.CreatedAt = now;
                }

                timestamped.UpdatedAt = now;
            }

            if (entry.State == EntityState.Modified && entry.Entity is IVersioned)
            {
                var version = entry.Property(nameof(IVersioned.RowVersion));
                version.CurrentValue = (long)version.OriginalValue! + 1;
            }
        }
    }

    private static void EnsureUtc(EntityEntry entry)
    {
        foreach (var property in entry.Properties)
        {
            var value = property.CurrentValue switch
            {
                DateTimeOffset v => v,
                _ => (DateTimeOffset?)null,
            };
            if (value is { Offset.Ticks: not 0 } && (entry.State == EntityState.Added || property.IsModified))
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.DisplayName()}.{property.Metadata.Name} = {value:O} is not UTC: Watchpoint stores UTC only (spec STO §6.2).");
            }
        }
    }

    private static IEnumerable<Type> KernelIdTypes(Type genericInterface) =>
        typeof(IEntityId<>).Assembly.GetExportedTypes().Where(t =>
            t.IsValueType && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericInterface));

    private sealed class EntityIdConverter<TId>() : ValueConverter<TId, Guid>(id => EntityIdConversions<TId>.ToGuid(id), g => EntityIdConversions<TId>.FromGuid(g))
        where TId : struct, IEntityId<TId>;

    private sealed class StringIdConverter<TId>() : ValueConverter<TId, string>(StringIdConversions<TId>.ToText, StringIdConversions<TId>.FromText)
        where TId : struct;

    private static class EntityIdConversions<TId>
        where TId : struct, IEntityId<TId>
    {
        public static Guid ToGuid(TId id) => id.Value;

        public static TId FromGuid(Guid value) => TId.From(value);
    }

    private static class StringIdConversions<TId>
        where TId : struct
    {
        public static readonly Expression<Func<TId, string>> ToText = BuildToText();
        public static readonly Expression<Func<string, TId>> FromText = BuildFromText();

        private static Expression<Func<TId, string>> BuildToText()
        {
            var id = Expression.Parameter(typeof(TId), "id");
            return Expression.Lambda<Func<TId, string>>(Expression.Property(id, "Value"), id);
        }

        private static Expression<Func<string, TId>> BuildFromText()
        {
            var text = Expression.Parameter(typeof(string), "text");
            var fromTrusted = typeof(TId).GetMethod("FromTrusted", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
            return Expression.Lambda<Func<string, TId>>(Expression.Call(fromTrusted, text), text);
        }
    }
}
