using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Watchpoint.Configuration.Adapters;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Host.Modules;
using Watchpoint.IntegrationTests.Integrations;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Scheduling;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Configuration;

/// <summary>
/// Publish end to end (spec CFG §13, CFG-I-001…006, 008; T3.02): a node with the registry and the configuration modules
/// on PostgreSQL and NATS; the registry is the module that applies (ALM and IRT types come with their tasks).
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "CFG")]
public sealed class ConfigurationIntegrationTests(PostgresContainerFixture postgres)
{
    private static readonly UserId _alice = UserId.From(Guid.Parse("0a000000-0000-0000-0000-00000000a11c"));
    private static readonly UserId _bob = UserId.From(Guid.Parse("0b000000-0000-0000-0000-000000000b0b"));
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CFG_I_001_Publish_across_registry_types_in_one_transaction_with_real_PostgreSQL()
    {
        await using var node = await StartAsync("i001");
        var (_, door) = await SeedAsync(node);
        var revision = node.Registry.Revision;
        var lobby = Guid.CreateVersion7();
        var family = Guid.CreateVersion7();
        var tag = Guid.CreateVersion7();
        var drafts = Drafts(node);

        var draft = await NewDraftAsync(node);
        draft = await SetAsync(node, draft, "registry.location", lobby, EntryOperation.Create, $$"""{"name":{"default":"Lobby"},"kind":"zone","parentId":"{{node.Root.Value}}"}""");
        draft = await SetAsync(node, draft, "registry.tagFamily", family, EntryOperation.Create, """{"key":"zone","name":{"default":"Zone"},"exclusive":true}""");
        draft = await SetAsync(node, draft, "registry.tag", tag, EntryOperation.Create, $$"""{"key":"public","name":{"default":"Public"},"familyId":"{{family}}"}""");
        var live = RegistryDocuments.Load(node.Registry, "registry.device", door.Value)!;
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(live.Content)!;
        edited["name"] = JsonSerializer.SerializeToElement(new { @default = "Main door" });
        edited["locationId"] = JsonSerializer.SerializeToElement(lobby.ToString("D"));
        edited["tags"] = JsonSerializer.SerializeToElement(new[] { tag.ToString("D") });
        draft = await SetAsync(node, draft, "registry.device", door.Value, EntryOperation.Update, JsonSerializer.Serialize(edited), live.Version);
        draft.Problems.Where(p => p.Severity == ProblemSeverity.Error).ShouldBeEmpty(string.Join("; ", draft.Problems.Select(p => p.Code + " " + p.Path)));

        var outcome = await PublishAsync(node, draft);

        outcome.ShouldBe(new PublishOutcome(DraftStatus.Published, 1));
        var device = node.Registry.GetDevice(door).Value;
        device.DisplayName!.Default.ShouldBe("Main door");
        device.LocationId.Value.ShouldBe(lobby);
        device.Tags.ShouldBe([TagId.From(tag)]);
        node.Registry.GetLocation(LocationId.From(lobby)).Value.ParentId.ShouldBe(node.Root);
        node.Registry.Revision.ShouldBe(revision + 1, "one registry batch for the whole change set");
        (await node.ScalarAsync<long>("SELECT count(*) FROM wp_config.versions", Ct)).ShouldBe(1);
        (await node.ScalarAsync<long>("SELECT count(*) FROM wp_config.version_entries WHERE version = 1", Ct)).ShouldBe(4);
        (await node.ScalarAsync<string>("SELECT status FROM wp_config.drafts WHERE id = @id", Ct, ("id", draft.Id.Value))).ShouldBe("Published");
        var detail = (await Versions(node).GetAsync(1, Ct)).Value;
        detail.Diff.Single(d => d.Ref.Type == "registry.device").Changes.Select(c => c.Path).ShouldContain("/locationId");

        // config.published on WP_CFG.
        // Appended before the publish returns.
        var published = (await ReadCfgAsync(node)).Single(r => r.Envelope.Kind == "config.published");
        published.Envelope.Body.GetProperty("version").GetInt64().ShouldBe(1);
        published.Envelope.Body.GetProperty("changes").GetArrayLength().ShouldBe(4);
        _ = drafts;
    }

