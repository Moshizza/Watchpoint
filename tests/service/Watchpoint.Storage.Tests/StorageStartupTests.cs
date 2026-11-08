using Shouldly;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Internal;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;
using static Watchpoint.Storage.Tests.StorageTestHost;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class StorageStartupTests
{
    private const string Password = "pw-Zq8!never-logged";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task STO_U_014_Connection_string_sanitised_in_logs()
    {
        using var logs = new LogCollector();
        var clock = new TestClock();
        var provider = new FakeStorageProvider();
        var calls = 0;
        provider.Probe = db => calls++ == 0 ? StorageErrors.Unavailable(db.Key) with { DeveloperDetail = "connection refused" } : new DatabaseInfo("17.2", null, [], 1);
        var startup = Startup(provider, clock, logs, ("Database:Databases:default:Host", "db.local"), ("Database:Databases:default:Name", "site_a"),
            ("Database:Databases:default:PasswordDev", Password));

        var wait = startup.WaitForDatabasesAsync(Ct);
        await DriveAsync(clock, wait);

        var retry = logs.WithEventId(1202).ShouldHaveSingleItem();
        retry.RenderedMessage.ShouldContain("db.local");
        retry.RenderedMessage.ShouldContain("site_a");
        logs.WithEventId(1201).ShouldHaveSingleItem();
        var detector = new SecretLeakDetector();
        detector.Register(Password);
        detector.AssertNoLeak(logs);

        // Connection strings printed by the PostgreSQL provider lose their password.
        var sanitized = new PostgresProvider(new PostgresDataSources(new DevelopmentPasswordProvider()), clock).Sanitize($"Host=db.local;Database=site_a;Username=wp;Password={Password}");
        sanitized.ShouldContain("db.local");
        sanitized.ShouldNotContain(Password);
    }

    [Fact]
    public async Task STO_U_015_Startup_wait_then_exit_code_75()
    {
        using var logs = new LogCollector();
        var clock = new TestClock();
        var provider = new FakeStorageProvider { Probe = db => StorageErrors.Unavailable(db.Key) };
        var startup = Startup(provider, clock, logs, ("Database:StartupWait", "PT1M"));

        var wait = startup.WaitForDatabasesAsync(Ct);
        await DriveAsync(clock, wait);

        var ex = await Should.ThrowAsync<StorageStartupException>(() => wait);
        ex.Error.Code.Value.ShouldBe(StorageErrors.UnavailableCode);
        ex.Error.Args["store"].ShouldBe("default");
        ex.ExitCode.ShouldBe(StorageStartupException.TempFail);
        clock.GetUtcNow().ShouldBeGreaterThanOrEqualTo(TestClock.DefaultStart.AddMinutes(1));
        clock.GetUtcNow().ShouldBeLessThan(TestClock.DefaultStart.AddMinutes(2));
        provider.ProbeCount.ShouldBeGreaterThan(3);
        logs.WithEventId(1202).Count().ShouldBe(provider.ProbeCount - 1);
    }

    private static StorageStartup Startup(FakeStorageProvider provider, TestClock clock, LogCollector logs, params (string, string?)[] settings)
    {
        var holder = Holder(Settings(settings), true, provider);
        var runner = new MigrationRunner(holder, [], new NoMigrator(), Logger<MigrationRunner>(logs));
        return new StorageStartup(holder, runner, clock, Logger<StorageStartup>(logs));
    }

    /// <summary>Advances the fake clock one second at a time until <paramref name="task"/> ends (5 simulated minutes at most).</summary>
    private static async Task DriveAsync(TestClock clock, Task task)
    {
        for (var i = 0; i < 300 && !task.IsCompleted; i++)
        {
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await Task.WhenAny(task);
        task.IsCompleted.ShouldBeTrue();
    }

    private sealed class NoMigrator : IModuleMigrator
    {
        public Task<IReadOnlyList<string>> MigrateAsync(IModuleStorageRegistration registration, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
