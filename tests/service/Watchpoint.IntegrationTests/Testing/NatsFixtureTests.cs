using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Testing;

[Trait("layer", "I")]
[Trait("spec", "TST")]
public sealed class NatsFixtureTests(NatsContainerFixture nats)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TST_I_005_NATS_container_has_JetStream()
    {
        await nats.StartAsync(Ct);
        var scope = nats.CreateScope(nameof(NatsFixtureTests));
        await using var client = new NatsClient(nats.Url);
        var js = client.CreateJetStreamContext();

        var stream = await js.CreateStreamAsync(
            new StreamConfig(scope.StreamPrefix + "EVENTS", [scope.SubjectPrefix + ".events.>"]) { Storage = StreamConfigStorage.Memory },
            Ct);
        var ack = await js.PublishAsync(scope.SubjectPrefix + ".events.one", "hello", cancellationToken: Ct);

        stream.Info.Config.Name.ShouldBe(scope.StreamPrefix + "EVENTS");
        ack.Stream.ShouldBe(scope.StreamPrefix + "EVENTS");
        ack.Seq.ShouldBe(1UL);
    }

    [Fact]
    public async Task TST_I_006_NATS_scopes_isolate_subjects()
    {
        await nats.StartAsync(Ct);
        var a = nats.CreateScope("ScopeA");
        var b = nats.CreateScope("ScopeB");
        await using var client = new NatsClient(nats.Url);
        await using var subscription = await client.Connection.SubscribeCoreAsync<string>(b.SubjectPrefix + ".>", cancellationToken: Ct);
        await client.PingAsync(Ct);

        // Messages from one connection arrive in order: if A's message leaked, it would be read before B's.
        await client.PublishAsync(a.SubjectPrefix + ".x", "from-a", cancellationToken: Ct);
        await client.PublishAsync(b.SubjectPrefix + ".x", "from-b", cancellationToken: Ct);
        var first = await subscription.Msgs.ReadAsync(Ct);

        a.SubjectPrefix.ShouldNotBe(b.SubjectPrefix);
        a.StreamPrefix.ShouldNotBe(b.StreamPrefix);
        first.Data.ShouldBe("from-b");
        first.Subject.ShouldBe(b.SubjectPrefix + ".x");
    }
}
