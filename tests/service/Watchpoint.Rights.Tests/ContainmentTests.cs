using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Model.Documents;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Evaluation;
using Xunit;

namespace Watchpoint.Rights.Tests;

/// <summary>Scope containment used by the escalation guard and the rights copy (RGT-U-035…050).</summary>
public sealed class ContainmentTests
{
    private static readonly LocationId Building = TestIds.Of<LocationId>(1);
    private static readonly LocationId Floor = TestIds.Of<LocationId>(2);
    private static readonly LocationId Other = TestIds.Of<LocationId>(3);
    private static readonly ServerId S1 = TestIds.Server(1);
    private static readonly ServerId S2 = TestIds.Server(2);
    private static readonly TagId T1 = TestIds.Of<TagId>(1);
    private static readonly TagId T2 = TestIds.Of<TagId>(2);
    private static readonly DeviceId D1 = TestIds.Device(1);
    private static readonly DeviceId D2 = TestIds.Device(2);

    private static bool Within(LocationId inner, LocationId outer) => inner == outer || (inner == Floor && outer == Building);

    private static bool In(IReadOnlyList<GrantScope> actor, GrantScope granted, Func<GrantScope, DeviceId, bool>? devices = null) =>
        ScopeContainment.Contains(actor, granted, devices, Within);

    [Fact]
    public void RGT_U_035_Everything_actor_contains_every_scope() =>
        In([GrantScope.All], new GrantScope(LocationsSubtree: [Building], Devices: [D1])).ShouldBeTrue();

    [Fact]
    public void RGT_U_036_Only_everything_contains_everything()
    {
        In([GrantScope.All], GrantScope.All).ShouldBeTrue();
        In([new GrantScope(LocationsSubtree: [Building])], GrantScope.All).ShouldBeFalse();
        In([], GrantScope.All).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_037_Location_inside_the_actors_location_is_contained() =>
        In([new GrantScope(LocationsSubtree: [Building])], new GrantScope(LocationsSubtree: [Floor])).ShouldBeTrue();

    [Fact]
    public void RGT_U_038_Location_outside_or_wider_is_not_contained()
    {
        In([new GrantScope(LocationsSubtree: [Floor])], new GrantScope(LocationsSubtree: [Building])).ShouldBeFalse();
        In([new GrantScope(LocationsSubtree: [Building])], new GrantScope(LocationsSubtree: [Other])).ShouldBeFalse();
        In([new GrantScope(LocationsSubtree: [Building])], new GrantScope(Servers: [S1])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_039_Union_of_actor_grants_covers_locations_piecewise()
    {
        var actor = new[] { new GrantScope(LocationsSubtree: [Floor]), new GrantScope(LocationsSubtree: [Other]) };
        In(actor, new GrantScope(LocationsSubtree: [Floor, Other])).ShouldBeTrue();
        In(actor, new GrantScope(LocationsSubtree: [Floor, Building])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_040_Servers_must_be_a_subset()
    {
        In([new GrantScope(Servers: [S1, S2])], new GrantScope(Servers: [S1])).ShouldBeTrue();
        In([new GrantScope(Servers: [S1])], new GrantScope(Servers: [S1, S2])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_041_Categories_must_be_a_subset()
    {
        In([new GrantScope(Categories: [Category.Video, Category.Fire])], new GrantScope(Categories: [Category.Video])).ShouldBeTrue();
        In([new GrantScope(Categories: [Category.Video])], new GrantScope(Categories: [Category.Fire])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_042_Types_must_be_a_subset()
    {
        var door = TypeId.FromTrusted("ac.door");
        var cam = TypeId.FromTrusted("video.camera");
        In([new GrantScope(Types: [door, cam])], new GrantScope(Types: [door])).ShouldBeTrue();
        In([new GrantScope(Types: [door])], new GrantScope(Types: [door, cam])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_043_Tags_any_must_be_a_subset()
    {
        In([new GrantScope(TagsAny: [T1, T2])], new GrantScope(TagsAny: [T2])).ShouldBeTrue();
        In([new GrantScope(TagsAny: [T1])], new GrantScope(TagsAny: [T1, T2])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_044_Excluded_tags_of_the_actor_must_stay_excluded()
    {
        In([new GrantScope(LocationsSubtree: [Building], TagsExclude: [T1])], new GrantScope(LocationsSubtree: [Building], TagsExclude: [T1, T2])).ShouldBeTrue();
        In([new GrantScope(LocationsSubtree: [Building], TagsExclude: [T1])], new GrantScope(LocationsSubtree: [Building])).ShouldBeFalse();
        In([new GrantScope(LocationsSubtree: [Building], TagsExclude: [T1])], new GrantScope(LocationsSubtree: [Building], TagsExclude: [T2])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_045_Explicit_devices_must_be_in_the_actors_device_list()
    {
        In([new GrantScope(Devices: [D1, D2])], new GrantScope(Devices: [D1])).ShouldBeTrue();
        In([new GrantScope(Devices: [D1])], new GrantScope(Devices: [D1, D2])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_046_Explicit_device_matched_through_the_actors_scope()
    {
        var actor = new[] { new GrantScope(LocationsSubtree: [Building]) };
        In(actor, new GrantScope(Devices: [D1]), (scope, device) => device == D1 && scope.LocationsSubtree!.Contains(Building)).ShouldBeTrue();
        In(actor, new GrantScope(Devices: [D2]), (_, device) => device == D1).ShouldBeFalse();
        In(actor, new GrantScope(Devices: [D1])).ShouldBeFalse(); // no resolver: explicit lists only
    }

    [Fact]
    public void RGT_U_047_Devices_of_a_union_may_come_from_different_actor_grants()
    {
        var actor = new[] { new GrantScope(Devices: [D1]), new GrantScope(Devices: [D2]) };
        In(actor, new GrantScope(Devices: [D1, D2])).ShouldBeTrue();
        In(actor, new GrantScope(Devices: [D1, TestIds.Device(9)])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_048_Granted_scope_narrower_by_an_added_component_is_contained()
    {
        In([new GrantScope(LocationsSubtree: [Building])], new GrantScope(LocationsSubtree: [Building], Categories: [Category.Video])).ShouldBeTrue();
        In([new GrantScope(LocationsSubtree: [Building])], new GrantScope(LocationsSubtree: [Floor], Servers: [S1], Categories: [Category.Video])).ShouldBeTrue();
    }

    [Fact]
    public void RGT_U_049_Granted_scope_missing_a_component_the_actor_has_is_wider()
    {
        In([new GrantScope(LocationsSubtree: [Building], Categories: [Category.Video])], new GrantScope(LocationsSubtree: [Building])).ShouldBeFalse();
        In([new GrantScope(Servers: [S1], TagsAny: [T1])], new GrantScope(Servers: [S1])).ShouldBeFalse();
    }

    [Fact]
    public void RGT_U_050_Sites_and_empty_actor_lists()
    {
        var site = TestIds.Of<SiteId>(1);
        In([new GrantScope(Sites: [site])], new GrantScope(Sites: [site])).ShouldBeTrue();
        In([new GrantScope(Sites: [site])], new GrantScope(Sites: [TestIds.Of<SiteId>(2)])).ShouldBeFalse();
        In([], new GrantScope(LocationsSubtree: [Building])).ShouldBeFalse();
        In([], new GrantScope(Devices: [D1])).ShouldBeFalse();
    }
}
