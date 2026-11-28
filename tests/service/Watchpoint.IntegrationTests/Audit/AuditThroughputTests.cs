using System.Diagnostics.CodeAnalysis;
using Shouldly;
using Watchpoint.Audit.Contracts;
using Watchpoint.IntegrationTests.Storage;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Audit;

/// <summary>Audit write throughput (AUD-U-020): runs alone, after the parallel tests, so concurrent tests do not distort the measurement.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
[Collection(SerialTiming.Name)]
[Trait("layer", "U")]
[Trait("spec", "AUD")]
public sealed class AuditThroughputTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];
    private readonly string _fallback = Path.Combine(Path.GetTempPath(), "wp-audit-perf-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        if (Directory.Exists(_fallback))
        {
            Directory.Delete(_fallback, recursive: true);
        }
    }

    [Fact]
    public async Task AUD_U_020_Write_throughput_is_at_least_5000_records_per_second_sustained()
    {
        var (node, _) = await StartAsync(s => s with { BatchDelay = TimeSpan.FromMilliseconds(50), ChannelFullWait = TimeSpan.FromSeconds(60) });
        const int Producers = 8;
        const int PerProducer = 2_500;
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();

        await Task.WhenAll(Enumerable.Range(0, Producers).Select(_ => Task.Run(
            async () =>
            {
                for (var i = 0; i < PerProducer; i++)
                {
                    await node.Writer.WriteAsync(AuditRecordOf("commands.requested"), Ct);
                }
            },
            Ct)));
        await UntilHeadAsync(node, Producers * PerProducer);

        var seconds = clock.GetElapsedTime(started).TotalSeconds;
        (Producers * PerProducer / seconds).ShouldBeGreaterThanOrEqualTo(5_000, $"{Producers * PerProducer} records took {seconds:F2} s");
    }

    private static AuditRecord AuditRecordOf(string action) =>
        new(new AuditAction(action), AuditOutcome.Success, AuditActor.User(Guid.CreateVersion7(), "Alice"), [new AuditObjectRef("device", Guid.CreateVersion7())], null, null);

    private async Task<(AuditNode Node, AuditDatabase Db)> StartAsync(Func<AuditSettings, AuditSettings> tweak)
    {
        var db = await AuditDatabase.CreateAsync(postgres, "AuditPerf" + Guid.NewGuid().ToString("N")[..8], Ct);
        _owned.Add(db);
        var node = await AuditNode.StartAsync(db.ConnectionString, _fallback, tweak);
        _owned.Add(node);
        return (node, db);
    }

    private static async Task UntilHeadAsync(AuditNode node, long sequence)
    {
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        while ((await node.Store.GetHeadAsync("node-it", Ct))?.Sequence is not { } head || head < sequence)
        {
            clock.GetElapsedTime(started).ShouldBeLessThan(TestTimeouts.Container * 4, $"the head did not reach {sequence}");
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, Ct);
        }
    }
}
