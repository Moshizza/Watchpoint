using Shouldly;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Internal;
using Watchpoint.Testing.Logging;
using Xunit;
using static Watchpoint.Storage.Tests.StorageTestHost;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class MigrationRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task STO_U_012_Migration_order_follows_registrations()
    {
        var provider = new FakeStorageProvider();
        var migrator = new RecordingMigrator(provider.Events);
        var runner = Runner(provider, migrator, Registration("Gamma", 3), Registration("Alpha", 1), Registration("Beta", 2));

        await runner.RunAsync(Ct);

        provider.Events.ShouldBe(["lock default", "bootstrap default [wp_alpha,wp_beta,wp_gamma]", "migrate Alpha", "migrate Beta", "migrate Gamma", "unlock default"]);
    }

    [Fact]
    public async Task STO_U_013_Migration_failure_stops_and_releases_lock()
    {
        using var logs = new LogCollector();
        var provider = new FakeStorageProvider();
        var migrator = new RecordingMigrator(provider.Events) { FailOn = "Beta" };
        var runner = Runner(provider, migrator, logs, Registration("Alpha", 1), Registration("Beta", 2), Registration("Gamma", 3));

        var ex = await Should.ThrowAsync<StorageStartupException>(() => runner.RunAsync(Ct));

        ex.Error.Code.Value.ShouldBe(StorageErrors.MigrationFailedCode);
        ex.Error.Args["module"].ShouldBe("Beta");
        ex.Error.Args["migration"].ShouldBe("20260101_Second");
        ex.ExitCode.ShouldBe(StorageStartupException.Software);
        provider.Events.ShouldBe(["lock default", "bootstrap default [wp_alpha,wp_beta,wp_gamma]", "migrate Alpha", "migrate Beta", "unlock default"]);
        logs.WithEventId(1211).ShouldHaveSingleItem().Properties["Module"].ShouldBe("Beta");
        logs.WithEventId(1210).ShouldHaveSingleItem().Properties["Module"].ShouldBe("Alpha");
    }

    private static MigrationRunner Runner(FakeStorageProvider provider, IModuleMigrator migrator, params IModuleStorageRegistration[] registrations) =>
        Runner(provider, migrator, new LogCollector(), registrations);

    private static MigrationRunner Runner(FakeStorageProvider provider, IModuleMigrator migrator, LogCollector logs,
        params IModuleStorageRegistration[] registrations) =>
        new(Holder(Settings(), true, provider), registrations, migrator, Logger<MigrationRunner>(logs));

    private static ModuleStorageRegistration<TestModuleContext> Registration(string module, int order) =>
        new ModuleStorageRegistration<TestModuleContext>(module, new Dictionary<string, Type> { ["postgres"] = typeof(TestModuleContext) }, order);

    private sealed class RecordingMigrator(List<string> events) : IModuleMigrator
    {
        public string? FailOn { get; init; }

        public Task<IReadOnlyList<string>> MigrateAsync(IModuleStorageRegistration registration, CancellationToken ct)
        {
            events.Add($"migrate {registration.Module}");
            return registration.Module == FailOn
                ? throw new ModuleMigrationException("20260101_Second", new InvalidOperationException("column already exists"))
                : Task.FromResult<IReadOnlyList<string>>(["20260101_Initial"]);
        }
    }
}
