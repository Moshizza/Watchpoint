using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class InitializationTests
{
    [Fact]
    public void STA_U_001_New_device_initialised_unknown()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);

        var state = fixture.Store.Get(device.Id.Value).Value;

        state.Properties[TestTypes.Position].Value.ShouldBeNull();
        state.Health.ShouldBe(HealthState.Unknown);
        state.Summary.State.ShouldBe(OverallState.Unknown);
    }

    [Fact]
    public void STA_U_002_Default_values_used()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(defaultValue: WpValue.Enum("closed")));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);

        var state = fixture.Store.Get(device.Id.Value).Value;

        state.Properties[TestTypes.Position].Value.ShouldBe(WpValue.Enum("closed"));
        state.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Uncertain);
    }

    [Fact]
    public void STA_U_015_Deleted_device_removed()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        fixture.Store.Get(device.Id.Value).IsSuccess.ShouldBeTrue();

        fixture.Registry.DeleteDevice(device.Id);

        var result = fixture.Store.Get(device.Id.Value);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe("state.entity.not_found");
    }

    [Fact]
    public void STA_U_016_Type_change_drops_and_initialises_properties()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        fixture.Types.Add(TestTypes.DoorV2DroppingPosition());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        fixture.Store.Get(device.Id.Value).Value.Properties.ShouldContainKey(TestTypes.Position);

        fixture.Registry.UpdateDevice(device with { Type = TestTypes.DoorV2 });

        var state = fixture.Store.Get(device.Id.Value).Value;
        state.Properties.ShouldNotContainKey(TestTypes.Position);
        state.Properties.ShouldContainKey(TestTypes.Locked);
    }

    [Fact]
    public void STA_U_017_Disabled_flag_sets_overall_disabled()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(defaultValue: WpValue.Enum("closed")));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);

        fixture.Registry.UpdateDevice(device with { Enabled = false });

        var state = fixture.Store.Get(device.Id.Value).Value;
        state.Summary.State.ShouldBe(OverallState.Disabled);
    }

    [Fact]
    public void STA_U_018_Maintenance_flag_sets_overall_maintenance_and_keeps_underlying()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(defaultValue: WpValue.Enum("forced")));
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        fixture.Store.Get(device.Id.Value).Value.Summary.UnderlyingState.ShouldBe(OverallState.Alarm);

        fixture.Registry.UpdateDevice(device with { Maintenance = true });

        var state = fixture.Store.Get(device.Id.Value).Value;
        state.Summary.State.ShouldBe(OverallState.Maintenance);
        state.Summary.UnderlyingState.ShouldBe(OverallState.Alarm);
    }
}
