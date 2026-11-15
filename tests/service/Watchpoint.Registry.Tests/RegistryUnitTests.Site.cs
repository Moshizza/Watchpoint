using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Watchpoint.Registry.Internal.Storage;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Registry.Tests;

/// <summary>Site identity (REG §4 R1): the site row carries <c>Site:Id</c>; a stored row with another id wins.</summary>
public sealed partial class RegistryUnitTests
{
    private const int SiteIdMismatchEventId = 1504;

    [Fact]
    public async Task REG_U_046_Created_site_carries_configured_site_id()
    {
        var configured = SiteId.New();
        var collector = new LogCollector();

        RegistrySnapshot snapshot;
        using (var db = _factory.CreateDbContext())
        {
            snapshot = RegistrySnapshotLoader.Load(db, configured, collector.CreateLogger("registry"));
        }

        snapshot.Site.Id.ShouldBe(configured);
        snapshot.Site.Version.ShouldBe(0);
        collector.Entries.ShouldBeEmpty();

        // The first UpdateSite (dev seed or API) writes the row with Site:Id; the seed creates the root location with it (R2).
        using var writer = new RegistryWriter(_factory, _types, _publisher, _clock, NullLogger<RegistryWriter>.Instance, s => snapshot = s, () => snapshot);
        var root = new Location(LocationId.New(), null, new LocalizedName("HQ", new Dictionary<string, string>()), LocationKind.Site, "/HQ", 0, null, null, null, 0, 0);
        var batch = new RegistryChangeBatch([
            new RegistryChange.UpdateSite(snapshot.Site with { DefaultTimezone = "Europe/Paris" }),
            new RegistryChange.UpsertLocation(root)
        ]);
        var res = await writer.ApplyAsync(batch, _context, TestContext.Current.CancellationToken);
        res.IsSuccess.ShouldBeTrue(res.IsSuccess ? null : res.Error.ToString());

        snapshot.Site.Id.ShouldBe(configured);
        using (var db = _factory.CreateDbContext())
        {
            var row = await db.Sites.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            row.Id.ShouldBe(configured.Value);
            row.DefaultTimezone.ShouldBe("Europe/Paris");
        }
    }

    [Fact]
    public async Task REG_U_047_Stored_site_row_with_another_id_wins_and_warns_once()
    {
        var stored = SiteId.New();
        var configured = SiteId.New();
        var now = _clock.GetUtcNow();
        using (var db = _factory.CreateDbContext())
        {
            db.Sites.Add(new SiteRecord
            {
                Id = stored.Value,
                NameDefault = "Existing",
                NameTranslations = "{}",
                DefaultTimezone = "UTC",
                RowVersion = 3,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var collector = new LogCollector();
        RegistrySnapshot snapshot;
        using (var db = _factory.CreateDbContext())
        {
            snapshot = RegistrySnapshotLoader.Load(db, configured, collector.CreateLogger("registry"));
        }

        snapshot.Site.Id.ShouldBe(stored);
        snapshot.Site.Version.ShouldBe(3);

        var warnings = collector.WithEventId(SiteIdMismatchEventId).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].Level.ShouldBe(LogLevel.Warning);
        warnings[0].Properties["StoredSiteId"].ShouldBe(stored.Value);
        warnings[0].Properties["ConfiguredSiteId"].ShouldBe(configured.Value);
        warnings[0].RenderedMessage.ShouldContain(stored.Value.ToString());
        warnings[0].RenderedMessage.ShouldContain(configured.Value.ToString());

        // No write: the row is unchanged, and loading with the stored id is silent.
        using (var db = _factory.CreateDbContext())
        {
            var row = await db.Sites.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            row.Id.ShouldBe(stored.Value);
            row.UpdatedAt.ShouldBe(now);

            collector.Clear();
            RegistrySnapshotLoader.Load(db, stored, collector.CreateLogger("registry")).Site.Id.ShouldBe(stored);
            collector.Entries.ShouldBeEmpty();
        }
    }
}
