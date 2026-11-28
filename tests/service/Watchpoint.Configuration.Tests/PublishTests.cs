using Shouldly;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Publish;
using Watchpoint.Configuration.Internal.Versions;
using Watchpoint.Kernel.Testing;
using Xunit;
using static Watchpoint.Configuration.Tests.ConfigHarness;
using static Watchpoint.Configuration.Tests.PublishHarness;

namespace Watchpoint.Configuration.Tests;

/// <summary>Publish, versions, rollback, merge and propagation (spec CFG §7–§8; T3.02).</summary>
[Trait("layer", "U")]
[Trait("spec", "CFG")]
public sealed class PublishTests : IDisposable
{
    private const string LocationType = "registry.location";
    private const string Calendar = "scheduling.calendar";
    private const string User = "identity.user";
    private readonly PublishHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task CFG_U_031_Publish_runs_the_modules_in_catalogue_order_inside_one_transaction()
    {
        var draft = await _h.DraftAsync(
            (User, 3, EntryOperation.Create, """{"username":"jdoe","displayName":"J. Doe"}""", null),
            (Calendar, 2, EntryOperation.Create, """{"key":"office-hours","name":{"default":"Office hours"}}""", null),
            (LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null));

        var version = (await _h.PublishAsync(draft)).ShouldBeSuccess();

        version.ShouldBe(1);
        _h.Calls.Where(c => c.Call == "prepare").Select(c => c.Module).ShouldBe(["Registry", "Scheduling", "Identity"]);
        var transactions = _h.Appliers.Values.SelectMany(a => a.Transactions).Distinct().ToList();
        transactions.Count.ShouldBe(1, "every module writes in the same transaction");
        _h.Store.Commits.ShouldBe(1);
        (await _h.ReloadAsync(draft)).Status.ShouldBe(DraftStatus.Published);
        _h.Source.Documents.Keys.ShouldContain(Location(1));
        _h.Logs.WithEventId(1301).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CFG_U_032_Revalidation_at_publish_catches_live_changes_since_the_last_validation()
    {
        _h.LiveLocation(1, "Building");
        var draft = await _h.DraftAsync((LocationType, 2, EntryOperation.Create, LocationJson("Floor", parent: 1), null));
        var changeSet = (await _h.Config.Drafts.BuildChangeSetAsync(draft.Id, Alice, Ct)).ShouldBeSuccess();

        _h.Source.Documents.Remove(Location(1)); // deleted meanwhile by another publish

        var result = await _h.Orchestrator.PublishAsync(changeSet, Alice, null, Ct);

        result.ShouldFailWith(ConfigurationErrors.PublishInvalid);
        var reopened = await _h.ReloadAsync(draft);
        reopened.Status.ShouldBe(DraftStatus.Open);
        reopened.Problems.ShouldContain(p => p.Code == ConfigurationErrors.ReferenceMissing);
        _h.Calls.ShouldBeEmpty();
        _h.Store.Commits.ShouldBe(0);
    }

    [Fact]
    public async Task CFG_U_033_Base_version_mismatch_is_a_conflict_listing_the_objects()
    {
        _h.LiveLocation(1, "Lobby");
        _h.LiveLocation(2, "Hall");
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Update, LocationJson("Lobby A"), 1),
            (LocationType, 2, EntryOperation.Update, LocationJson("Hall B"), 1));
        var changeSet = (await _h.Config.Drafts.BuildChangeSetAsync(draft.Id, Alice, Ct)).ShouldBeSuccess();
        _h.LiveLocation(2, "Hall (renamed)", version: 2);

        var error = (await _h.Orchestrator.PublishAsync(changeSet, Alice, null, Ct)).ShouldFailWith(ConfigurationErrors.PublishConflict);

