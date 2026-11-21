using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.State.Internal;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class ChangeFeedTests
{
    [Fact]
    public void STA_U_024_Fanout_delivers_changes_in_sequence_order()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        using var listener = fixture.Fanout.Subscribe("test", 100);

        foreach (var value in new[] { "open", "closed", "forced", "closed", "open" })
        {
            fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum(value)));
        }

        var sequences = new List<long>();
        while (listener.Changes.TryRead(out var change))
        {
            sequences.Add(change.Sequence);
        }

        sequences.Count.ShouldBe(5);
        sequences.ShouldBe([.. sequences.OrderBy(s => s)]);
        sequences.Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public void STA_U_025_Slow_listener_overflows_without_blocking_writer()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        using var listener = fixture.Fanout.Subscribe("slow", 10);

        for (var i = 0; i < 100; i++)
        {
            fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, i % 2 == 0 ? WpValue.Enum("open") : WpValue.Enum("closed")));
        }

        listener.Overflowed.ShouldBeTrue();
        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Value.ShouldBe(WpValue.Enum("closed"));
    }

    [Fact]
    public void STA_U_026_Changes_since_sequence()
    {
        var full = new RecentChangesRing(1000);
        for (var i = 0; i < 1000; i++)
        {
            full.Append(Guid.NewGuid(), [], false, null, DateTimeOffset.UnixEpoch);
        }

        full.GetSince(900, 1000)!.Count.ShouldBe(100);

        var trimmed = new RecentChangesRing(500);
        for (var i = 0; i < 1000; i++)
        {
            trimmed.Append(Guid.NewGuid(), [], false, null, DateTimeOffset.UnixEpoch);
        }

        trimmed.GetSince(0, 10).ShouldBeNull();
    }
}
