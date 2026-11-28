using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Catalogue;
using Watchpoint.Rights.Internal.Evaluation;
using Xunit;

namespace Watchpoint.Rights.Tests;

/// <summary>Catalogue, patterns and grant validation (RGT §4, §5).</summary>
public sealed class CatalogueAndGrantTests
{
    [Fact]
    public void RGT_U_001_Catalogue_registers_and_regenerates_command_permissions()
    {
        using var h = new Harness();
        using var generator = new CommandPermissionGenerator(h.Types, h.Catalogue);
        h.Catalogue.Get(PermissionCode.FromTrusted("alarms.view")).IsSuccess.ShouldBeTrue();
        h.Catalogue.Get(PermissionCode.FromTrusted("commands.openable.open")).IsSuccess.ShouldBeFalse();

        h.Types.Add("ac.lock", Category.AccessControl, null, "openable.open");
        h.Types.RaiseChanged();
        var generated = h.Catalogue.Get(PermissionCode.FromTrusted("commands.openable.open")).Value;
        generated.ScopeKind.ShouldBe(PermissionScopeKind.Devices);
        generated.AllowedConstraints.ShouldContain(ConstraintKind.RequireFourEyes);

        h.Catalogue.Register(new PermissionDefinition(PermissionCode.FromTrusted("video.live"), "operation.video", "video", "l", "d", PermissionScopeKind.Devices, new HashSet<ConstraintKind>(), false));
        h.Catalogue.All.ShouldContain(d => d.Code.Value == "video.live");
        h.Catalogue.Get(PermissionCode.FromTrusted("nope.nothing")).Error.Code.Value.ShouldBe("rights.permission.unknown");
    }

    [Fact]
    public void RGT_U_002_Pattern_matching_exact_prefix_and_star()
    {
        using var h = new Harness();
        static PermissionCode P(string s) => PermissionCode.FromTrusted(s);
        h.Catalogue.Matches(P("commands.openable.*"), P("commands.openable.open")).ShouldBeTrue();
        h.Catalogue.Matches(P("commands.openable.*"), P("commands.lighting.on")).ShouldBeFalse();
        h.Catalogue.Matches(P("commands.openable.*"), P("commands.openableX.open")).ShouldBeFalse();
        h.Catalogue.Matches(P("alarms.view"), P("alarms.view")).ShouldBeTrue();
        h.Catalogue.Matches(P("alarms.view"), P("alarms.viewAll")).ShouldBeFalse();
        h.Catalogue.Matches(P("alarms.*"), P("alarms.view")).ShouldBeTrue();
    }

    [Fact]
    public void RGT_U_003_Validation_rejects_unknown_wrong_constraint_wrong_scope_and_duplicates()
    {
        using var h = new Harness();
        var validator = new GrantValidator(new PermissionCatalogueAccess(h.Catalogue));
        var u = UserId.New();
        validator.Validate([Harness.Grant(u, "alarms.view"), Harness.Grant(u, "operate.devices.view", new GrantScope(Devices: [h.DoorA1]))]).ShouldBeEmpty();

        validator.Validate([Harness.Grant(u, "nope.thing")]).Single().Code.Value.ShouldBe("rights.permission.unknown");
        validator.Validate([Harness.Grant(u, "alarms.acknowledge", constraints: new GrantConstraints(RequireReason: true))]).Single().Code.Value.ShouldBe("rights.grant.constraint_not_allowed");
        validator.Validate([Harness.Grant(u, "alarms.mute", new GrantScope(Devices: [h.DoorA1]))]).Single().Code.Value.ShouldBe("rights.grant.invalid_scope");
        validator.Validate([Harness.Grant(u, "operate.devices.view", new GrantScope(true, Servers: [h.Server1]))]).Single().Code.Value.ShouldBe("rights.grant.invalid_scope");
        validator.Validate([Harness.Grant(u, "alarms.view"), Harness.Grant(u, "alarms.view")]).Single().Code.Value.ShouldBe("rights.grant.duplicate");
        validator.Validate([Harness.Grant(u, "alarms.view", constraints: new GrantConstraints(ClientKinds: new HashSet<Watchpoint.Identity.Contracts.ClientKind>()))])
            .Single().Code.Value.ShouldBe("rights.grant.constraint_not_allowed");
    }
}