        error.Args["count"].ShouldBe(1);
        ((IEnumerable<string>)error.Args["objects"]!).ShouldBe([Location(2).ToString()]);
        (await _h.ReloadAsync(draft)).Status.ShouldBe(DraftStatus.Open);
        _h.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CFG_U_034_A_failure_in_PrepareApply_rolls_back_every_module()
    {
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null),
            (Calendar, 2, EntryOperation.Create, """{"key":"office-hours","name":{"default":"Office hours"}}""", null));
        _h.Appliers["Scheduling"].RefuseWith = "scheduling.calendar.invalid";

        var error = (await _h.PublishAsync(draft)).ShouldFailWith(ConfigurationErrors.PublishFailed);

        error.Args["reason"].ShouldBe("scheduling.calendar.invalid");
        _h.Store.Commits.ShouldBe(0);
        _h.Store.Rollbacks.ShouldBe(1);
        _h.Store.Versions.ShouldBeEmpty();
        _h.Appliers["Registry"].Aborted.ShouldBe(1, "the module that prepared forgets its entries");
        _h.Appliers.Values.ShouldAllBe(a => a.Completed == 0);
        _h.Source.Documents.ShouldBeEmpty("nothing applied");
        var failed = await _h.ReloadAsync(draft);
        failed.Status.ShouldBe(DraftStatus.Failed);
        failed.Problems.ShouldHaveSingleItem().Code.ShouldBe(ConfigurationErrors.PublishFailed);
        _h.Logs.WithEventId(1303).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CFG_U_035_Version_row_with_inverse_and_diff()
    {
        _h.LiveLocation(1, "Lobby");
        _h.LiveLocation(3, "Old wing");
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Update, LocationJson("Lobby A"), 1),
            (LocationType, 2, EntryOperation.Create, LocationJson("New wing"), null),
            (LocationType, 3, EntryOperation.Delete, null, 1));

        (await _h.PublishAsync(draft, comment: "reorganise")).ShouldBeSuccess();

        var record = _h.Store.Versions.ShouldHaveSingleItem();
        record.Version.Version.ShouldBe(1);
        record.Version.Comment.ShouldBe("reorganise");
        record.Version.Author.ShouldBe(Alice);
        record.Version.Draft.ShouldBe(draft.Id);
        record.Version.Summary.ShouldBe(new Dictionary<string, int>
        {
            ["registry.location:create"] = 1,
            ["registry.location:delete"] = 1,
            ["registry.location:update"] = 1,
        });
        var inverse = record.Inverse.ToDictionary(e => e.Ref);
        inverse[Location(1)].Operation.ShouldBe(EntryOperation.Update);
        inverse[Location(1)].BaseVersion.ShouldBe(2);
        Json.Text(inverse[Location(1)].Content!.Value).ShouldContain("\"Lobby\"");
        inverse[Location(2)].Operation.ShouldBe(EntryOperation.Delete);
        inverse[Location(2)].BaseVersion.ShouldBe(1);
        inverse[Location(3)].Operation.ShouldBe(EntryOperation.Create);
        Json.Text(inverse[Location(3)].Content!.Value).ShouldContain("Old wing");
        var diff = record.Diff.Single(d => d.Ref == Location(1));
        diff.Changes.ShouldHaveSingleItem().Path.ShouldBe("/name/default");
        diff.Changes[0].Before!.Value.GetString().ShouldBe("Lobby");
        diff.Changes[0].After!.Value.GetString().ShouldBe("Lobby A");
        record.Diff.Single(d => d.Ref == Location(2)).Changes.ShouldHaveSingleItem().Path.ShouldBe("/");
    }

    [Fact]
    public async Task CFG_U_036_CompleteApply_runs_after_the_commit_and_a_failure_reloads()
    {
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null),
            (Calendar, 2, EntryOperation.Create, """{"key":"office-hours","name":{"default":"Office hours"}}""", null));
        _h.Appliers["Registry"].FailComplete = true;

        (await _h.PublishAsync(draft)).ShouldBeSuccess();

        _h.Calls.ShouldBe([("Registry", "prepare"), ("Scheduling", "prepare"), ("Registry", "complete"), ("Scheduling", "complete")]);
        _h.Store.Commits.ShouldBe(1);
        _h.Appliers["Registry"].Reloaded.ShouldHaveSingleItem().ShouldBe([Location(1)]);
        _h.Source.Documents.Keys.ShouldContain(Location(1), "reloaded from the committed state");
        _h.Logs.WithEventId(1304).ShouldHaveSingleItem();
        (await _h.ReloadAsync(draft)).Status.ShouldBe(DraftStatus.Published, "the version stands");
    }

    [Fact]
    public async Task CFG_U_037_Config_published_record_contents()
    {
        _h.LiveLocation(1, "Lobby");
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Update, LocationJson("Lobby A"), 1),
            (LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));

        (await _h.PublishAsync(draft)).ShouldBeSuccess();

        var (body, actor) = _h.Sink.Records.ShouldHaveSingleItem();
        actor.ShouldBe(Alice);
        body.Version.ShouldBe(1);
        body.DraftId.ShouldBe(draft.Id);
        body.NodeId.ShouldBe(Node);
        body.Changes.ShouldBe([
            new PublishedChange(LocationType, Id(1), EntryOperation.Update, 2),
            new PublishedChange(LocationType, Id(2), EntryOperation.Create, 1),
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task CFG_U_038_The_publish_lock_serialises_concurrent_publishes()
    {
        var first = await _h.DraftAsync((LocationType, 1, EntryOperation.Create, LocationJson("A"), null));
        var second = await _h.DraftAsync((LocationType, 2, EntryOperation.Create, LocationJson("B"), null));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Appliers["Registry"].DuringPrepare = () =>
        {
            entered.TrySetResult();
            _h.Appliers["Registry"].DuringPrepare = null;
            return release.Task;
        };

        var one = _h.PublishAsync(first);
        await entered.Task.WaitAsync(Ct);
        var two = _h.PublishAsync(second);
        await Task.Yield();
        _h.Appliers["Registry"].Prepared.Count.ShouldBe(1, "the second publish waits for the lock");
        release.SetResult();

        var versions = new[] { (await one).ShouldBeSuccess(), (await two).ShouldBeSuccess() };
        versions.Order().ShouldBe([1L, 2L]);
        _h.Store.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task CFG_U_039_Scheduled_publish_fires_at_its_time()
    {
        var draft = await _h.DraftAsync((LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null));
        var at = _h.Time.GetUtcNow().AddHours(2);

        var outcome = (await _h.Publisher.PublishDraftAsync(draft.Id, Alice, draft.Version, "night change", at, Ct)).ShouldBeSuccess();

        outcome.ShouldBe(new PublishOutcome(DraftStatus.Scheduled, null));
        var scheduled = await _h.ReloadAsync(draft);
        scheduled.Status.ShouldBe(DraftStatus.Scheduled);
        scheduled.PublishAt.ShouldBe(at);
        var key = "draft:" + draft.Id.Value.ToString("D");
        _h.Scheduler.Pending[key].At.ShouldBe(at);
        _h.Store.Versions.ShouldBeEmpty();

        await _h.Scheduler.FireAsync(key);

        (await _h.ReloadAsync(draft)).Status.ShouldBe(DraftStatus.Published);
        _h.Store.Versions.ShouldHaveSingleItem().Version.Comment.ShouldBe("night change");

        // After a restart the schedules are armed again.
        var later = await _h.DraftAsync((LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));
        (await _h.Publisher.PublishDraftAsync(later.Id, Alice, later.Version, null, at, Ct)).ShouldBeSuccess();
        _h.Scheduler.Pending.Clear();
        await _h.Publisher.ResumeSchedulesAsync(Ct);
        _h.Scheduler.Pending.Keys.ShouldBe(["draft:" + later.Id.Value.ToString("D")]);
    }

    [Fact]
    public async Task CFG_U_040_Approval_request_approve_by_another_user_reject_with_reason()
    {
        using var h = new PublishHarness(new ConfigurationSettings { ApprovalRequiredFor = [LocationType] });
        var draft = await h.DraftAsync((LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null));

        (await h.Publisher.PublishDraftAsync(draft.Id, Alice, draft.Version, "needs review", null, Ct)).ShouldBeSuccess()
            .ShouldBe(new PublishOutcome(DraftStatus.AwaitingApproval, null));
        var waiting = await h.ReloadAsync(draft);
        waiting.Status.ShouldBe(DraftStatus.AwaitingApproval);
        waiting.Approval!.RequestedBy.ShouldBe(Alice);
        h.Store.Versions.ShouldBeEmpty();

        (await h.Publisher.ApproveAsync(draft.Id, Alice, Ct)).ShouldFailWith(ConfigurationErrors.ApprovalSelfForbidden);
        h.Config.Authorizer.Denied.Add((Bob, "config.registry.publish"));
        (await h.Publisher.ApproveAsync(draft.Id, Bob, Ct)).ShouldFailWith("common.forbidden");
        h.Config.Authorizer.Denied.Clear();

        var approved = (await h.Publisher.ApproveAsync(draft.Id, Bob, Ct)).ShouldBeSuccess();
        approved.Status.ShouldBe(DraftStatus.Published);
        h.Store.Versions.ShouldHaveSingleItem().Version.Comment.ShouldBe("needs review");

        var other = await h.DraftAsync((LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));
        (await h.Publisher.PublishDraftAsync(other.Id, Alice, other.Version, null, null, Ct)).ShouldBeSuccess();
        (await h.Publisher.RejectAsync(other.Id, Bob, "wrong floor", Ct)).ShouldBeSuccess();
        var rejected = await h.ReloadAsync(other);
        rejected.Status.ShouldBe(DraftStatus.Open);
        var problem = rejected.Problems.Single(p => p.Code == ConfigurationErrors.ApprovalRejected);
        problem.Severity.ShouldBe(ProblemSeverity.Warning);
        problem.Args["reason"].ShouldBe("wrong floor");
        h.Store.Versions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CFG_U_041_Rollback_draft_is_the_inverse_of_a_version_validated_against_the_current_state()
    {
        _h.LiveLocation(1, "Lobby");
        var draft = await _h.DraftAsync(
            (LocationType, 1, EntryOperation.Update, LocationJson("Lobby A"), 1),
            (LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));
        (await _h.PublishAsync(draft)).ShouldBeSuccess();

        var rollbackId = (await _h.Versions.CreateRollbackDraftAsync(1, Bob, restore: false, Ct)).ShouldBeSuccess();

        var rollback = (await _h.Config.Drafts.GetAsync(rollbackId, Bob, Ct)).ShouldBeSuccess();
        rollback.Owner.ShouldBe(Bob);
        rollback.Problems.Where(p => p.Severity == ProblemSeverity.Error).ShouldBeEmpty();
        var entries = rollback.Entries.ToDictionary(e => e.Ref);
        entries[Location(1)].Operation.ShouldBe(EntryOperation.Update);
        entries[Location(1)].BaseVersion.ShouldBe(2);
        entries[Location(2)].Operation.ShouldBe(EntryOperation.Delete);
        _h.Logs.WithEventId(1310).ShouldHaveSingleItem();

        (await _h.PublishAsync(rollback)).ShouldBeSuccess().ShouldBe(2);
        Json.Text(_h.Live(Location(1))).ShouldContain("\"Lobby\"");
        _h.Source.Documents.ContainsKey(Location(2)).ShouldBeFalse();

        // Validated against the current state: undoing version 1 again is now outdated.
        var stale = (await _h.Versions.CreateRollbackDraftAsync(1, Bob, restore: false, Ct)).ShouldBeSuccess();
        (await _h.Config.Drafts.GetAsync(stale, Bob, Ct)).ShouldBeSuccess().Problems.ShouldContain(p => p.Code == ConfigurationErrors.EntryOutdated);
        (await _h.Versions.CreateRollbackDraftAsync(99, Bob, false, Ct)).ShouldFailWith(ConfigurationErrors.VersionNotFound);
    }

    [Fact]
    public async Task CFG_U_042_Restore_to_version_v_undoes_every_later_version()
    {
        _h.LiveLocation(1, "Lobby");
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("v1"), 1));                // version 1: Lobby → v1
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("v2"), 2),                 // version 2: v1 → v2, + Wing
            (LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("v3"), 3),                 // version 3: v2 → v3, Wing renamed
            (LocationType, 2, EntryOperation.Update, LocationJson("Wing 2"), 1));

        var id = (await _h.Versions.CreateRollbackDraftAsync(1, Alice, restore: true, Ct)).ShouldBeSuccess();
        var restore = (await _h.Config.Drafts.GetAsync(id, Alice, Ct)).ShouldBeSuccess();

        var entries = restore.Entries.ToDictionary(e => e.Ref);
        entries.Count.ShouldBe(2);
        entries[Location(1)].Operation.ShouldBe(EntryOperation.Update);
        entries[Location(1)].BaseVersion.ShouldBe(4, "based on the live version");
        Json.Text(entries[Location(1)].Content!.Value).ShouldContain("\"v1\"", Case.Sensitive, "the state right after version 1");
        entries[Location(2)].Operation.ShouldBe(EntryOperation.Delete, "it did not exist at version 1");
        entries[Location(2)].BaseVersion.ShouldBe(2);

        (await _h.PublishAsync(restore)).ShouldBeSuccess();
        Json.Text(_h.Live(Location(1))).ShouldContain("\"v1\"");
        _h.Source.Documents.ContainsKey(Location(2)).ShouldBeFalse();
    }

    [Fact]
    public async Task CFG_U_043_Object_at_version_v()
    {
        _h.LiveLocation(1, "Lobby");
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("v1"), 1));
        await PublishAsync((LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null));
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("v3"), 2));

        Json.Text((await _h.Versions.GetObjectAtVersionAsync(Location(1), 1, Ct)).ShouldBeSuccess()).ShouldContain("\"v1\"");
        Json.Text((await _h.Versions.GetObjectAtVersionAsync(Location(1), 2, Ct)).ShouldBeSuccess()).ShouldContain("\"v1\"", Case.Sensitive, "unchanged by version 2");
        Json.Text((await _h.Versions.GetObjectAtVersionAsync(Location(1), 3, Ct)).ShouldBeSuccess()).ShouldContain("\"v3\"");
        (await _h.Versions.GetObjectAtVersionAsync(Location(2), 1, Ct)).ShouldFailWith(ConfigurationErrors.VersionNotFound); // it did not exist yet
        (await _h.Versions.GetObjectAtVersionAsync(Location(1), 9, Ct)).ShouldFailWith(ConfigurationErrors.VersionNotFound);

        var page = await _h.Versions.ListAsync(new VersionQuery(ObjectId: Id(1)), new Kernel.Paging.PageRequest(1), Ct);
        page.Items.ShouldHaveSingleItem().Version.ShouldBe(3);
        page.NextCursor.ShouldNotBeNull();
        (await _h.Versions.ListAsync(new VersionQuery(ObjectId: Id(1)), new Kernel.Paging.PageRequest(5, page.NextCursor), Ct)).Items.Single().Version.ShouldBe(1);
        (await _h.Versions.GetAsync(2, Ct)).ShouldBeSuccess().Entries.ShouldHaveSingleItem().Ref.ShouldBe(Location(2));
    }

    [Fact]
    public void CFG_U_044_Diff_compares_arrays_by_id_when_items_have_one()
    {
        var before = Doc("""{"rules":[{"id":"a","level":1},{"id":"b","level":2}],"tags":["x","y"],"name":"n"}""");
        var after = Doc("""{"rules":[{"id":"b","level":3},{"id":"c","level":1}],"tags":["x","z"],"name":"n"}""");

        var changes = JsonDiff.Diff(before, after);

        changes.Select(c => c.Path).Order().ShouldBe(["/rules/a", "/rules/b/level", "/rules/c", "/tags/1"]);
        var removed = changes.Single(c => c.Path == "/rules/a");
        removed.After.ShouldBeNull();
        removed.Before!.Value.GetProperty("level").GetInt32().ShouldBe(1);
        changes.Single(c => c.Path == "/rules/c").Before.ShouldBeNull();
        changes.Single(c => c.Path == "/tags/1").After!.Value.GetString().ShouldBe("z");
        JsonDiff.Diff(before, before).ShouldBeEmpty();
        JsonDiff.Diff(Doc("""{"a~b/c":1}"""), Doc("""{"a~b/c":2}""")).Single().Path.ShouldBe("/a~0b~1c");
    }

    [Fact]
    public void CFG_U_045_Three_way_merge_auto_merges_non_conflicting_changes()
    {
        var @base = Doc("""{"name":"Lobby","kind":"zone","timeZone":"UTC","meta":{"a":1,"b":1}}""");
        var mine = Doc("""{"name":"Lobby A","kind":"zone","timeZone":"UTC","meta":{"a":2,"b":1}}""");
        var theirs = Doc("""{"name":"Lobby","kind":"room","meta":{"a":1,"b":3}}""");

        var merge = ThreeWayMerge.Merge(@base, mine, theirs);

        merge.Conflicts.ShouldBeEmpty();
        var merged = merge.Merged;
        merged.GetProperty("name").GetString().ShouldBe("Lobby A");
        merged.GetProperty("kind").GetString().ShouldBe("room");
        merged.TryGetProperty("timeZone", out _).ShouldBeFalse("removed on their side, untouched on mine");
        merged.GetProperty("meta").GetProperty("a").GetInt32().ShouldBe(2);
        merged.GetProperty("meta").GetProperty("b").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void CFG_U_046_Three_way_merge_reports_conflicting_paths()
    {
        var @base = Doc("""{"name":"Lobby","kind":"zone","tags":["a"]}""");
        var mine = Doc("""{"name":"Lobby A","kind":"zone","tags":["a","b"]}""");
        var theirs = Doc("""{"name":"Lobby B","kind":"zone","tags":["c"]}""");

        var merge = ThreeWayMerge.Merge(@base, mine, theirs);

        merge.Conflicts.Order().ShouldBe(["/name", "/tags"]);
        merge.Merged.GetProperty("name").GetString().ShouldBe("Lobby A", "conflicts keep mine until resolved");
        ThreeWayMerge.Merge(null, mine, theirs).Conflicts.ShouldContain("/name", "without a base every difference conflicts");
    }

    [Fact]
    public async Task CFG_U_047_Rebase_merges_with_the_live_document_and_updates_the_base_version()
    {
        _h.LiveLocation(1, "Lobby");
        await PublishAsync((LocationType, 1, EntryOperation.Update, LocationJson("Lobby"), 1)); // version 2 of the object, base document known
        var mine = await _h.DraftAsync((LocationType, 1, EntryOperation.Update, """{"name":{"default":"Lobby A"},"kind":"zone"}""", 2));
        await PublishAsync((LocationType, 1, EntryOperation.Update, """{"name":{"default":"Lobby"},"kind":"room"}""", 2)); // someone else: kind

        (await _h.PublishAsync(mine)).ShouldFailWith(ConfigurationErrors.PublishConflict);
        mine = await _h.ReloadAsync(mine);
        var entry = mine.Entries.Single();

        var rebased = (await _h.Publisher.RebaseEntryAsync(mine.Id, Alice, mine.Version, entry.EntryId, null, Ct)).ShouldBeSuccess();

        rebased.Conflicts.ShouldBeEmpty();
        rebased.LiveVersion.ShouldBe(3);
        var updated = rebased.Draft.Entries.Single();
        updated.BaseVersion.ShouldBe(3);
        updated.Content!.Value.GetProperty("name").GetProperty("default").GetString().ShouldBe("Lobby A");
        updated.Content.Value.GetProperty("kind").GetString().ShouldBe("room");
        (await _h.PublishAsync(rebased.Draft)).ShouldBeSuccess();

        // A conflict is reported without changing the entry; the resolved document is then taken.
        var second = await _h.DraftAsync((LocationType, 1, EntryOperation.Update, """{"name":{"default":"Mine"},"kind":"room"}""", 4));
        await PublishAsync((LocationType, 1, EntryOperation.Update, """{"name":{"default":"Theirs"},"kind":"room"}""", 4));
        var conflict = (await _h.Publisher.RebaseEntryAsync(second.Id, Alice, second.Version, second.Entries[0].EntryId, null, Ct)).ShouldBeSuccess();
        conflict.Conflicts.ShouldBe(["/name/default"]);
        conflict.LiveVersion.ShouldBeNull();
        conflict.Draft.Entries[0].BaseVersion.ShouldBe(4);
        var resolved = (await _h.Publisher.RebaseEntryAsync(second.Id, Alice, second.Version, second.Entries[0].EntryId,
            Doc("""{"name":{"default":"Theirs"},"kind":"room"}"""), Ct)).ShouldBeSuccess();
        resolved.Draft.Entries[0].BaseVersion.ShouldBe(5);
    }

    [Fact]
    public async Task CFG_U_048_An_entry_whose_base_is_outdated_gets_a_warning()
    {
        _h.LiveLocation(1, "Lobby", version: 3);

        var draft = await _h.DraftAsync((LocationType, 1, EntryOperation.Update, LocationJson("Lobby A"), 2));

        var warning = draft.Problems.Single(p => p.Code == ConfigurationErrors.EntryOutdated);
        warning.Severity.ShouldBe(ProblemSeverity.Warning);
        warning.Args["baseVersion"].ShouldBe(2L);
        warning.Args["liveVersion"].ShouldBe(3L);
    }

    [Fact]
    public async Task CFG_U_049_Every_publish_is_audited()
    {
        await PublishAsync((LocationType, 1, EntryOperation.Create, LocationJson("Lobby"), null));
        await PublishAsync((LocationType, 2, EntryOperation.Create, LocationJson("Wing"), null), (LocationType, 3, EntryOperation.Create, LocationJson("Hall"), null));

        _h.Audit.Records.Select(r => r.Version.Version).ShouldBe([1L, 2L]);
        _h.Audit.Records[1].Entries.Select(e => e.Ref).ShouldBe([Location(2), Location(3)], ignoreOrder: true);
        _h.Audit.Records[1].Version.Author.ShouldBe(Alice);

        // A refused publish is not audited.
        var refused = await _h.DraftAsync((LocationType, 4, EntryOperation.Create, LocationJson("X"), null));
        _h.Appliers["Registry"].RefuseWith = "registry.invariant";
        (await _h.PublishAsync(refused)).ShouldFailWith(ConfigurationErrors.PublishFailed);
        _h.Audit.Records.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CFG_U_050_Another_node_reloads_from_config_published()
    {
        var reloader = new ConfigReloader(_h.Config.Types, _h.Appliers.Values, Guid.Parse("0f000000-0000-0000-0000-000000000002"));
        var body = new ConfigPublishedBody(7, Kernel.Ids.DraftId.New(),
        [
            new PublishedChange(LocationType, Id(1), EntryOperation.Update, 2),
            new PublishedChange(Calendar, Id(2), EntryOperation.Create, 1),
            new PublishedChange(LocationType, Id(3), EntryOperation.Delete, 0),
            new PublishedChange("unknown.type", Id(4), EntryOperation.Create, 1),
        ], Node);

        await reloader.OnPublishedAsync(body, Ct);

        _h.Appliers["Registry"].Reloaded.ShouldHaveSingleItem().ShouldBe([Location(1), Location(3)]);
        _h.Appliers["Scheduling"].Reloaded.ShouldHaveSingleItem().ShouldBe([Ref(Calendar, 2)]);
        _h.Appliers["Identity"].Reloaded.ShouldBeEmpty();

        // The publishing node already applied its own changes.
        var own = new ConfigReloader(_h.Config.Types, _h.Appliers.Values, Node);
        await own.OnPublishedAsync(body, Ct);
        _h.Appliers["Registry"].Reloaded.Count.ShouldBe(1);
    }

    /// <summary>A draft with these entries, published at once.</summary>
    private async Task PublishAsync(params (string Type, long N, EntryOperation Op, string? Json, long? Base)[] entries)
    {
        var draft = await _h.DraftAsync(entries);
        (await _h.PublishAsync(draft)).ShouldBeSuccess();
    }
}
