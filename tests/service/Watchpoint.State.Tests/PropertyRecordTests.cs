using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class PropertyRecordTests
{
    [Fact]
    public void STA_U_003_Property_record_applied()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var at = fixture.Time.GetUtcNow();

        var record = fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("open"), at: at);
        fixture.Apply(record);

        var property = fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position];
        property.Value.ShouldBe(WpValue.Enum("open"));
        property.ReceivedTime.ShouldBe(at);
        property.LastSeen.ShouldBe(at);
        property.Sequence.ShouldBe(record.Sequence);
    }

    [Fact]
    public void STA_U_004_Summary_recomputed_on_forced()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);

        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("forced")));

        var state = fixture.Store.Get(device.Id.Value).Value;
        state.Summary.State.ShouldBe(OverallState.Alarm);
        state.Summary.Cause.ShouldBe(TestTypes.Position);
    }

    [Fact]
    public void STA_U_005_Event_record_updates_last_events_only()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var before = fixture.Store.Get(device.Id.Value).Value;

        fixture.Apply(fixture.Records.Event(device.Id, null, TestTypes.ForcedEvent, Severity.Alarm));

        var state = fixture.Store.Get(device.Id.Value).Value;
        state.LastEvents.ShouldContainKey(TestTypes.ForcedEvent);
        state.Properties.ShouldBe(before.Properties);
        state.Summary.ShouldBe(before.Summary);
    }

    [Fact]
    public void STA_U_014_CausedBy_from_correlation()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var command = CommandId.New();

        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("open"), command: command));

        fixture.Store.Get(device.Id.Value).Value.Properties[TestTypes.Position].CausedBy.ShouldBe(command);
    }
}
