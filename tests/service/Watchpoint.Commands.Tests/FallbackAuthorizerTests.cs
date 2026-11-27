using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Xunit;
using ChangeContext = Watchpoint.Identity.Contracts.ChangeContext;

namespace Watchpoint.Commands.Tests;

/// <summary>The M2 fallback authorizer (spec CMD §5; T2.01).</summary>
[Trait("layer", "U")]
[Trait("spec", "CMD")]
public sealed class FallbackAuthorizerTests
{
    [Fact]
    public async Task CMD_U_011_Fallback_authorizer_before_RGT()
    {
        var users = new FakeUsers();
        var admin = users.Add(1, administrator: true);
        var operatorId = users.Add(2, administrator: false);
        var disabled = users.Add(3, administrator: true, enabled: false);
        var authorizer = new FallbackCommandAuthorizer(users);
        using var h = new CommandsHarness();
        var device = h.Registry.Devices[h.Door];
        var now = TestTime.Start;
        CommandAuthorization Decide(UserId user, CommandPath path, Risk? risk = null)
        {
            var definition = CommandsHarness.DoorType.Commands[path];
            return authorizer.Authorize(h.User(user), device, definition, risk ?? definition.Risk, new Dictionary<string, WpValue>(), now);
        }

        // Administrators run everything, may raise to critical, need no approval.
        foreach (var path in CommandsHarness.DoorType.Commands.Keys)
        {
            var decision = Decide(admin, path);
            decision.Allowed.ShouldBeTrue(path.Value);
            decision.Constraints.ShouldBe(new CommandConstraints(MayRaiseToCritical: true));
        }

        // Other users: normal and sensitive only; nobody needs approval.
        Decide(operatorId, CommandsHarness.Open).Constraints.ShouldBe(CommandConstraints.None);
        Decide(operatorId, CommandsHarness.Reset).Allowed.ShouldBeTrue();
        var critical = Decide(operatorId, CommandsHarness.Lockdown);
        critical.Allowed.ShouldBeFalse();
        critical.Denial!.Code.Value.ShouldBe("common.forbidden");
        critical.Denial.Args["permission"]!.ToString().ShouldBe("commands.lockdown.engage");
        Decide(operatorId, CommandsHarness.UnlockFor, Risk.Critical).Allowed.ShouldBeFalse(); // the effective risk counts

        // Unknown and disabled identities run nothing; automations are judged by their identity.
        Decide(disabled, CommandsHarness.Open).Allowed.ShouldBeFalse();
        Decide(TestIds.User(99), CommandsHarness.Open).Allowed.ShouldBeFalse();
        authorizer.Authorize(new CommandIssuer.Automation(TestIds.Of<AutomationId>(1), TestIds.Of<AutomationRunId>(1), operatorId), device,
            CommandsHarness.DoorType.Commands[CommandsHarness.Open], Risk.Normal, new Dictionary<string, WpValue>(), now).Allowed.ShouldBeTrue();

        // Approvals: administrators only.
        var command = new Command
        {
            Id = CommandId.New(),
            Target = h.Door,
            Path = CommandsHarness.Open,
            Arguments = new Dictionary<string, WpValue>(),
            Issuer = h.User(operatorId),
            EffectiveRisk = Risk.Normal,
            Priority = CommandPriority.High,
            Timeout = TimeSpan.FromSeconds(1),
            ExpiresAt = now,
            State = CommandState.AwaitingApproval,
            RequestedAt = now,
        };
        authorizer.CanApprove(admin, command, now).ShouldBeTrue();
        authorizer.CanApprove(operatorId, command, now).ShouldBeFalse();
        authorizer.CanApprove(disabled, command, now).ShouldBeFalse();

        // Through the service: the operator's critical request is refused by the fallback.
        using var fallback = new CommandsHarness(authorizer: authorizer);
        var service = fallback.Service;
        (await service.RequestAsync(fallback.Request(CommandsHarness.Lockdown, issuer: fallback.User(operatorId), reason: "r"), TestContext.Current.CancellationToken))
            .ShouldFailWith("common.forbidden");
        (await service.RequestAsync(fallback.Request(issuer: fallback.User(operatorId)), TestContext.Current.CancellationToken)).ShouldBeSuccess();

        // The module registers the fallback only when no authorizer (RGT's) is registered.
        await using (var provider = Services(users, rights: null))
        {
            provider.GetRequiredService<ICommandAuthorizer>().ShouldBeOfType<FallbackCommandAuthorizer>();
            provider.GetRequiredService<ICommandService>().ShouldNotBeNull();
            provider.GetServices<IPartitionSubscriber>().ShouldContain(s => s.Name == "commands" && s.Order == 200);
        }

        var rgt = new TestAuthorizer();
        await using (var provider = Services(users, rgt))
        {
            provider.GetRequiredService<ICommandAuthorizer>().ShouldBeSameAs(rgt);
        }
    }

    private static ServiceProvider Services(IUserDirectory users, ICommandAuthorizer? rights)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWatchpointModel();
        services.AddWatchpointPipeline(new ConfigurationBuilder().Build());
        services.AddSingleton(new EventLogEndpoint("nats://127.0.0.1:4222", null)); // never connected: nothing is appended
        services.AddSingleton(users);
        services.AddSingleton<IRegistryReader>(new FakeRegistry());
        services.AddSingleton<ITypeCatalog>(new FakeTypes());
        services.AddSingleton<IStepUpValidator>(new FakeStepUp());
        services.AddSingleton<ICommandConnections>(new FakeConnections());
        services.AddSingleton<Integrations.Contracts.IConnectionCommandChannel>(new FakeChannel());
        if (rights is not null)
        {
            services.AddSingleton(rights);
        }

        services.AddWatchpointCommands(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commands:DefaultExpiry"] = "PT2M" }).Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    private sealed class FakeUsers : IUserDirectory
    {
        private readonly Dictionary<UserId, User> _users = [];

        public event EventHandler<UserChangedEventArgs>? Changed { add { } remove { } }

        public UserId Add(long n, bool administrator, bool enabled = true)
        {
            var id = TestIds.User(n);
            _users[id] = new User(id, "u" + n, "User " + n, null, null, "en", null, UserKind.Person, enabled, administrator, AuthSource.Local,
                null, null, null, null, new HashSet<ClientKind>(), [], null!, default, null, false, null, null, 1);
            return id;
        }

        public Result<User> Get(UserId id) => _users.TryGetValue(id, out var user) ? user : Error.NotFound("identity.user.not_found", ("id", id.Value));

        public Result<User> FindByLogin(string login) => throw new NotSupportedException();

        public Page<User> Query(UserQuery query, PageRequest page) => throw new NotSupportedException();

        public Task<Result<UserId>> CreateAsync(NewUser user, string? initialPassword, ChangeContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result> UpdateAsync(UserId id, UserUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result> DeleteAsync(UserId id, ChangeContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result> SetPasswordAsync(UserId id, string newPassword, bool mustChange, ChangeContext ctx, CancellationToken ct) => throw new NotSupportedException();

        public Task<Result> ChangeOwnPasswordAsync(UserId id, string current, string newPassword, SessionId? currentSession, CancellationToken ct) => throw new NotSupportedException();
    }
}
