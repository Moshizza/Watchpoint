using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class ConfigurationDbContextBaseTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();

    public ConfigurationDbContextBaseTests() => _connection.Open();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task STO_U_008_Id_value_converter_round_trip()
    {
        var id = DeviceId.New();
        await using (var context = await CreateAsync())
        {
            var model = context.Model.FindEntityType(typeof(Gadget))!;
            model.FindProperty(nameof(Gadget.Id))!.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(Guid));
            model.FindProperty(nameof(Gadget.Type))!.GetValueConverter()!.ProviderClrType.ShouldBe(typeof(string));
            context.Gadgets.Add(new Gadget { Id = id, Type = TypeId.FromTrusted("ac.door") });
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = await CreateAsync(create: false))
        {
            var loaded = await context.Gadgets.SingleAsync(g => g.Id == id, Ct);
            loaded.Id.ShouldBe(id);
            loaded.Type.ShouldBe(TypeId.FromTrusted("ac.door"));
        }
    }

    [Fact]
    public async Task STO_U_009_Non_UTC_DateTimeOffset_rejected_on_save()
    {
        await using var context = await CreateAsync();
        context.Gadgets.Add(new Gadget { Id = DeviceId.New(), SeenAt = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(1)) });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(Ct));

        ex.Message.ShouldContain("SeenAt");
        ex.Message.ShouldContain("not UTC");
    }

    [Fact]
    public async Task STO_U_010_Row_version_increments_on_modification()
    {
        await using var context = await CreateAsync();
        var gadget = new Gadget { Id = DeviceId.New(), Name = "a" };
        context.Gadgets.Add(gadget);
        await context.SaveChangesAsync(Ct);
        gadget.RowVersion.ShouldBe(0);

        gadget.Name = "b";
        await context.SaveChangesAsync(Ct);
        gadget.RowVersion.ShouldBe(1);

        await context.SaveChangesAsync(Ct); // unchanged
        gadget.RowVersion.ShouldBe(1);

        context.Model.FindEntityType(typeof(Gadget))!.FindProperty(nameof(IVersioned.RowVersion))!.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public async Task STO_U_011_Timestamps_set()
    {
        await using var context = await CreateAsync();
        var gadget = new Gadget { Id = DeviceId.New(), Name = "a" };
        context.Gadgets.Add(gadget);
        await context.SaveChangesAsync(Ct);
        gadget.CreatedAt.ShouldBe(TestClock.DefaultStart);
        gadget.UpdatedAt.ShouldBe(TestClock.DefaultStart);

        _clock.Advance(TimeSpan.FromMinutes(3));
        gadget.Name = "b";
        await context.SaveChangesAsync(Ct);

        gadget.CreatedAt.ShouldBe(TestClock.DefaultStart);
        gadget.UpdatedAt.ShouldBe(TestClock.DefaultStart.AddMinutes(3));
    }

    private async Task<TestModuleContext> CreateAsync(bool create = true)
    {
        var options = new DbContextOptionsBuilder().UseSqlite(_connection).Options;
        var context = new TestModuleContext(options, new StorageContextInfo("TestMod", StorageNaming.TablePrefixes, _clock));
        if (create)
        {
            await context.Database.EnsureCreatedAsync(Ct);
        }

        return context;
    }
}

internal sealed class Gadget : IVersioned, ITimestamped
{
    public DeviceId Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public TypeId Type { get; set; } = TypeId.FromTrusted("generic.thing");

    public DateTimeOffset? SeenAt { get; set; }

    public long RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TestModuleContext(DbContextOptions options, StorageContextInfo info) : ConfigurationDbContextBase(options, info)
{
    public DbSet<Gadget> Gadgets => Set<Gadget>();

    protected override void OnModuleModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Gadget>().ToTable("gadgets");
}
