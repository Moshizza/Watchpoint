using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Rights.Contracts;
using Xunit;

namespace Watchpoint.Rights.Tests;

/// <summary>Scopes, schedules, constraints and decisions of the evaluator (RGT §6).</summary>
public sealed class EvaluatorTests
{
    private const string View = "operate.devices.view";
    private const string Open = "commands.openable.open";

    [Fact]
    public async Task RGT_U_004_Scope_everything()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View));
        new[] { h.DoorA1, h.CameraA, h.DoorB, h.CameraB }.ShouldAllBe(d => h.Allowed(u, View, d));
        h.Allowed(u, "alarms.view", h.DoorA1).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_005_Scope_location_subtree_including_devices_moved_in_later()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])));
        h.Allowed(u, View, h.DoorA1).ShouldBeTrue();
        h.Allowed(u, View, h.CameraA).ShouldBeTrue();
        h.Allowed(u, View, h.CameraB).ShouldBeFalse();

        h.Registry.Move(h.CameraB, h.Floor1);
        h.Allowed(u, View, h.CameraB).ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_006_Scope_servers_categories_types_and_tags()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(Servers: [h.Server2], Categories: [Category.Video])));
        h.Scope(u, View).Devices!.ShouldBe([h.CameraB]);

        await h.Give(u, Harness.Grant(u, View, new GrantScope(Types: [h.Door])));
        h.Scope(u, View).Devices!.ShouldBe([h.DoorA1, h.DoorB], ignoreOrder: true); // IsA: the special door is a door

        await h.Give(u, Harness.Grant(u, View, new GrantScope(TagsAny: [h.TagFront], TagsExclude: [h.TagRear])));
        h.Scope(u, View).Devices!.ShouldBe([h.DoorA1]);

        await h.Give(u, Harness.Grant(u, View, new GrantScope(TagsAny: [h.TagFront, h.TagRear])));
        h.Scope(u, View).Devices!.Count.ShouldBe(3);
    }

    [Fact]
    public async Task RGT_U_007_Explicit_devices_are_ored_with_the_rest()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA], Devices: [h.DoorB])));
        h.Scope(u, View).Devices!.ShouldBe([h.DoorA1, h.CameraA, h.DoorB], ignoreOrder: true);
        h.Allowed(u, View, h.DoorB).ShouldBeTrue();
        h.Allowed(u, View, h.CameraB).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_008_Schedule_active_or_outside_schedule()
    {
        using var h = new Harness();
        h.Calendars.Daily("office", 8, 18);
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, Open, schedule: "office"));
        h.Allowed(u, Open, h.DoorA1).ShouldBeTrue();

        h.Time.Advance(TimeSpan.FromHours(8)); // 20:00
        var decision = h.Authorize(u, Open, h.DoorA1);
        decision.Allowed.ShouldBeFalse();
        decision.DenialCode.ShouldBe("rights.outside_schedule");
        h.Authorize(u, "alarms.view", h.DoorA1).DenialCode.ShouldBe("common.forbidden");
    }

    [Fact]
    public void RGT_U_009_Administrators_are_allowed_everything()
    {
        using var h = new Harness();
        var admin = h.NewUser(admin: true);
        var d = h.Authorize(admin, "config.rights.edit", h.DoorA1);
        d.Allowed.ShouldBeTrue();
        d.EffectiveConstraints.ShouldBe(GrantConstraints.None);
        h.Evaluator.HasPermission(admin, PermissionCode.FromTrusted("anything.at.all"), h.Now).ShouldBeTrue();
        h.Scope(admin, View).Everything.ShouldBeTrue();
        h.Evaluator.Authorize(admin, PermissionCode.FromTrusted("x.y"), new AuthorizationTarget.System(), h.Now).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_010_Template_disabled_and_expired_users_are_denied()
    {
        using var h = new Harness();
        var template = h.NewUser(UserKind.Template);
        var disabled = h.NewUser(enabled: false);
        var expired = h.NewUser(validUntil: h.Now.AddMinutes(-1));
        var unknown = UserId.New();
        foreach (var u in new[] { template, disabled, expired, unknown })
        {
            await h.Give(u, Harness.Grant(u, View));
            h.Allowed(u, View, h.DoorA1).ShouldBeFalse();
            h.Evaluator.HasPermission(u, PermissionCode.FromTrusted(View), h.Now).ShouldBeFalse();
            h.Scope(u, View).Devices!.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task RGT_U_011_Least_restrictive_constraints_across_matching_grants()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u,
            Harness.Grant(u, Open, new GrantScope(LocationsSubtree: [h.BuildingA]), constraints: new GrantConstraints(true, true, true, TimeSpan.FromMinutes(5), null, null, null)),
            Harness.Grant(u, Open, new GrantScope(Categories: [Category.AccessControl]), constraints: new GrantConstraints(true, false, true, TimeSpan.FromMinutes(10), null, null, null)));
        var c = h.Authorize(u, Open, h.DoorA1).EffectiveConstraints; // matched by both
        c.RequireReason.ShouldBeTrue();
        c.RequireFourEyes.ShouldBeFalse();
        c.RequireStepUp.ShouldBeTrue();
        c.MaxDuration.ShouldBe(TimeSpan.FromMinutes(10));
        h.Authorize(u, Open, h.CameraA).EffectiveConstraints.RequireFourEyes.ShouldBeTrue(); // matched by the first only

        await h.Give(u, Harness.Grant(u, Open, constraints: new GrantConstraints(RequireReason: true, MaxDuration: TimeSpan.FromMinutes(5))),
            Harness.Grant(u, Open, new GrantScope(Devices: [h.DoorA1])));
        var open = h.Authorize(u, Open, h.DoorA1).EffectiveConstraints;
        open.RequireReason.ShouldBeFalse();
        open.MaxDuration.ShouldBeNull(); // a grant without limit is the least restrictive
    }

    [Fact]
    public async Task RGT_U_012_Client_kind_constraint()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, Open, constraints: new GrantConstraints(ClientKinds: new HashSet<ClientKind> { ClientKind.Desktop })));
        h.Authorize(u, Open, h.DoorA1, ClientKind.Desktop).Allowed.ShouldBeTrue();
        var denied = h.Authorize(u, Open, h.DoorA1, ClientKind.Web);
        denied.Allowed.ShouldBeFalse();
        denied.DenialCode.ShouldBe("rights.client_kind");
        h.Authorize(u, Open, h.DoorA1).Allowed.ShouldBeTrue(); // client unknown: not checked

        await h.Give(u, Harness.Grant(u, Open, constraints: new GrantConstraints(ClientKinds: new HashSet<ClientKind> { ClientKind.Desktop })),
            Harness.Grant(u, Open, constraints: new GrantConstraints(ClientKinds: new HashSet<ClientKind> { ClientKind.Web })));
        h.Authorize(u, Open, h.DoorA1, ClientKind.Web).Allowed.ShouldBeTrue(); // union
        h.Authorize(u, Open, h.DoorA1, ClientKind.Mobile).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_013_Priority_range_hull_for_alarms_view()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, "alarms.view", new GrantScope(LocationsSubtree: [h.BuildingA]), constraints: new GrantConstraints(Priorities: new PriorityRange(1, 3))),
            Harness.Grant(u, "alarms.view", new GrantScope(Categories: [Category.AccessControl]), constraints: new GrantConstraints(Priorities: new PriorityRange(5, 8))));
        h.Authorize(u, "alarms.view", h.DoorA1).EffectiveConstraints.Priorities.ShouldBe(new PriorityRange(1, 8));
        h.Authorize(u, "alarms.view", h.CameraA).EffectiveConstraints.Priorities.ShouldBe(new PriorityRange(1, 3));
    }

    [Fact]
    public async Task RGT_U_014_System_target_only_for_everything_or_system_permissions()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, "operate.devices.view", new GrantScope(LocationsSubtree: [h.BuildingA])), Harness.Grant(u, "alarms.view"));
        var system = new AuthorizationTarget.System();
        h.Evaluator.Authorize(u, PermissionCode.FromTrusted("operate.devices.view"), system, h.Now).Allowed.ShouldBeFalse();
        h.Evaluator.Authorize(u, PermissionCode.FromTrusted("alarms.view"), system, h.Now).Allowed.ShouldBeTrue();
        await h.Give(u, Harness.Grant(u, "admin.systemEvents.view"));
        h.Evaluator.Authorize(u, PermissionCode.FromTrusted("admin.systemEvents.view"), system, h.Now).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_015_HasPermission_means_anywhere_and_now()
    {
        using var h = new Harness();
        h.Calendars.Daily("night", 22, 23);
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingB])), Harness.Grant(u, "alarms.view", schedule: "night"));
        h.Evaluator.HasPermission(u, PermissionCode.FromTrusted(View), h.Now).ShouldBeTrue();
        h.Evaluator.HasPermission(u, PermissionCode.FromTrusted("alarms.view"), h.Now).ShouldBeFalse();
        h.Evaluator.HasPermission(u, PermissionCode.FromTrusted("alarms.close"), h.Now).ShouldBeFalse();
        h.Evaluator.HasPermission(u, PermissionCode.FromTrusted("alarms.view"), h.Now.AddHours(10)).ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_016_Visible_scope_bitsets_for_devices_and_locations()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])));
        var scope = h.Scope(u, View);
        scope.Everything.ShouldBeFalse();
        scope.ContainsDevice(h.DoorA1).ShouldBeTrue();
        scope.ContainsDevice(h.DoorB).ShouldBeFalse();
        scope.Locations!.ShouldContain(h.Floor1);
        scope.ContainsLocation(h.BuildingA).ShouldBeTrue();
        scope.ContainsLocation(h.BuildingB).ShouldBeFalse();

        await h.Give(u, Harness.Grant(u, View));
        var all = h.Scope(u, View);
        all.Everything.ShouldBeTrue();
        all.Devices.ShouldBeNull();
        all.ContainsDevice(h.CameraB).ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_017_Visible_location_includes_locations_containing_visible_devices()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(Devices: [h.DoorA1])));
        var scope = h.Scope(u, View);
        new[] { h.Floor1, h.BuildingA, h.Root }.ShouldAllBe(l => scope.ContainsLocation(l));
        scope.ContainsLocation(h.BuildingB).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_018_ValidUntil_is_the_next_schedule_transition()
    {
        using var h = new Harness();
        h.Calendars.Daily("office", 8, 18);
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, schedule: "office"));
        h.Scope(u, View).ValidUntil.ShouldBe(new DateTimeOffset(2026, 11, 30, 18, 0, 0, TimeSpan.Zero));
        await h.Give(u, Harness.Grant(u, View));
        h.Scope(u, View).ValidUntil.ShouldBe(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task RGT_U_019_Cache_invalidated_on_grants_change()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])));
        h.Allowed(u, View, h.DoorA1).ShouldBeTrue();
        h.Scope(u, View).ContainsDevice(h.DoorB).ShouldBeFalse();

        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingB])));
        h.Allowed(u, View, h.DoorA1).ShouldBeFalse();
        h.Scope(u, View).ContainsDevice(h.DoorB).ShouldBeTrue();
        await h.Give(u);
        h.Allowed(u, View, h.DoorB).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_020_Cache_invalidated_on_registry_change()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])), Harness.Grant(u, "alarms.view", new GrantScope(TagsAny: [h.TagFront])));
        h.Scope(u, View).ContainsDevice(h.CameraB).ShouldBeFalse();
        h.Allowed(u, "alarms.view", h.CameraA).ShouldBeFalse();

        h.Registry.Move(h.CameraB, h.BuildingA);
        h.Scope(u, View).ContainsDevice(h.CameraB).ShouldBeTrue();
        h.Registry.Retag(h.CameraA, h.TagFront);
        h.Allowed(u, "alarms.view", h.CameraA).ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_U_021_Cache_invalidated_on_calendar_change_and_schedule_edge()
    {
        using var h = new Harness();
        h.Calendars.Daily("shift", 8, 18);
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, schedule: "shift"));
        h.Allowed(u, View, h.DoorA1).ShouldBeTrue();

        h.Calendars.Daily("shift", 14, 18); // calendar edited: no longer active at noon
        h.Allowed(u, View, h.DoorA1).ShouldBeFalse();

        h.Time.Advance(TimeSpan.FromHours(2)); // 14:00 edge crossed
        h.Allowed(u, View, h.DoorA1).ShouldBeTrue();
        h.Time.Advance(TimeSpan.FromHours(4)); // 18:00 edge
        h.Allowed(u, View, h.DoorA1).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_022_RightsChanged_only_when_effective_rights_changed()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])));
        h.Allowed(u, View, h.DoorA1).ShouldBeTrue();
        h.Sink.Calls.Count.ShouldBe(0);

        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA]))); // same grant, new id
        h.Sink.Calls.ShouldBeEmpty();
        h.Registry.Raise(); // registry change that moves nothing
        h.Sink.Calls.ShouldBeEmpty();

        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingB])));
        h.Sink.Calls.Count.ShouldBe(1);
        h.Sink.Calls[0].ShouldBe([u]);
    }

    [Fact]
    public async Task RGT_U_023_Explain_returns_matching_grants_and_why_others_did_not()
    {
        using var h = new Harness();
        h.Calendars.Daily("night", 22, 23);
        var u = h.NewUser();
        var match = Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA]));
        var wrongScope = Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingB]));
        var wrongPermission = Harness.Grant(u, "alarms.view");
        var wrongTime = Harness.Grant(u, View, schedule: "night");
        await h.Give(u, match, wrongScope, wrongPermission, wrongTime);
        var why = h.Evaluator.Explain(u, PermissionCode.FromTrusted(View), Harness.On(h.DoorA1), h.Now).ToDictionary(e => e.Grant.Id, e => e);
        why[match.Id].Matched.ShouldBeTrue();
        why[wrongScope.Id].Reason.ShouldBe("scope");
        why[wrongPermission.Id].Reason.ShouldBe("permission");
        why[wrongTime.Id].Reason.ShouldBe("schedule");
    }

    [Fact]
    public async Task RGT_U_024_Authorize_warm_p99_below_5_microseconds()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, [.. Enumerable.Range(0, 20).Select(i => Harness.Grant(u, i == 0 ? View : "alarms.view", new GrantScope(Devices: [h.DoorB], Servers: [h.Server2], Categories: [Category.AccessControl], Types: i == 0 ? null : [h.Door])))]);
        var code = PermissionCode.FromTrusted(View);
        var target = Harness.On(h.DoorB);
        for (var i = 0; i < 20_000; i++)
        {
            h.Evaluator.Authorize(u, code, target, h.Now);
        }

        const int batch = 100;
        var samples = new double[500];
        for (var s = 0; s < samples.Length; s++)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var i = 0; i < batch; i++)
            {
                h.Evaluator.Authorize(u, code, target, h.Now);
            }

            samples[s] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds / batch;
        }

        Array.Sort(samples);
        samples[(int)(samples.Length * 0.99)].ShouldBeLessThan(5.0);
    }

    [Fact]
    public async Task RGT_U_025_Bitset_memory_for_50000_devices_and_200_users_within_budget()
    {
        using var h = new Harness();
        for (var i = 10; i < 50_010; i++)
        {
            h.Registry.AddDevice(i, i % 2 == 0 ? h.BuildingA : h.BuildingB, i % 3 == 0 ? h.Server1 : h.Server2, h.Door);
        }

        h.Registry.Raise();
        for (var n = 0; n < 200; n++)
        {
            var u = h.NewUser();
            await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [n % 2 == 0 ? h.BuildingA : h.BuildingB], Servers: n % 3 == 0 ? [h.Server1] : null)));
            h.Scope(u, View).ContainsDevice(TestIdsDevice(10 + n)).ShouldBe(h.Scope(u, View).Devices!.Contains(TestIdsDevice(10 + n)));
        }

        h.Evaluator.CachedEntries.ShouldBe(200);
        h.Evaluator.CachedBitsetBytes.ShouldBeLessThanOrEqualTo(200L * 6_256); // 50,000 bits = 6.25 KB per user
    }

    private static DeviceId TestIdsDevice(long n) => Watchpoint.Kernel.Testing.TestIds.Device(n);
}
