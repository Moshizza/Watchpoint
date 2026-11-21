using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class TouchAndStaleTests
{
    [Fact]
    public void STA_U_010_Touch_updates_last_seen()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("closed")));
        var partition = Partitioning.Of(device.Id.Value);

        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        var touchTime = fixture.Time.GetUtcNow();
        fixture.Touch(new TouchInfo(device.Id, null, TestTypes.Position, touchTime, touchTime), partition);

        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].LastSeen.ShouldBe(touchTime);
    }

    [Fact]
    public void STA_U_011_Staleness_after_stale_after()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(staleAfter: TimeSpan.FromMinutes(10)));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var partition = Partitioning.Of(device.Id.Value);
        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("closed")));
        using var listener = fixture.Fanout.Subscribe("test", 100);

        fixture.Time.Advance(TimeSpan.FromMinutes(11));
        fixture.Tick(partition);

        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Stale);
        listener.Changes.TryRead(out _).ShouldBeTrue();
    }

    [Fact]
    public void STA_U_012_Touch_restores_from_stale()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(staleAfter: TimeSpan.FromMinutes(10)));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var partition = Partitioning.Of(device.Id.Value);
        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("closed")));
        fixture.Time.Advance(TimeSpan.FromMinutes(11));
        fixture.Tick(partition);
        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Stale);

        var touchTime = fixture.Time.GetUtcNow();
        fixture.Touch(new TouchInfo(device.Id, null, TestTypes.Position, touchTime, touchTime), partition);

        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Good);
    }

    [Fact]
    public void STA_U_013_Stale_tracker_rescheduled_on_refresh()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(staleAfter: TimeSpan.FromMinutes(10)));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var partition = Partitioning.Of(device.Id.Value);
        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("closed")));

        fixture.Time.Advance(TimeSpan.FromMinutes(9));
        var touchTime = fixture.Time.GetUtcNow();
        fixture.Touch(new TouchInfo(device.Id, null, TestTypes.Position, touchTime, touchTime), partition);

        fixture.Time.Advance(TimeSpan.FromMinutes(2)); // total 11 minutes since the original record
        fixture.Tick(partition);
        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Good);

        fixture.Time.Advance(TimeSpan.FromMinutes(8)); // total 19 minutes since the original record
        fixture.Tick(partition);
        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Stale);
    }
}
