using Shouldly;
using Watchpoint.Testing.Containers;
using Xunit;
using Xunit.Sdk;

namespace Watchpoint.IntegrationTests.Testing;

[Trait("layer", "I")]
[Trait("spec", "TST")]
public sealed class DockerAvailabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TST_I_007_Missing_Docker_skips_locally()
    {
        var options = new ContainerFixtureOptions { DockerProbe = () => false, RequireContainers = false };
        await using var postgres = PostgresContainerFixture.WithOptions(options);
        await using var nats = NatsContainerFixture.WithOptions(options);

        var ex = await Should.ThrowAsync<SkipException>(() => postgres.CreateDatabaseAsync("Any", Ct));
        await Should.ThrowAsync<SkipException>(() => nats.StartAsync(Ct));

        ex.Message.ShouldContain(DockerAvailability.SkipReason);
    }

    [Fact]
    public async Task TST_I_007_Missing_Docker_fails_when_containers_are_required()
    {
        var options = new ContainerFixtureOptions { DockerProbe = () => false, RequireContainers = true };
        await using var postgres = PostgresContainerFixture.WithOptions(options);
        await using var nats = NatsContainerFixture.WithOptions(options);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => postgres.StartAsync(Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => nats.StartAsync(Ct));

        ex.Message.ShouldContain(DockerAvailability.RequireVariable);
    }
}
