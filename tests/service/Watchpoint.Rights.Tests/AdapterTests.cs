using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Commands.Contracts;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Adapters;
using Watchpoint.Rights.Internal.Configuration;
using Watchpoint.Rights.Internal.Groups;
using Xunit;
using ApiVisibleScope = Watchpoint.Api.Contracts.IVisibleScope;

namespace Watchpoint.Rights.Tests;

/// <summary>Adapters (ICurrentUser, CMD, ALM), user groups and the CFG object type (RGT §6.1, §7, §5).</summary>
public sealed class AdapterTests
{
    private const string View = "operate.devices.view";

    private static (CommandDefinition Command, TypeId Type) LockCommand(Harness h)
    {
        var type = h.Types.Add("ac.lock", Category.AccessControl, null, "openable.open");
        return (h.Types.Get(new TypeRef(type, new SemVersion(1, 0, 0))).Value.Commands.Values.Single(), type);
    }

    [Fact]
    public async Task RGT_U_026_Current_user_adapter_filters_permission_and_visible_scope()
    {
        using var h = new Harness();
        var adapter = new PermissionEvaluatorAdapter(h.Evaluator, h.Time);
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])));
        var user = new FakeCurrentUser(u, false);

        (await adapter.HasPermissionAsync(user, PermissionCode.FromTrusted(View), CancellationToken.None)).ShouldBeTrue();
        (await adapter.HasPermissionAsync(user, PermissionCode.FromTrusted("alarms.view"), CancellationToken.None)).ShouldBeFalse();
        var scope = await adapter.GetVisibleScopeAsync(user, PermissionCode.FromTrusted(View), CancellationToken.None);
        scope.IsEverything.ShouldBeFalse();
        scope.Devices!.ShouldBe([h.DoorA1, h.CameraA], ignoreOrder: true);

        var admin = new FakeCurrentUser(h.NewUser(admin: true), true);
        (await adapter.HasPermissionAsync(admin, PermissionCode.FromTrusted("config.rights.edit"), CancellationToken.None)).ShouldBeTrue();
        (await adapter.GetVisibleScopeAsync(admin, PermissionCode.FromTrusted(View), CancellationToken.None)).IsEverything.ShouldBeTrue();
        (await adapter.HasPermissionAsync(FakeCurrentUser.Anonymous, PermissionCode.FromTrusted(View), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_027_Command_authorizer_maps_constraints_and_denials()
    {
        using var h = new Harness();
        h.Calendars.Daily("office", 8, 18);
        var (command, _) = LockCommand(h);
        var authorizer = new CommandAuthorizer(h.Evaluator, h.Time);
        var u = h.NewUser();
        var code = "commands." + command.Path.Value;
        await h.Give(u, Harness.Grant(u, code, schedule: "office", constraints: new GrantConstraints(true, true, true, ClientKinds: new HashSet<ClientKind> { ClientKind.Desktop })),
            Harness.Grant(u, "commands.priority.critical"));
        var device = h.Registry.GetDevice(h.DoorA1).Value;
        var args = new Dictionary<string, Watchpoint.Kernel.Values.WpValue>();
        CommandIssuer Issuer(ClientKind k) => new CommandIssuer.User(u, SessionId.New(), k, null);

        var ok = authorizer.Authorize(Issuer(ClientKind.Desktop), device, command, Risk.Normal, args, h.Now);
        ok.Allowed.ShouldBeTrue();
        ok.Constraints.ReasonRequired.ShouldBeTrue();
        ok.Constraints.ApprovalRequired.ShouldBeTrue();
        ok.Constraints.StepUpRequired.ShouldBeTrue();
        ok.Constraints.AllowedClientKinds!.ShouldBe([ClientKind.Desktop]);
        ok.Constraints.MayRaiseToCritical.ShouldBeTrue();

        authorizer.Authorize(Issuer(ClientKind.Web), device, command, Risk.Normal, args, h.Now).Denial!.Code.Value.ShouldBe("commands.client_kind_not_allowed");
        authorizer.Authorize(Issuer(ClientKind.Desktop), device, command, Risk.Normal, args, h.Now.AddHours(8)).Denial!.Code.Value.ShouldBe("commands.outside_schedule");
        var nobody = h.NewUser();
        authorizer.Authorize(new CommandIssuer.User(nobody, SessionId.New(), ClientKind.Desktop, null), device, command, Risk.Normal, args, h.Now).Denial!.Code.Value.ShouldBe("common.forbidden");
    }

    [Fact]
    public async Task RGT_U_028_Script_identity_is_evaluated_like_a_user()
    {
        using var h = new Harness();
        var (command, _) = LockCommand(h);
        var authorizer = new CommandAuthorizer(h.Evaluator, h.Time);
        var script = h.NewUser(UserKind.Script);
        var device = h.Registry.GetDevice(h.DoorB).Value;
        var issuer = new CommandIssuer.Script(ScriptId.New(), script);
        var args = new Dictionary<string, Watchpoint.Kernel.Values.WpValue>();
        authorizer.Authorize(issuer, device, command, Risk.Normal, args, h.Now).Allowed.ShouldBeFalse();
        await h.Give(script, Harness.Grant(script, "commands.openable.*", new GrantScope(LocationsSubtree: [h.BuildingB])),
            Harness.Grant(script, "operate.devices.view", new GrantScope(LocationsSubtree: [h.BuildingB])));
        authorizer.Authorize(issuer, device, command, Risk.Normal, args, h.Now).Allowed.ShouldBeTrue();
        authorizer.Authorize(issuer, h.Registry.GetDevice(h.DoorA1).Value, command, Risk.Normal, args, h.Now).Allowed.ShouldBeFalse();
        authorizer.VisibleDevices(issuer)!.ShouldBe([h.DoorB, h.CameraB], ignoreOrder: true);
    }

    [Fact]
    public async Task RGT_U_029_Alarm_visibility_carries_the_priority_range()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, "alarms.view", new GrantScope(LocationsSubtree: [h.BuildingA]), constraints: new GrantConstraints(Priorities: new PriorityRange(2, 4))));
        var decision = h.Authorize(u, "alarms.view", h.DoorA1);
        decision.Allowed.ShouldBeTrue();
        decision.EffectiveConstraints.Priorities.ShouldBe(new PriorityRange(2, 4));
        h.Scope(u, "alarms.view").Devices!.ShouldBe([h.DoorA1, h.CameraA], ignoreOrder: true);
        h.Allowed(u, "alarms.view", h.DoorB).ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_U_030_User_group_crud_and_managers_edit_membership_only()
    {
        using var h = new Harness();
        var bulk = new RecordingBulk();
        var groups = new UserGroupService(h.Persistence, h.Evaluator, bulk, h.Time);
        var admin = h.NewUser(admin: true);
        var manager = h.NewUser();
        var stranger = h.NewUser();
        var member = h.NewUser();
        var name = new LocalizedName("Night shift", new Dictionary<string, string>());

        (await groups.CreateAsync(name, null, [], [], stranger, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.Forbidden);
        var g = (await groups.CreateAsync(name, "d", [member], [manager], admin, CancellationToken.None)).Value;
        g.Version.ShouldBe(1);
        groups.Get(g.Id).Value.Managers.ShouldBe([manager]);

        var added = await groups.UpdateAsync(g.Id, new UserGroupUpdate(Members: [member, stranger]), 1, manager, CancellationToken.None);
        added.Value.Members.Count.ShouldBe(2);
        (await groups.UpdateAsync(g.Id, new UserGroupUpdate(Name: name with { Default = "x" }), 2, manager, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.Forbidden);
        (await groups.UpdateAsync(g.Id, new UserGroupUpdate(Managers: [stranger]), 2, manager, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.Forbidden);
        (await groups.UpdateAsync(g.Id, new UserGroupUpdate(Members: []), 2, stranger, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.Forbidden);
        (await groups.UpdateAsync(g.Id, new UserGroupUpdate(Members: []), 1, manager, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        (await groups.UpdateAsync(g.Id, new UserGroupUpdate(Name: name with { Default = "Renamed" }), 2, admin, CancellationToken.None)).Value.Name.Default.ShouldBe("Renamed");

        (await h.Persistence.LoadGroupsAsync(CancellationToken.None)).Single().Version.ShouldBe(3);
        (await groups.DeleteAsync(g.Id, manager, CancellationToken.None)).IsSuccess.ShouldBeFalse();
        (await groups.DeleteAsync(g.Id, admin, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        groups.Get(g.Id).Error.Code.Value.ShouldBe("rights.group.not_found");
    }

    [Fact]
    public async Task RGT_U_031_Group_bulk_actions_call_identity_for_every_member()
    {
        using var h = new Harness();
        var bulk = new RecordingBulk();
        var groups = new UserGroupService(h.Persistence, h.Evaluator, bulk, h.Time);
        var admin = h.NewUser(admin: true);
        var a = h.NewUser();
        var b = h.NewUser();
        var g = (await groups.CreateAsync(new LocalizedName("g", new Dictionary<string, string>()), null, [a, b], [], admin, CancellationToken.None)).Value;
        bulk.FailFor = b;
        var until = h.Now.AddDays(30);

        foreach (var action in Enum.GetValues<GroupBulkAction>())
        {
            var failed = (await groups.BulkAsync(g.Id, action, until, admin, CancellationToken.None)).Value;
            failed.ShouldBe([b]);
        }

        bulk.Calls.Count.ShouldBe(10);
        bulk.Calls.Select(c => c.Action).Distinct().Count().ShouldBe(5);
        bulk.Calls.ShouldAllBe(c => c.Parameter == until);
        (await groups.BulkAsync(g.Id, GroupBulkAction.Disable, null, a, CancellationToken.None)).Error.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task RGT_U_032_Groups_hold_no_rights_grants_document_rejects_group_targets()
    {
        using var h = new Harness();
        var groupId = Guid.NewGuid();
        var type = new GrantsObjectType(h.Store, h.Catalogue, _ => true, id => id == groupId);
        var content = GrantsObjectType.ToContent([Harness.Grant(h.NewUser(), View)]);
        var problems = await type.ValidateAsync(Entry(groupId, content), null!, CancellationToken.None);
        problems.Single().Code.ShouldBe("rights.grant.group_target");
        (await type.ValidateAsync(Entry(Guid.NewGuid(), content), null!, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RGT_U_033_Grants_object_type_validates_loads_and_applies()
    {
        using var h = new Harness();
        var type = new GrantsObjectType(h.Store, h.Catalogue, key => key == "office", _ => false);
        type.Type.ShouldBe("rights.grants");
        type.PermissionFor(ConfigAccess.Edit).Value.ShouldBe("config.rights.edit");
        type.PermissionFor(ConfigAccess.View).Value.ShouldBe("config.rights.view");
        type.PermissionFor(ConfigAccess.Publish).Value.ShouldBe("config.publish");

        var u = h.NewUser();
        var bad = GrantsObjectType.ToContent([Harness.Grant(u, "nope.thing"), Harness.Grant(u, View, schedule: "missing"), Harness.Grant(u, "alarms.mute", new GrantScope(Devices: [h.DoorA1]))]);
        var problems = await type.ValidateAsync(Entry(u.Value, bad), null!, CancellationToken.None);
        problems.Select(p => p.Code).ShouldBe(["rights.permission.unknown", "rights.grant.invalid_scope", "rights.grant.calendar_unknown"], ignoreOrder: true);
        problems.ShouldAllBe(p => p.Severity == ProblemSeverity.Error);

        var good = GrantsObjectType.ToContent([Harness.Grant(u, "alarms.view", new GrantScope(LocationsSubtree: [h.BuildingA], Devices: [h.DoorB]), "office",
            new GrantConstraints(Priorities: new PriorityRange(1, 2)))]);
        (await type.ValidateAsync(Entry(u.Value, good), null!, CancellationToken.None)).ShouldBeEmpty();
        (await type.LoadAsync(u.Value, CancellationToken.None)).ShouldBeNull();

        await type.ApplyAsync(u, good, CancellationToken.None);
        var loaded = (await type.LoadAsync(u.Value, CancellationToken.None))!;
        GrantsObjectType.TryParse(loaded.Content, u, out var parsed, out _).ShouldBeTrue();
        parsed.Single().Scope.LocationsSubtree.ShouldBe([h.BuildingA]);
        parsed.Single().Constraints.Priorities.ShouldBe(new PriorityRange(1, 2));
        h.Store.GetGrants(u).Single().ScheduleCalendar.ShouldBe("office");
        (await type.ValidateAsync(Entry(u.Value, null), null!, CancellationToken.None)).Single().Code.ShouldBe("rights.grant.invalid_document");
    }

    [Fact]
    public async Task RGT_U_034_Me_permissions_lists_the_codes_held_anywhere()
    {
        using var h = new Harness();
        var u = h.NewUser();
        await h.Give(u, Harness.Grant(u, View, new GrantScope(LocationsSubtree: [h.BuildingA])), Harness.Grant(u, "alarms.*"));
        var codes = MyPermissions.Of(h.Catalogue, h.Evaluator, u, false, h.Now);
        codes.ShouldContain(View);
        codes.ShouldContain("alarms.acknowledge");
        codes.ShouldContain("alarms.view");
        codes.ShouldNotContain("config.rights.edit");
        MyPermissions.Of(h.Catalogue, h.Evaluator, u, true, h.Now).Count.ShouldBe(h.Catalogue.All.Count);
    }

    private static DraftEntry Entry(Guid id, System.Text.Json.JsonElement? content) =>
        new(Guid.NewGuid(), new ConfigObjectRef("rights.grants", id), EntryOperation.Update, null, content, DateTimeOffset.UnixEpoch);

    private sealed class RecordingBulk : IUserBulkActions
    {
        public List<(UserId User, GroupBulkAction Action, DateTimeOffset? Parameter)> Calls { get; } = [];

        public UserId? FailFor { get; set; }

        public Task<Result> RunAsync(UserId user, GroupBulkAction action, DateTimeOffset? parameter, UserId actor, CancellationToken ct)
        {
            Calls.Add((user, action, parameter));
            return Task.FromResult(user == FailFor ? Result.Failure(Error.Create(ErrorKind.Unprocessable, "x.failed")) : Result.Success());
        }
    }

    private sealed class FakeCurrentUser(UserId id, bool admin, bool authenticated = true) : ICurrentUser
    {
        public static FakeCurrentUser Anonymous { get; } = new(default, false, false);

        public bool IsAuthenticated => authenticated;

        public UserId UserId => id;

        public SessionId SessionId => default;

        public ClientKind ClientKind => ClientKind.Desktop;

        public bool IsAdministrator => admin;

        public string Language => "en";

        public ValueTask<bool> HasPermissionAsync(PermissionCode permission, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<ApiVisibleScope> GetVisibleScopeAsync(PermissionCode permission, CancellationToken ct) => throw new NotSupportedException();
    }
}
