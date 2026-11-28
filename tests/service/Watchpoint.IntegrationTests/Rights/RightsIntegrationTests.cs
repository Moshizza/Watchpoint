using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Commands.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.Rights;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Adapters;
using Watchpoint.Rights.Internal.Evaluation;
using Watchpoint.Rights.Internal.Storage;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Xunit;
using RegistryChangeContext = Watchpoint.Registry.Contracts.ChangeContext;

namespace Watchpoint.IntegrationTests.Rights;

/// <summary>
/// Rights with a real PostgreSQL database and the real registry (spec RGT §15). Each test starts nodes on one database; the
/// evaluator, grant store, persistence, registry and adapters are the production ones.
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "RGT")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
public sealed class RightsIntegrationTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var o in _owned)
        {
            await o.DisposeAsync();
        }
    }

    [Fact]
    public async Task RGT_I_001_Operator_with_building_A_grants_sees_only_building_A()
    {
        await using var node = await StartAsync(nameof(RGT_I_001_Operator_with_building_A_grants_sees_only_building_A));
        var site = await SeedAsync(node);
        var op = node.AddUser();
        await node.Store.ReplaceAsync(op, [Grant(op, "operate.devices.view", new GrantScope(LocationsSubtree: [site.BuildingA]))], Ct);

        var scope = node.Evaluator.GetVisibleScope(op, PermissionCode.FromTrusted("operate.devices.view"), Noon);
        scope.ContainsDevice(site.DoorA).ShouldBeTrue();
        scope.ContainsDevice(site.DoorB).ShouldBeFalse();

        // what GET /api/v1/devices does with the visible scope (REG §11)
        var current = new Principal(op, false);
        var apiScope = await node.PermissionAdapter.GetVisibleScopeAsync(current, PermissionCode.FromTrusted("operate.devices.view"), Ct);
        var listed = node.Reader.QueryDevices(new DeviceQuery(Within: apiScope.Devices), new Watchpoint.Kernel.Paging.PageRequest(100)).Items;
        listed.Select(d => d.Id).ShouldBe([site.DoorA]);
        (await node.PermissionAdapter.GetVisibleScopeAsync(new Principal(node.AddUser(admin: true), true), PermissionCode.FromTrusted("operate.devices.view"), Ct)).IsEverything.ShouldBeTrue();
    }

    [Fact]
    public async Task RGT_I_002_Rights_reduced_while_connected_publishes_rights_changed_and_denies_the_next_call()
    {
        await using var node = await StartAsync(nameof(RGT_I_002_Rights_reduced_while_connected_publishes_rights_changed_and_denies_the_next_call));
        var site = await SeedAsync(node);
        var op = node.AddUser();
        var view = PermissionCode.FromTrusted("operate.devices.view");
        await node.Store.ReplaceAsync(op, [Grant(op, "operate.devices.view")], Ct);
        (await node.PermissionAdapter.HasPermissionAsync(new Principal(op, false), view, Ct)).ShouldBeTrue();
        node.Evaluator.GetVisibleScope(op, view, Noon).ContainsDevice(site.DoorB).ShouldBeTrue(); // the connected client's live scope

        await node.Store.ReplaceAsync(op, [Grant(op, "operate.devices.view", new GrantScope(LocationsSubtree: [site.BuildingA]))], Ct);

        node.Sink.Calls.Single().ShouldBe([op]); // the API re-filters its live subscriptions on this (API §8.6)
        node.Evaluator.GetVisibleScope(op, view, Noon).ContainsDevice(site.DoorB).ShouldBeFalse();
        node.Evaluator.Authorize(op, view, new AuthorizationTarget.Device(site.DoorB), Noon).Allowed.ShouldBeFalse();

        await node.Store.ReplaceAsync(op, [], Ct);
        (await node.PermissionAdapter.HasPermissionAsync(new Principal(op, false), view, Ct)).ShouldBeFalse(); // 403 on the next REST call
    }

    [Fact]
    public async Task RGT_I_003_Command_denied_outside_schedule()
    {
        await using var node = await StartAsync(nameof(RGT_I_003_Command_denied_outside_schedule));
        var site = await SeedAsync(node);
        var op = node.AddUser();
        await node.Store.ReplaceAsync(op, [Grant(op, "commands.openable.open", schedule: "office")], Ct);
        var authorizer = new CommandAuthorizer(node.Evaluator, node.Time);
        var command = OpenCommand();
        var device = node.Reader.GetDevice(site.DoorA).Value;
        var issuer = new CommandIssuer.User(op, SessionId.New(), ClientKind.Desktop, null);

        authorizer.Authorize(issuer, device, command, Risk.Normal, new Dictionary<string, WpValue>(), Noon).Allowed.ShouldBeTrue();
        var denied = authorizer.Authorize(issuer, device, command, Risk.Normal, new Dictionary<string, WpValue>(), Noon.AddHours(9));
        denied.Allowed.ShouldBeFalse();
        denied.Denial!.Code.Value.ShouldBe("commands.outside_schedule");
    }

    [Fact]
    public async Task RGT_I_004_Four_eyes_command_via_grant_constraint()
    {
        await using var node = await StartAsync(nameof(RGT_I_004_Four_eyes_command_via_grant_constraint));
        var site = await SeedAsync(node);
        var op = node.AddUser();
        var approver = node.AddUser();
        await node.Store.ReplaceAsync(op, [Grant(op, "commands.openable.open", constraints: new GrantConstraints(RequireReason: true, RequireFourEyes: true))], Ct);
        await node.Store.ReplaceAsync(approver, [Grant(approver, "commands.approve", new GrantScope(LocationsSubtree: [site.BuildingA]))], Ct);
        var authorizer = new CommandAuthorizer(node.Evaluator, node.Time);
        var device = node.Reader.GetDevice(site.DoorA).Value;

        var decision = authorizer.Authorize(new CommandIssuer.User(op, SessionId.New(), ClientKind.Desktop, null), device, OpenCommand(), Risk.Normal, new Dictionary<string, WpValue>(), Noon);
        decision.Allowed.ShouldBeTrue();
        decision.Constraints.ApprovalRequired.ShouldBeTrue();
        decision.Constraints.ReasonRequired.ShouldBeTrue();
        node.Evaluator.Authorize(approver, PermissionCode.FromTrusted("commands.approve"), new AuthorizationTarget.Device(site.DoorA), Noon).Allowed.ShouldBeTrue();
        node.Evaluator.Authorize(approver, PermissionCode.FromTrusted("commands.approve"), new AuthorizationTarget.Device(site.DoorB), Noon).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task RGT_I_008_Grants_and_groups_persist_across_restarts()
    {
        var name = nameof(RGT_I_008_Grants_and_groups_persist_across_restarts);
        var db = await postgres.CreateDatabaseAsync(nameof(RightsIntegrationTests) + "." + name, Ct);
        _owned.Add(db);
        var user = UserId.New();
        var other = UserId.New();
        var groupId = UserGroupId.New();
        var calendarGrant = new RightGrant(RightGrantId.New(), user, PermissionCode.FromTrusted("alarms.view"),
            new GrantScope(LocationsSubtree: [LocationId.New()], Categories: [Watchpoint.Model.Documents.Category.Video], Types: [TypeId.FromTrusted("video.camera")],
                TagsAny: [TagId.New()], TagsExclude: [TagId.New()], Devices: [DeviceId.New()]), "office",
            new GrantConstraints(Priorities: new PriorityRange(1, 4), ClientKinds: new HashSet<ClientKind> { ClientKind.Web }, MaxLookback: TimeSpan.FromMinutes(30)),
            new GrantSource.CopiedFrom(other, Guid.NewGuid(), Noon, true), Noon, other);
        var plain = Grant(user, "operate.devices.view");

        await using (var first = await StartAsync(name, db))
        {
            await first.Store.ReplaceAsync(user, [calendarGrant, plain], Ct);
            await first.Persistence.SaveGroupAsync(new UserGroup(groupId, new LocalizedName("Night", new Dictionary<string, string> { ["fr"] = "Nuit" }), "d", [user, other], [other], 3), Ct);
            await first.Store.ReplaceAsync(other, [Grant(other, "alarms.view")], Ct);
            await first.Store.ReplaceAsync(other, [], Ct);
        }

        await using var second = await StartAsync(name, db);
        var loaded = second.Store.GetGrants(user);
        loaded.Count.ShouldBe(2);
        var restored = loaded.Single(g => g.Permission.Value == "alarms.view");
        restored.Id.ShouldBe(calendarGrant.Id);
        restored.ScheduleCalendar.ShouldBe("office");
        restored.Scope.Categories.ShouldBe(calendarGrant.Scope.Categories);
        restored.Scope.Types.ShouldBe(calendarGrant.Scope.Types);
        restored.Scope.TagsExclude.ShouldBe(calendarGrant.Scope.TagsExclude);
        restored.Scope.Devices.ShouldBe(calendarGrant.Scope.Devices);
        restored.Constraints.Priorities.ShouldBe(new PriorityRange(1, 4));
        restored.Constraints.ClientKinds.ShouldBe([ClientKind.Web]);
        restored.Constraints.MaxLookback.ShouldBe(TimeSpan.FromMinutes(30));
        restored.Source.ShouldBeOfType<GrantSource.CopiedFrom>().SourceWasTemplate.ShouldBeTrue();
        second.Store.GetGrants(other).ShouldBeEmpty();

        var group = (await second.Persistence.LoadGroupsAsync(Ct)).Single();
        group.Name.Default.ShouldBe("Night");
        group.Name.Translations["fr"].ShouldBe("Nuit");
        group.Members.ShouldBe([user, other], ignoreOrder: true);
        group.Managers.ShouldBe([other]);
        group.Version.ShouldBe(3);

        // the unique (user, identity) index backs up the duplicate validation
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'wp_rights' AND indexname = 'ix_grants_user_id_identity_hash'", connection);
        ((long)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(1);
    }

    private static RightGrant Grant(UserId user, string permission, GrantScope? scope = null, string? schedule = null, GrantConstraints? constraints = null) =>
        new(RightGrantId.New(), user, PermissionCode.FromTrusted(permission), scope ?? GrantScope.All, schedule, constraints ?? GrantConstraints.None, new GrantSource.Manual(), Noon, user);

    private static CommandDefinition OpenCommand() => new(CommandPath.FromTrusted("openable.open"),
        new ParameterSchema(System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()), Risk.Normal,
        new Dictionary<string, IReadOnlyDictionary<string, Risk>>(), TimeSpan.FromSeconds(5), false, false, null);

    private async Task<Node> StartAsync(string name, PostgresDatabase? existing = null)
    {
        var db = existing ?? await postgres.CreateDatabaseAsync(nameof(RightsIntegrationTests) + "." + name, Ct);
        if (existing is null)
        {
            _owned.Add(db);
        }

        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        }).Build();
        var users = new TestUsers();
        var sink = new CollectingSink();
        var time = new FixedTime(Noon);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointModel();
        services.AddWatchpointRegistry(SiteId.New());
        services.AddSingleton<IRightsUserLookup>(users);
        services.AddSingleton<IRightsChangedSink>(sink);
        services.AddSingleton<IRightsCalendars, OfficeHours>();
        services.AddWatchpointRights();
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        var store = provider.GetRequiredService<GrantStore>();
        await store.LoadAsync(Ct);
        return new Node(provider, users, sink, time);
    }

    private static async Task<Site3> SeedAsync(Node node)
    {
        var siteId = SiteId.New();
        var root = LocationId.New();
        var a = LocationId.New();
        var b = LocationId.New();
        var server = ServerId.New();
        var doorA = DeviceId.New();
        var doorB = DeviceId.New();
        static LocalizedName N(string s) => new(s, new Dictionary<string, string>());
        TypeRef door = new(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value);
        Device Door(DeviceId id, LocationId at, string native) => new(id, door, server, null, native, null, "v", at, [], new Dictionary<string, WpValue>(),
            new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, true, false, false, default, 0);
        var batch = new RegistryChangeBatch([
            new RegistryChange.UpdateSite(new Site(siteId, N("Site"), "UTC", 0)),
            new RegistryChange.UpsertLocation(new Location(root, null, N("Root"), LocationKind.Site, "", 0, null, null, null, 0, 0)),
            new RegistryChange.UpsertLocation(new Location(a, root, N("A"), LocationKind.Building, "", 0, null, null, null, 0, 0)),
            new RegistryChange.UpsertLocation(new Location(b, root, N("B"), LocationKind.Building, "", 0, null, null, null, 1, 0)),
            new RegistryChange.UpsertServer(new Server(server, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(), null, "srv", null, "v", a, [],
                new Dictionary<string, WpValue>(), true, false, 0)),
            new RegistryChange.UpsertDevice(Door(doorA, a, "door-a")),
            new RegistryChange.UpsertDevice(Door(doorB, b, "door-b")),
        ]);
        (await node.Writer.ApplyAsync(batch, new RegistryChangeContext(UserId.New(), "test", null, "seed"), Ct)).IsSuccess.ShouldBeTrue();
        return new Site3(a, b, doorA, doorB);
    }

    private sealed record Site3(LocationId BuildingA, LocationId BuildingB, DeviceId DoorA, DeviceId DoorB);

    private sealed class Node(ServiceProvider provider, TestUsers users, CollectingSink sink, FixedTime time) : IAsyncDisposable
    {
        public CollectingSink Sink => sink;

        public FixedTime Time => time;

        public IRegistryWriter Writer => provider.GetRequiredService<IRegistryWriter>();

        public IRegistryReader Reader => provider.GetRequiredService<IRegistryReader>();

        public IRightsEvaluator Evaluator => provider.GetRequiredService<IRightsEvaluator>();

        public IGrantStore Store => provider.GetRequiredService<IGrantStore>();

        internal IRightsPersistence Persistence => provider.GetRequiredService<IRightsPersistence>();

        public IPermissionEvaluator PermissionAdapter => provider.GetRequiredService<IPermissionEvaluator>();

        public UserId AddUser(bool admin = false)
        {
            var id = UserId.New();
            users.Users[id] = new RightsUser(UserKind.Person, true, admin, null, null);
            return id;
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class TestUsers : IRightsUserLookup
    {
        public Dictionary<UserId, RightsUser> Users { get; } = [];

        public event EventHandler<UserId>? UserChanged
        {
            add { }
            remove { }
        }

        public RightsUser? Find(UserId id) => Users.GetValueOrDefault(id);
    }

    private sealed class CollectingSink : IRightsChangedSink
    {
        public List<IReadOnlyCollection<UserId>> Calls { get; } = [];

        public void RightsChanged(IReadOnlyCollection<UserId> users) => Calls.Add(users);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class OfficeHours : IRightsCalendars
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Exists(string calendarKey) => true;

        public bool IsActive(string calendarKey, DateTimeOffset instant) => instant.Hour is >= 8 and < 18;

        public DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after) =>
            after.Hour < 8 ? after.Date.AddHours(8) : after.Hour < 18 ? after.Date.AddHours(18) : after.Date.AddDays(1).AddHours(8);
    }

    private sealed class Principal(UserId id, bool admin) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public UserId UserId => id;

        public SessionId SessionId => default;

        public ClientKind ClientKind => ClientKind.Desktop;

        public bool IsAdministrator => admin;

        public string Language => "en";

        public ValueTask<bool> HasPermissionAsync(PermissionCode permission, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<Watchpoint.Api.Contracts.IVisibleScope> GetVisibleScopeAsync(PermissionCode permission, CancellationToken ct) => throw new NotSupportedException();
    }
}