    [Fact]
    public async Task CFG_I_002_Crash_between_commit_and_CompleteApply_the_restart_loads_the_committed_state()
    {
        await using var node = await StartAsync("i002", crashAfterCommit: true);
        var lobby = Guid.CreateVersion7();
        var draft = await NewDraftAsync(node);
        draft = await SetAsync(node, draft, "registry.location", lobby, EntryOperation.Create, $$"""{"name":{"default":"Lobby"},"kind":"zone","parentId":"{{node.Root.Value}}"}""");

        (await PublishAsync(node, draft)).Status.ShouldBe(DraftStatus.Published);
        node.Registry.GetLocation(LocationId.From(lobby)).IsSuccess.ShouldBeFalse("the node 'crashed' before the in-memory swap");
        (await node.ScalarAsync<long>("SELECT count(*) FROM wp_config.versions", Ct)).ShouldBe(1);

        await node.RestartAsync(Ct);

        node.Registry.GetLocation(LocationId.From(lobby)).Value.Name.Default.ShouldBe("Lobby");
        (await Versions(node).GetAsync(1, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task CFG_I_003_Concurrent_publishes_are_serialised()
    {
        await using var node = await StartAsync("i003");
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToList();
        var drafts = new List<Draft>();
        foreach (var id in ids)
        {
            var draft = await NewDraftAsync(node);
            drafts.Add(await SetAsync(node, draft, "registry.location", id, EntryOperation.Create, $$"""{"name":{"default":"Room {{id:N}}"},"kind":"room","parentId":"{{node.Root.Value}}"}"""));
        }

        var outcomes = await Task.WhenAll(drafts.Select(d => PublishAsync(node, d)));

        outcomes.Select(o => o.Version!.Value).Order().ShouldBe([1L, 2L, 3L, 4L]);
        ids.ShouldAllBe(id => node.Registry.GetLocation(LocationId.From(id)).IsSuccess);
        (await node.ScalarAsync<long>("SELECT count(DISTINCT draft_id) FROM wp_config.versions", Ct)).ShouldBe(4);
    }

    [Fact]
    public async Task CFG_I_004_Conflict_and_merge_with_two_configurators()
    {
        await using var node = await StartAsync("i004");
        var lobby = Guid.CreateVersion7();
        var create = await NewDraftAsync(node);
        create = await SetAsync(node, create, "registry.location", lobby, EntryOperation.Create, $$"""{"name":{"default":"Lobby"},"kind":"zone","parentId":"{{node.Root.Value}}"}""");
        await PublishAsync(node, create);
        var root = node.Root.Value;

        // Alice renames, Bob changes the kind; Bob publishes first.
        var alice = await NewDraftAsync(node, _alice);
        alice = await SetAsync(node, alice, "registry.location", lobby, EntryOperation.Update, $$"""{"name":{"default":"Main lobby"},"kind":"zone","parentId":"{{root}}"}""", 1, _alice);
        var bob = await NewDraftAsync(node, _bob);
        bob = await SetAsync(node, bob, "registry.location", lobby, EntryOperation.Update, $$"""{"name":{"default":"Lobby"},"kind":"room","parentId":"{{root}}"}""", 1, _bob);
        await PublishAsync(node, bob, _bob);

        var publisher = node.Services.GetRequiredService<IDraftPublisher>();
        var refused = await publisher.PublishDraftAsync(alice.Id, _alice, alice.Version, null, null, Ct);
        refused.Error.Code.Value.ShouldBe(ConfigurationErrors.PublishConflict);
        alice = (await Drafts(node).GetAsync(alice.Id, _alice, Ct)).Value;

        var rebased = (await publisher.RebaseEntryAsync(alice.Id, _alice, alice.Version, alice.Entries[0].EntryId, null, Ct)).Value;
        rebased.Conflicts.ShouldBeEmpty();
        rebased.LiveVersion.ShouldBe(2);
        await PublishAsync(node, rebased.Draft, _alice);

        var location = node.Registry.GetLocation(LocationId.From(lobby)).Value;
        location.Name.Default.ShouldBe("Main lobby");
        location.Kind.ShouldBe(LocationKind.Room);
    }

    [Fact]
    public async Task CFG_I_005_Rollback_end_to_end_restores_the_previous_device()
    {
        await using var node = await StartAsync("i005");
        var (_, door) = await SeedAsync(node);
        var before = node.Registry.GetDevice(door).Value;
        var wing = Guid.CreateVersion7();
        var live = RegistryDocuments.Load(node.Registry, "registry.device", door.Value)!;
        var edited = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(live.Content)!;
        edited["maintenance"] = JsonSerializer.SerializeToElement(true);
        edited["name"] = JsonSerializer.SerializeToElement(new { @default = "Door (works)" });
        var draft = await NewDraftAsync(node);
        draft = await SetAsync(node, draft, "registry.device", door.Value, EntryOperation.Update, JsonSerializer.Serialize(edited), live.Version);
        draft = await SetAsync(node, draft, "registry.location", wing, EntryOperation.Create, $$"""{"name":{"default":"Wing"},"kind":"building","parentId":"{{node.Root.Value}}"}""");
        await PublishAsync(node, draft);
        node.Registry.GetDevice(door).Value.Maintenance.ShouldBeTrue();

        var rollbackId = (await Versions(node).CreateRollbackDraftAsync(1, _bob, restore: false, Ct)).Value;
        var rollback = (await Drafts(node).GetAsync(rollbackId, _bob, Ct)).Value;
        (await PublishAsync(node, rollback, _bob)).Version.ShouldBe(2);

        var after = node.Registry.GetDevice(door).Value;
        after.Maintenance.ShouldBe(before.Maintenance);
        after.DisplayName?.Default.ShouldBe(before.DisplayName?.Default);
        node.Registry.GetLocation(LocationId.From(wing)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task CFG_I_006_Scheduled_publish_end_to_end()
    {
        await using var node = await StartAsync("i006");
        var lobby = Guid.CreateVersion7();
        var draft = await NewDraftAsync(node);
        draft = await SetAsync(node, draft, "registry.location", lobby, EntryOperation.Create, $$"""{"name":{"default":"Lobby"},"kind":"zone","parentId":"{{node.Root.Value}}"}""");

        var at = DateTimeOffset.UtcNow.AddSeconds(2);
        var outcome = (await node.Services.GetRequiredService<IDraftPublisher>().PublishDraftAsync(draft.Id, _alice, draft.Version, "at night", at, Ct)).Value;

        outcome.Status.ShouldBe(DraftStatus.Scheduled);
        node.Registry.GetLocation(LocationId.From(lobby)).IsSuccess.ShouldBeFalse();
        await Eventually.UntilAsync(() => node.Registry.GetLocation(LocationId.From(lobby)).IsSuccess, _wait, "published at its time", ct: Ct);
        (await node.ScalarAsync<string>("SELECT comment FROM wp_config.versions WHERE version = 1", Ct)).ShouldBe("at night");
        (await node.ScalarAsync<string>("SELECT status FROM wp_config.drafts WHERE id = @id", Ct, ("id", draft.Id.Value))).ShouldBe("Published");
    }

    [Fact]
    public async Task CFG_I_008_Approval_end_to_end()
    {
        await using var node = await StartAsync("i008", settings: new Dictionary<string, string?> { ["Configuration:ApprovalRequiredFor:0"] = "registry.location" });
        var lobby = Guid.CreateVersion7();
        var draft = await NewDraftAsync(node);
        draft = await SetAsync(node, draft, "registry.location", lobby, EntryOperation.Create, $$"""{"name":{"default":"Lobby"},"kind":"zone","parentId":"{{node.Root.Value}}"}""");
        var publisher = node.Services.GetRequiredService<IDraftPublisher>();

        (await publisher.PublishDraftAsync(draft.Id, _alice, draft.Version, "please check", null, Ct)).Value.Status.ShouldBe(DraftStatus.AwaitingApproval);
        (await node.ScalarAsync<string>("SELECT status FROM wp_config.drafts WHERE id = @id", Ct, ("id", draft.Id.Value))).ShouldBe("AwaitingApproval");
        node.Registry.GetLocation(LocationId.From(lobby)).IsSuccess.ShouldBeFalse();
        (await publisher.ApproveAsync(draft.Id, _alice, Ct)).Error.Code.Value.ShouldBe(ConfigurationErrors.ApprovalSelfForbidden);

        var approved = (await publisher.ApproveAsync(draft.Id, _bob, Ct)).Value;

        approved.ShouldBe(new PublishOutcome(DraftStatus.Published, 1));
        node.Registry.GetLocation(LocationId.From(lobby)).IsSuccess.ShouldBeTrue();
        (await node.ScalarAsync<string>("SELECT comment FROM wp_config.versions WHERE version = 1", Ct)).ShouldBe("please check");
    }

    private Task<ConnectionNode> StartAsync(string name, bool crashAfterCommit = false, Dictionary<string, string?>? settings = null) =>
        ConnectionNode.StartAsync(postgres, "cfg-" + name, new NodeOptions(
            [new ConfigurationModule()],
            settings ?? [],
            services =>
            {
                services.TryAddSingleton<IConfigurationAuthorizer, AllowAll>();
                services.AddScheduling();
            },
            crashAfterCommit ? services =>
            {
                // The registry's applier "crashes" between the commit and its in-memory swap.
                services.RemoveAll<IConfigApplier>();
                services.AddSingleton<IConfigApplier>(sp => new CrashBeforeComplete(ActivatorUtilities.CreateInstance<RegistryConfigApplier>(sp)));
            }
    : null), Ct);

    private static async Task<(ServerId Server, DeviceId Door)> SeedAsync(ConnectionNode node)
    {
        var server = ServerId.New();
        var door = DeviceId.New();
        var result = await node.Services.GetRequiredService<IRegistryWriter>().ApplyAsync(new RegistryChangeBatch(
        [
            new RegistryChange.UpsertServer(new Server(server, new TypeRef(TypeId.FromTrusted("generic.server"), SemVersion.Parse("1.0.0").Value), ConnectionId.New(),
                null, "srv-1", null, "Server", node.Root, [], new Dictionary<string, WpValue>(), true, false, 0)),
            new RegistryChange.UpsertDevice(new Device(door, new TypeRef(TypeId.FromTrusted("ac.door"), SemVersion.Parse("1.0.0").Value), server, null, "door-1",
                LocalizedName.Of("Door"), "Door 1", node.Root, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(),
                null, true, false, false, default, 0)),
        ]), new ChangeContext(null, "test"), Ct);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Code.Value);
        return (server, door);
    }

    private static IDraftService Drafts(ConnectionNode node) => node.Services.GetRequiredService<IDraftService>();

    private static IVersionService Versions(ConnectionNode node) => node.Services.GetRequiredService<IVersionService>();

    private static async Task<Draft> NewDraftAsync(ConnectionNode node, UserId? owner = null) =>
        (await Drafts(node).CreateAsync(LocalizedName.Of("Test"), owner ?? _alice, Ct)).Value;

    private static async Task<Draft> SetAsync(ConnectionNode node, Draft draft, string type, Guid id, EntryOperation op, string json, long? baseVersion = null,
        UserId? actor = null)
    {
        var result = await Drafts(node).SetEntryAsync(draft.Id, actor ?? draft.Owner, draft.Version,
            new EntryRequest(new ConfigObjectRef(type, id), op, baseVersion, JsonDocument.Parse(json).RootElement.Clone()), Ct);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Code.Value);
        return result.Value.Draft;
    }

    private static async Task<PublishOutcome> PublishAsync(ConnectionNode node, Draft draft, UserId? actor = null)
    {
        var current = (await Drafts(node).GetAsync(draft.Id, draft.Owner, Ct)).Value;
        var result = await node.Services.GetRequiredService<IDraftPublisher>().PublishDraftAsync(current.Id, actor ?? current.Owner, current.Version, null, null, Ct);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Code.Value + " " + string.Join(",", result.Error.Args.Select(a => a.Key + "=" + a.Value))
            + Environment.NewLine + node.Problems());
        return result.Value;
    }

    private static async Task<List<LogRecord>> ReadCfgAsync(ConnectionNode node)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var records = new List<LogRecord>();
        await foreach (var record in node.Services.GetRequiredService<IEventLogReader>().ReadGlobalAsync(LogStream.Cfg, 1, timeout.Token))
        {
            records.Add(record);
        }

        return records;
    }

    private sealed class AllowAll : IConfigurationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(UserId user, PermissionCode permission, ConfigObjectRef? scope, CancellationToken ct) => ValueTask.FromResult(true);
    }

    /// <summary>Prepares through the registry, then throws before its in-memory swap (and does not reload).</summary>
    private sealed class CrashBeforeComplete(RegistryConfigApplier inner) : IConfigApplier
    {
        public string Module => inner.Module;

        public Task<Result<IReadOnlyList<AppliedEntry>>> PrepareApplyAsync(IReadOnlyList<DraftEntry> entries, DbConnection connection, DbTransaction transaction,
            ConfigApplyContext context, CancellationToken ct) => inner.PrepareApplyAsync(entries, connection, transaction, context, ct);

        public ValueTask CompleteApplyAsync(IReadOnlyList<AppliedEntry> applied, ConfigApplyContext context, CancellationToken ct) =>
            throw new InvalidOperationException("crash after commit");

        public ValueTask AbortApplyAsync(ConfigApplyContext context, CancellationToken ct) => inner.AbortApplyAsync(context, ct);

        public ValueTask ReloadAsync(IReadOnlyList<ConfigObjectRef> objects, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
