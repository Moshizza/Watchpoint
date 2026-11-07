using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Testing;

/// <summary>
/// TST-I-010: eight test classes (each its own xUnit collection, so they run in parallel) create and drop a database and a
/// NATS scope using identical table, stream and subject names; each sees only its own data.
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "TST")]
public abstract class ParallelIsolationTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
    [Fact]
    public async Task TST_I_010_Parallel_test_classes_do_not_interfere()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = GetType().Name;

        await using (var db = await postgres.CreateDatabaseAsync(marker, ct))
        {
            await PostgresFixtureTests.ExecuteAsync(db.ConnectionString, "CREATE TABLE shared_name (marker text)");
            await PostgresFixtureTests.ExecuteAsync(db.ConnectionString, $"INSERT INTO shared_name VALUES ('{marker}')");
            (await PostgresFixtureTests.QueryListAsync(db.ConnectionString, "SELECT marker FROM shared_name")).ShouldBe([marker]);
        }

        await nats.StartAsync(ct);
        var scope = nats.CreateScope(marker);
        await using var client = new NatsClient(nats.Url);
        var js = client.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(scope.StreamPrefix + "SHARED", [scope.SubjectPrefix + ".shared"]) { Storage = StreamConfigStorage.Memory }, ct);
        await js.PublishAsync(scope.SubjectPrefix + ".shared", marker, cancellationToken: ct);
        var stream = await js.GetStreamAsync(scope.StreamPrefix + "SHARED", cancellationToken: ct);
        stream.Info.State.Messages.ShouldBe(1L);
        await js.DeleteStreamAsync(scope.StreamPrefix + "SHARED", ct);
    }
}

public sealed class ParallelIsolation1(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation2(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation3(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation4(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation5(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation6(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation7(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);

public sealed class ParallelIsolation8(PostgresContainerFixture p, NatsContainerFixture n) : ParallelIsolationTests(p, n);
