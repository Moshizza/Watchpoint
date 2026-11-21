using System.Diagnostics;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Tests.Support;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class QueryTests
{
    [Fact]
    public void STA_U_027_GetMany_omits_unknown_ids()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);

        var result = fixture.Store.GetMany([device.Id.Value, Guid.NewGuid()]);

        result.Count.ShouldBe(1);
        result[0].EntityId.ShouldBe(device.Id.Value);
    }

    [Fact]
    public void STA_U_028_Query_by_state()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        for (var i = 0; i < 100; i++)
        {
            var device = TestEntities.Door();
            fixture.Registry.CreateDevice(device);
            fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, i < 5 ? WpValue.Enum("forced") : WpValue.Enum("closed")));
        }

        var page = fixture.Store.Query(new StateQuery(new DeviceQuery(), States: [OverallState.Alarm]), new PageRequest(1000)).Value;

        page.Items.Count.ShouldBe(5);
        page.Items.ShouldAllBe(s => s.Summary.State == OverallState.Alarm);
    }

    [Fact]
    public void STA_U_029_Query_by_property_condition()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var forced = TestEntities.Door();
        var closed = TestEntities.Door();
        fixture.Registry.CreateDevice(forced);
        fixture.Registry.CreateDevice(closed);
        fixture.Apply(fixture.Records.Property(forced.Id, null, TestTypes.Position, WpValue.Enum("forced")));
        fixture.Apply(fixture.Records.Property(closed.Id, null, TestTypes.Position, WpValue.Enum("closed")));

        var page = fixture.Store.Query(new StateQuery(new DeviceQuery(), Where: new PropertyCondition(TestTypes.Position, WpValue.Enum("forced"))), new PageRequest()).Value;

        page.Items.Count.ShouldBe(1);
        page.Items[0].EntityId.ShouldBe(forced.Id.Value);
    }

    [Fact]
    public void STA_U_030_Query_invalid_condition()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var unknown = PropertyPath.FromTrusted("nope.bogus");

        var result = fixture.Store.Query(new StateQuery(new DeviceQuery(), Where: new PropertyCondition(unknown, WpValue.Bool(true))), new PageRequest());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe("state.query.invalid_condition");
    }

    [Fact]
    public void STA_U_031_Query_performance()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door(defaultValue: WpValue.Enum("closed")));
        for (var i = 0; i < 50_000; i++)
        {
            fixture.Registry.CreateDevice(TestEntities.Door());
        }

        var stopwatch = Stopwatch.StartNew();
        var page = fixture.Store.Query(new StateQuery(new DeviceQuery(), Where: new PropertyCondition(TestTypes.Position, WpValue.Enum("closed"))), new PageRequest()).Value;
        stopwatch.Stop();

        page.Items.Count.ShouldBe(PageRequest.DefaultLimit);
        var limit = TimingLimits.For(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));
        stopwatch.Elapsed.ShouldBeLessThan(limit);
    }
}
