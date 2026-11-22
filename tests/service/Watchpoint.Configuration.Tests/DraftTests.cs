using System.Text.Json;
using Shouldly;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Validation;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Secrets.Contracts;
using Xunit;
using static Watchpoint.Configuration.Tests.ConfigHarness;

namespace Watchpoint.Configuration.Tests;

[Trait("layer", "U")]
[Trait("spec", "CFG")]
public sealed class DraftTests
{
    private const string Location = "registry.location";
    private const string Device = "registry.device";
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static string LocationJson(string name = "Main building", long? parent = null) =>
        $$"""{"name":{"default":"{{name}}"}{{(parent is null ? string.Empty : $",\"parentId\":\"{Id(parent.Value)}\"")}}}""";

    private static string DeviceJson(long server = 20, long location = 10, long? parent = null) =>
        $$"""{"type":"ac.door@1.0.0","serverId":"{{Id(server)}}","locationId":"{{Id(location)}}"{{(parent is null ? string.Empty : $",\"parentId\":\"{Id(parent.Value)}\"")}}}""";

    [Fact]
    public void CFG_U_001_Object_types_register_and_are_found()
    {
        var h = new ConfigHarness(registerModuleTypes: false);
        var first = new Watchpoint.Configuration.Adapters.DocumentObjectType(Watchpoint.Configuration.Adapters.ModuleObjectTypes.Registry[1], h.Source);
        var second = new Watchpoint.Configuration.Adapters.DocumentObjectType(Watchpoint.Configuration.Adapters.ModuleObjectTypes.Registry[5], h.Source);

        h.Types.Register(first);
        h.Types.Register(second);

        h.Types.Find(Location).ShouldBeSameAs(first);
        h.Types.Find(Device).ShouldBeSameAs(second);
        h.Types.Find("registry.nothing").ShouldBeNull();
        h.Types.All.Select(t => t.Type).ShouldBe([Location, Device]);
        Should.Throw<InvalidOperationException>(() => h.Types.Register(first)).Message.ShouldContain(Location);
        first.Module.ShouldBe("Registry");
        first.PermissionFor(ConfigAccess.Edit).Value.ShouldBe("config.registry.edit");
        first.PermissionFor(ConfigAccess.Publish).Value.ShouldBe("config.registry.publish");
    }

    [Fact]
    public async Task CFG_U_002_Drafts_keep_the_base_version_and_two_editors_cannot_overwrite_each_other()
    {
        var h = new ConfigHarness();
        h.Source.Put(Location, Id(10), 7, LocationJson());
        var draft = await h.NewDraftAsync();
        draft.Version.ShouldBe(0);

        // An update remembers the version it started from; replacing it keeps that version and the entry id.
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Update, LocationJson("A"), baseVersion: 7);
        var entry = draft.Entries.ShouldHaveSingleItem();
        entry.BaseVersion.ShouldBe(7);
        draft.Version.ShouldBe(1);
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Update, LocationJson("B"), baseVersion: 99);
        var replaced = draft.Entries.ShouldHaveSingleItem();
        (replaced.EntryId, replaced.BaseVersion).ShouldBe((entry.EntryId, 7L));
        replaced.Content!.Value.GetProperty("name").GetProperty("default").GetString().ShouldBe("B");

        // A create that is deleted again cancels out; a delete followed by a create of the same object is an update.
        draft = await h.SetAsync(draft, Location, 11, EntryOperation.Create, LocationJson("New"));
        draft.Entries.Count.ShouldBe(2);
        draft = await h.SetAsync(draft, Location, 11, EntryOperation.Delete, null);
        draft.Entries.Select(e => e.Ref).ShouldBe([Ref(Location, 10)]);
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Delete, null);
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Create, LocationJson("Back"));
        draft.Entries.Single().Operation.ShouldBe(EntryOperation.Update);
        draft.Entries.Single().BaseVersion.ShouldBe(7);

        // The entry can be removed: the object is back to live.
        var removed = (await h.Drafts.RemoveEntryAsync(draft.Id, Alice, draft.Version, draft.Entries.Single().EntryId, _ct)).ShouldBeSuccess();
        removed.Entries.ShouldBeEmpty();

        // Two editors read the same version: the first change wins, the second is told to reload (nothing is overwritten).
        var seen = removed.Version;
        var one = new EntryRequest(Ref(Location, 12), EntryOperation.Create, null, Doc(LocationJson("One")));
        var two = new EntryRequest(Ref(Location, 13), EntryOperation.Create, null, Doc(LocationJson("Two")));
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, seen, one, _ct)).ShouldBeSuccess();
        var conflict = (await h.Drafts.SetEntryAsync(draft.Id, Alice, seen, two, _ct)).ShouldFailWith("common.version.conflict");
        conflict.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        (conflict.Args["expected"], conflict.Args["actual"]).ShouldBe((seen, seen + 1));
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Entries.Select(e => e.Ref).ShouldBe([Ref(Location, 12)]);

        // The same race, truly concurrent: exactly one of the two succeeds.
        var current = (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Version;
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => Task.Run(() => h.Drafts.SetEntryAsync(draft.Id, Alice, current, new EntryRequest(Ref(Location, 100 + i), EntryOperation.Create, null, Doc(LocationJson($"R{i}"))), _ct))));
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error.Code.Value == "common.version.conflict");
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Entries.Count.ShouldBe(2);

        // Nothing of this touched the live configuration.
        h.Source.Documents.Single().Value.Version.ShouldBe(7);
    }

    [Fact]
    public async Task CFG_U_003_A_draft_holds_at_most_the_configured_number_of_entries()
    {
        var h = new ConfigHarness(new ConfigurationSettings { MaxDraftEntries = 3 });
        var draft = await h.NewDraftAsync();
        for (var i = 0; i < 3; i++)
        {
            draft = await h.SetAsync(draft, Location, 10 + i, EntryOperation.Create, LocationJson($"L{i}"));
        }

        var fourth = new EntryRequest(Ref(Location, 50), EntryOperation.Create, null, Doc(LocationJson("Four")));
        var error = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, fourth, _ct)).ShouldFailWith("config.draft.too_large");
        error.Args["max"].ShouldBe(3);

        // Changing an entry that is already there is not adding one.
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(Location, 10), EntryOperation.Create, null, Doc(LocationJson("Changed"))), _ct)).ShouldBeSuccess();
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Entries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task CFG_U_004_Schema_violations_are_problems_with_json_pointer_paths()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        var json = """{"type":"","serverId":"not-a-uuid","tags":["x",1],"enabled":"yes","extra":1,"name":{"default":""}}""";
        var result = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(Device, 1), EntryOperation.Create, null, Doc(json)), _ct)).ShouldBeSuccess();

        result.Problems.ShouldAllBe(p => p.Severity == ProblemSeverity.Error && p.Object == Ref(Device, 1));
        var byPath = result.Problems.ToDictionary(p => p.Path!, p => p.Code);
        byPath["/locationId"].ShouldBe("common.value.required");
        byPath["/serverId"].ShouldBe("common.value.invalid_format");
        byPath["/type"].ShouldBe("common.value.too_short");
        byPath["/enabled"].ShouldBe("common.value.invalid_format");
        byPath["/extra"].ShouldBe("common.value.not_allowed");
        byPath["/tags/0"].ShouldBe("common.value.invalid_format");
        byPath["/tags/1"].ShouldBe("common.value.invalid_format");
        byPath["/name/default"].ShouldBe("common.value.too_short");
        result.Problems.Single(p => p.Path == "/serverId").Args["expected"].ShouldBe("uuid");

        // Problems are data: validating again, or in another order, gives exactly the same problems.
        var again = (await h.Drafts.ValidateAsync(draft.Id, Alice, _ct)).ShouldBeSuccess();
        again.Problems.ShouldBe(result.Draft.Problems);
        (await h.Drafts.ValidateAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Problems.ShouldBe(again.Problems);

        // Delete entries may not carry content; creates and updates must.
        var d2 = await h.NewDraftAsync();
        var noContent = (await h.Drafts.SetEntryAsync(d2.Id, Alice, d2.Version, new EntryRequest(Ref(Device, 2), EntryOperation.Create, null, null), _ct)).ShouldBeSuccess();
        noContent.Problems.Single().Code.ShouldBe("config.entry.invalid");
        noContent.Problems.Single().Args["reason"].ShouldBe("content_required");
        (await h.Drafts.SetEntryAsync(d2.Id, Alice, noContent.Draft.Version, new EntryRequest(new ConfigObjectRef("nothing.here", Id(1)), EntryOperation.Create, null, Doc("{}")), _ct)).ShouldFailWith("config.entry.invalid");
    }

    [Fact]
    public async Task CFG_U_005_Module_validators_are_aggregated_with_the_object_they_concern()
    {
        var rule = new Watchpoint.Configuration.Adapters.ObjectTypeDefinition(
            "test.widget", "Test", "config.test", """{"type":"object","properties":{"size":{"type":"integer"}}}""", [])
        {
            Rule = (entry, view, ct) => ValueTask.FromResult<IReadOnlyList<Problem>>(
            [
                new Problem(ProblemSeverity.Error, "test.widget.too_big", new Dictionary<string, object?> { ["size"] = 99 }, null, "/size", []),
                new Problem(ProblemSeverity.Warning, "test.widget.odd", new Dictionary<string, object?>(), entry.Ref, null, []),
            ]),
        };
        var h = new ConfigHarness(null, true, new Watchpoint.Configuration.Adapters.DocumentObjectType(rule, new FakeSource()));
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, "test.widget", 1, EntryOperation.Create, """{"size":99}""");
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Create, "{}");

        draft.Problems.Where(p => p.Object == Ref("test.widget", 1)).Select(p => (p.Code, p.Severity, p.Path)).ShouldBe(
        [
            ("test.widget.odd", ProblemSeverity.Warning, null),
            ("test.widget.too_big", ProblemSeverity.Error, "/size"),
        ]);
        draft.Problems.Any(p => p.Object == Ref(Location, 10) && p.Code == "common.value.required").ShouldBeTrue();

        // Problems come in a fixed order whatever the order entries were added in.
        draft.Problems.ShouldBe(DraftValidator.Sort(draft.Problems.Reverse()));
    }

    [Fact]
    public async Task CFG_U_006_Permissions_are_checked_per_object_type_and_object()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        var edit = (Permission: "config.registry.edit", Allowed: true);
        h.Authorizer.Denied.Add((Alice, "config.scheduling.edit"));
        h.Authorizer.DeniedObjects.Add((edit.Permission, Ref(Location, 66)));

        // A type the user may not edit.
        var denied = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref("scheduling.holidaySet", 1), EntryOperation.Create, null, Doc("""{"name":{"default":"H"}}""")), _ct))
            .ShouldFailWith("common.forbidden");
        denied.Kind.ShouldBe(ErrorKind.Forbidden);
        denied.Args["permission"].ShouldBe("config.scheduling.edit");

        // The same type, an object outside the user's scope (RGT scope rule) and one inside.
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(Location, 66), EntryOperation.Create, null, Doc(LocationJson())), _ct)).ShouldFailWith("common.forbidden");
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(Location, 67), EntryOperation.Create, null, Doc(LocationJson())), _ct)).ShouldBeSuccess();

        // Viewing an object through the draft needs the view permission; the draft was not changed by the refusals.
        h.Authorizer.Denied.Add((Alice, "config.registry.view"));
        (await h.Drafts.GetObjectAsync(draft.Id, Alice, Ref(Location, 67), _ct)).ShouldFailWith("common.forbidden");
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CFG_U_007_A_reference_to_an_object_deleted_in_the_same_draft_is_missing()
    {
        var h = new ConfigHarness();
        h.Source.Put(Location, Id(10), 1, LocationJson());
        h.Source.Put(Location, Id(11), 1, LocationJson("Annex"));
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, Device, 1, EntryOperation.Create, DeviceJson(20, 10));
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Delete, null, baseVersion: 1);

        var missing = draft.Problems.Where(p => p.Code == "config.reference.missing").ToList();
        missing.Count.ShouldBe(2);   // the server does not exist, the location is deleted in this draft
        var deleted = missing.Single(p => (Guid)p.Args["id"]! == Id(10));
        (deleted.Object, deleted.Args["type"], deleted.Args["deletedInDraft"]).ShouldBe((Ref(Device, 1), Location, true));
        missing.Single(p => (Guid)p.Args["id"]! == Id(20)).Args["deletedInDraft"].ShouldBe(false);

        // Pointing the device at another location clears that problem.
        draft = await h.SetAsync(draft, Device, 1, EntryOperation.Create, DeviceJson(20, 11));
        draft.Problems.Count(p => p.Code == "config.reference.missing").ShouldBe(1);
    }

    [Fact]
    public async Task CFG_U_008_Deleting_a_live_object_that_others_refer_to_lists_the_referrers()
    {
        var h = new ConfigHarness();
        h.Source.Put(Location, Id(10), 3, LocationJson());
        for (var i = 0; i < 12; i++)
        {
            h.Source.Put(Device, Id(100 + i), 1, DeviceJson(20, 10));
        }

        h.Source.Put(Device, Id(200), 1, DeviceJson(20, 11));
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Delete, null, baseVersion: 3);

        var inUse = draft.Problems.Single(p => p.Code == "config.reference.in_use");
        inUse.Object.ShouldBe(Ref(Location, 10));
        inUse.Args["count"].ShouldBe(12);
        ((IReadOnlyList<string>)inUse.Args["referrers"]!).Count.ShouldBe(DraftValidator.MaxListedReferrers);
        ((IReadOnlyList<string>)inUse.Args["referrers"]!)[0].ShouldBe(Ref(Device, 100).ToString());

        // A referrer the draft deletes or changes no longer counts: the draft decides what it refers to.
        for (var i = 0; i < 12; i++)
        {
            draft = await h.SetAsync(draft, Device, 100 + i, i % 2 == 0 ? EntryOperation.Delete : EntryOperation.Update, i % 2 == 0 ? null : DeviceJson(20, 11), baseVersion: 1);
        }

        draft.Problems.Any(p => p.Code == "config.reference.in_use").ShouldBeFalse();
    }

    [Fact]
    public async Task CFG_U_009_Changing_an_entry_checks_it_and_the_entries_that_refer_to_it_again()
    {
        var calls = new List<Guid>();
        var rule = new Watchpoint.Configuration.Adapters.ObjectTypeDefinition(
            "test.node", "Test", "config.test", """{"type":"object","properties":{"next":{"type":"string","format":"uuid"}}}""", [new("/next", "test.node")])
        {
            Rule = (entry, view, ct) =>
            {
                calls.Add(entry.Ref.Id);
                return ValueTask.FromResult<IReadOnlyList<Problem>>([]);
            },
        };
        var h = new ConfigHarness(null, false, new Watchpoint.Configuration.Adapters.DocumentObjectType(rule, new FakeSource()));
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, "test.node", 3, EntryOperation.Create, "{}");
        draft = await h.SetAsync(draft, "test.node", 2, EntryOperation.Create, $$"""{"next":"{{Id(3)}}"}""");
        draft = await h.SetAsync(draft, "test.node", 1, EntryOperation.Create, $$"""{"next":"{{Id(2)}}"}""");
        draft = await h.SetAsync(draft, "test.node", 4, EntryOperation.Create, "{}");
        draft.Problems.ShouldBeEmpty();

        // Deleting node 3 breaks node 2 (which refers to it) and nobody else: nodes 1 and 4 are not checked again.
        calls.Clear();
        draft = await h.SetAsync(draft, "test.node", 3, EntryOperation.Create, "{}");
        calls.ShouldBe([Id(3), Id(2)], ignoreOrder: true);
        calls.Clear();
        var result = (await h.Drafts.RemoveEntryAsync(draft.Id, Alice, draft.Version, draft.Entries.Single(e => e.Ref.Id == Id(3)).EntryId, _ct)).ShouldBeSuccess();
        calls.ShouldBe([Id(2)]);
        result.Problems.Single().Code.ShouldBe("config.reference.missing");
        result.Problems.Single().Object.ShouldBe(Ref("test.node", 2));

        // Putting it back clears the problem without touching the unrelated entries.
        calls.Clear();
        draft = await h.SetAsync(result, "test.node", 3, EntryOperation.Create, "{}");
        draft.Problems.ShouldBeEmpty();
        calls.ShouldNotContain(Id(4));
        calls.ShouldNotContain(Id(1));
    }

    [Fact]
    public async Task CFG_U_010_A_large_draft_is_validated_in_the_background_with_progress()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        var armed = false;
        var rule = new Watchpoint.Configuration.Adapters.ObjectTypeDefinition("test.bulk", "Test", "config.test", """{"type":"object"}""", [])
        {
            Rule = async (entry, view, ct) =>
            {
                // The 130th entry waits: the job has finished the chunks before it and reports them.
                if (armed && Interlocked.Increment(ref seen) == 130)
                {
                    reached.SetResult();
                    await gate.Task;
                }

                return [new Problem(ProblemSeverity.Warning, "test.bulk.note", new Dictionary<string, object?>(), null, null, [])];
            },
        };
        var h = new ConfigHarness(new ConfigurationSettings { ValidationChunkSize = 50 }, false, new Watchpoint.Configuration.Adapters.DocumentObjectType(rule, new FakeSource()));
        var draft = await h.NewDraftAsync();
        for (var i = 0; i < 250; i++)
        {
            draft = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref("test.bulk", i + 1), EntryOperation.Create, null, Doc("{}")), _ct)).ShouldBeSuccess().Draft;
        }

        seen = 0;
        armed = true;
        var job = h.Drafts.StartValidation(draft.Id, Alice, _ct);
        await reached.Task;
        job.Total.ShouldBe(250);
        job.Done.ShouldBe(100);   // two chunks of 50 are done; the third is in progress
        job.Completion.IsCompleted.ShouldBeFalse();

        gate.SetResult();
        var done = (await job.Completion).ShouldBeSuccess();
        job.Done.ShouldBe(250);
        done.Problems.Count.ShouldBe(250);
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Problems.Count.ShouldBe(250);

        // A draft that changed while the job ran keeps its newer problems: the job's result is dropped with a conflict.
        var other = await h.NewDraftAsync();
        var second = h.Drafts.StartValidation(other.Id, Bob, _ct);
        (await second.Completion).ShouldFailWith("config.draft.not_found");
    }

    [Fact]
    public async Task CFG_U_011_Fix_suggestions_carry_patches_that_make_the_problem_go_away()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        var json = """{"type":"x","serverId":"00000000-0000-7000-8000-000000000014","locationId":"00000000-0000-7000-8000-00000000000a","bogus":true,"name":{"default":"d"}}""";
        var content = Doc(json);
        var result = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(Device, 1), EntryOperation.Create, null, content), _ct)).ShouldBeSuccess();
        var remove = result.Problems.Single(p => p.Path == "/bogus").Fixes.ShouldHaveSingleItem();
        remove.Code.ShouldBe("config.fix.remove_field");

        // Applying the patch gives a document without the problem.
        var fixedContent = JsonPatch.Apply(content, remove.Patch!.Value);
        fixedContent.TryGetProperty("bogus", out _).ShouldBeFalse();
        var after = (await h.Drafts.SetEntryAsync(draft.Id, Alice, result.Draft.Version, new EntryRequest(Ref(Device, 1), EntryOperation.Create, null, fixedContent), _ct)).ShouldBeSuccess();
        after.Problems.Any(p => p.Path == "/bogus").ShouldBeFalse();

        // A missing required field with a default is fixed by adding the default; a number out of range by the nearest allowed value.
        var schema = Doc("""{"type":"object","required":["mode","size"],"properties":{"mode":{"type":"string","default":"auto"},"size":{"type":"integer","minimum":1,"maximum":10}}}""");
        var problems = DocumentSchemaValidator.Validate(schema, Doc("""{"size":50}"""), Ref(Device, 9));
        var add = problems.Single(p => p.Path == "/mode").Fixes.Single();
        add.Code.ShouldBe("config.fix.set_default");
        JsonPatch.Apply(Doc("""{"size":5}"""), add.Patch!.Value).GetProperty("mode").GetString().ShouldBe("auto");
        var nearest = problems.Single(p => p.Path == "/size").Fixes.Single();
        nearest.Code.ShouldBe("config.fix.set_nearest");
        nearest.Args["value"].ShouldBe(10.0);
        DocumentSchemaValidator.Validate(schema, JsonPatch.Apply(JsonPatch.Apply(Doc("""{"size":50}"""), nearest.Patch!.Value), add.Patch!.Value), Ref(Device, 9)).ShouldBeEmpty();

        // Patches that do not apply are refused, not applied halfway.
        Should.Throw<InvalidOperationException>(() => JsonPatch.Apply(Doc("""{"a":1}"""), JsonPatch.One("remove", "/b")));
        Should.Throw<InvalidOperationException>(() => JsonPatch.Apply(Doc("""{"a":1}"""), JsonPatch.One("replace", "/b", System.Text.Json.Nodes.JsonValue.Create(1))));
    }

    [Fact]
    public async Task CFG_U_012_Reading_an_object_through_a_draft_shows_the_draft_over_the_live_state()
    {
        var h = new ConfigHarness();
        h.Source.Put(Location, Id(10), 4, LocationJson("Live"));
        h.Source.Put(Location, Id(11), 2, LocationJson("Other"));
        h.Source.Put(Location, Id(12), 1, LocationJson("Gone"));
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Update, LocationJson("Edited"), baseVersion: 4);
        draft = await h.SetAsync(draft, Location, 12, EntryOperation.Delete, null, baseVersion: 1);
        draft = await h.SetAsync(draft, Location, 13, EntryOperation.Create, LocationJson("Fresh"));

        async Task<DraftObjectState> Read(long n) => (await h.Drafts.GetObjectAsync(draft.Id, Alice, Ref(Location, n), _ct)).ShouldBeSuccess();
        var edited = await Read(10);
        edited.Source.ShouldBe(DraftObjectSource.Draft);
        edited.Document!.Content.GetProperty("name").GetProperty("default").GetString().ShouldBe("Edited");
        edited.LiveVersion.ShouldBe(4);
        (await Read(11)).Source.ShouldBe(DraftObjectSource.Live);
        (await Read(12)).Source.ShouldBe(DraftObjectSource.Deleted);
        (await Read(13)).Source.ShouldBe(DraftObjectSource.Draft);
        (await Read(14)).Source.ShouldBe(DraftObjectSource.Missing);

        // The live configuration is what it was; only a publish changes it.
        h.Source.Documents[Ref(Location, 10)].Content.GetProperty("name").GetProperty("default").GetString().ShouldBe("Live");
        h.Source.Documents.Count.ShouldBe(3);
        var changeSet = (await h.Drafts.BuildChangeSetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess();
        changeSet.Entries.Count.ShouldBe(3);
        h.Source.Documents[Ref(Location, 10)].Version.ShouldBe(4);
        (await h.Drafts.GetObjectAsync(draft.Id, Bob, Ref(Location, 10), _ct)).ShouldFailWith("config.draft.not_found");
    }

    [Fact]
    public async Task CFG_U_013_Secrets_in_drafts_go_to_the_vault_and_are_replaced_by_references()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        var json = """{"name":{"default":"Cameras"},"package":"onvif","settings":{"host":"10.0.0.5","password":{"$secret":"hunter2"}}}""";
        var result = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref("integrations.connection", 1), EntryOperation.Create, null, Doc(json)), _ct)).ShouldBeSuccess();

        var secret = h.Vault.Secrets.ShouldHaveSingleItem();
        secret.Value.Value.ShouldBe("hunter2");
        secret.Value.Owner.ShouldBe(new SecretOwner("Configuration", draft.Id.Value));
        var stored = result.Entry!.Content!.Value;
        stored.GetRawText().ShouldNotContain("hunter2");
        stored.GetProperty("settings").GetProperty("password").GetProperty("$secretRef").GetString().ShouldBe(secret.Key.ToString());
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess().Entries.Single().Content!.Value.GetRawText().ShouldNotContain("hunter2");

        // Sending the reference back keeps the secret; sending a new value replaces it; leaving it out deletes it.
        var keep = stored.GetRawText();
        var kept = (await h.Drafts.SetEntryAsync(draft.Id, Alice, result.Draft.Version, new EntryRequest(Ref("integrations.connection", 1), EntryOperation.Create, null, Doc(keep)), _ct)).ShouldBeSuccess();
        h.Vault.Secrets.Count.ShouldBe(1);
        var replaced = (await h.Drafts.SetEntryAsync(draft.Id, Alice, kept.Draft.Version, new EntryRequest(Ref("integrations.connection", 1), EntryOperation.Create, null, Doc(json)), _ct)).ShouldBeSuccess();
        h.Vault.Secrets.Count.ShouldBe(1);
        h.Vault.Secrets.ContainsKey(secret.Key).ShouldBeFalse();
        var cleared = (await h.Drafts.SetEntryAsync(draft.Id, Alice, replaced.Draft.Version, new EntryRequest(Ref("integrations.connection", 1), EntryOperation.Create, null, Doc("""{"name":{"default":"C"},"package":"p"}""")), _ct)).ShouldBeSuccess();
        h.Vault.Secrets.ShouldBeEmpty();

        // A secret must be text, and a change that is refused leaves nothing in the vault.
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, cleared.Draft.Version, new EntryRequest(Ref("integrations.connection", 2), EntryOperation.Create, null, Doc("""{"name":{"default":"C"},"package":"p","settings":{"a":{"$secret":"x"},"b":{"$secret":5}}}""")), _ct))
            .ShouldFailWith("config.secret.invalid");
        h.Vault.Secrets.ShouldBeEmpty();
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, 0, new EntryRequest(Ref("integrations.connection", 3), EntryOperation.Create, null, Doc("""{"name":{"default":"C"},"package":"p","settings":{"a":{"$secret":"x"}}}""")), _ct))
            .ShouldFailWith("common.version.conflict");
        h.Vault.Secrets.ShouldBeEmpty();
    }

    [Fact]
    public async Task CFG_U_014_Discarding_a_draft_deletes_its_secrets()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, "integrations.connection", 1, EntryOperation.Create, """{"name":{"default":"C"},"package":"p","settings":{"a":{"$secret":"one"},"b":{"$secret":"two"}}}""");
        h.Vault.Secrets.Count.ShouldBe(2);

        (await h.Drafts.AddCollaboratorAsync(draft.Id, Alice, draft.Version, Bob, _ct)).ShouldBeSuccess();
        (await h.Drafts.DiscardAsync(draft.Id, Bob, _ct)).ShouldFailWith("common.forbidden");
        h.Vault.Secrets.Count.ShouldBe(2);

        (await h.Drafts.DiscardAsync(draft.Id, Alice, _ct)).ShouldBeSuccess();
        h.Vault.Secrets.ShouldBeEmpty();
        (await h.Drafts.GetAsync(draft.Id, Alice, _ct)).ShouldFailWith("config.draft.not_found");
        (await h.Drafts.ListAsync(Alice, true, _ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CFG_U_015_A_quick_edit_validates_and_publishes_through_a_hidden_draft()
    {
        var h = new ConfigHarness();
        h.Source.Put(Location, Id(10), 1, LocationJson());
        var request = new EntryRequest(Ref(Device, 1), EntryOperation.Create, null, Doc(DeviceJson(20, 10)));
        h.Source.Put("registry.server", Id(20), 1, """{"type":"s","connectionId":"00000000-0000-7000-8000-000000000015","locationId":"00000000-0000-7000-8000-00000000000a"}""");
        h.Source.Put("integrations.connection", Id(21), 1, """{"name":{"default":"c"},"package":"p"}""");

        var result = (await h.Quick.ApplyAsync([request], Alice, "add a door", _ct)).ShouldBeSuccess();
        result.Status.ShouldBe(QuickEditStatus.Published);
        result.Version.ShouldBe(42);
        var (changeSet, actor, comment) = h.Publisher.Published.ShouldHaveSingleItem();
        (actor, comment).ShouldBe((Alice, "add a door"));
        changeSet.Entries.Single().Ref.ShouldBe(Ref(Device, 1));
        changeSet.Origin.ShouldBe("publish:" + changeSet.Id.Value.ToString("D"));
        changeSet.Draft.ShouldBe(result.Draft);

        // The draft is hidden, and errors stop the quick edit before anything is published.
        (await h.Drafts.ListAsync(Alice, false, _ct)).ShouldBeEmpty();
        var bad = (await h.Quick.ApplyAsync([new EntryRequest(Ref(Device, 2), EntryOperation.Create, null, Doc("{}"))], Alice, null, _ct)).ShouldBeSuccess();
        bad.Status.ShouldBe(QuickEditStatus.Invalid);
        bad.Problems.Any(p => p.Code == "common.value.required").ShouldBeTrue();
        h.Publisher.Published.Count.ShouldBe(1);

        // Rights: editing and publishing are both needed.
        h.Authorizer.Denied.Add((Alice, "config.registry.publish"));
        (await h.Quick.ApplyAsync([request], Alice, null, _ct)).ShouldFailWith("common.forbidden").Args["permission"].ShouldBe("config.registry.publish");
        (await h.Quick.ApplyAsync([new EntryRequest(new ConfigObjectRef("no.such", Id(1)), EntryOperation.Create, null, Doc("{}"))], Bob, null, _ct)).ShouldFailWith("config.entry.invalid");
    }

    [Fact]
    public async Task CFG_U_016_A_quick_edit_of_a_type_that_needs_approval_waits_for_an_approver()
    {
        var h = new ConfigHarness(new ConfigurationSettings { ApprovalRequiredFor = ["registry.location"] });
        var request = new EntryRequest(Ref(Location, 10), EntryOperation.Create, null, Doc(LocationJson()));

        var result = (await h.Quick.ApplyAsync([request], Alice, "needs a look", _ct)).ShouldBeSuccess();

        result.Status.ShouldBe(QuickEditStatus.AwaitingApproval);
        result.Version.ShouldBeNull();
        h.Publisher.Published.ShouldBeEmpty();
        var draft = await h.Repository.GetAsync(result.Draft, _ct);
        draft!.Status.ShouldBe(DraftStatus.AwaitingApproval);
        draft.Approval!.RequestedBy.ShouldBe(Alice);

        // A draft that is waiting cannot be changed any more, and a type that needs no approval is published at once.
        (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, request, _ct)).ShouldFailWith("config.draft.not_open");
        (await h.Quick.ApplyAsync([new EntryRequest(Ref("history.policyOverride", 1), EntryOperation.Create, null, Doc("""{"scope":"type","typeId":"ac.door","path":"a.b","mode":"none"}"""))], Alice, null, _ct)).ShouldBeSuccess().Status.ShouldBe(QuickEditStatus.Published);
    }

    [Fact]
    public async Task CFG_U_017_Collaborators_need_the_same_permissions_as_the_owner()
    {
        var h = new ConfigHarness();
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, Location, 10, EntryOperation.Create, LocationJson());

        // Bob may not edit locations: he cannot be added. Anyone but the owner is refused.
        h.Authorizer.Denied.Add((Bob, "config.registry.edit"));
        (await h.Drafts.AddCollaboratorAsync(draft.Id, Alice, draft.Version, Bob, _ct)).ShouldFailWith("common.forbidden").Args["permission"].ShouldBe("config.registry.edit");
        h.Authorizer.Denied.Clear();
        (await h.Drafts.AddCollaboratorAsync(draft.Id, Bob, draft.Version, Bob, _ct)).ShouldFailWith("config.draft.not_found");
        var shared = (await h.Drafts.AddCollaboratorAsync(draft.Id, Alice, draft.Version, Bob, _ct)).ShouldBeSuccess();
        shared.Collaborators.ShouldBe([Bob]);

        // Bob sees the draft, changes it with his own rights, and loses a right when the rule changes.
        (await h.Drafts.ListAsync(Bob, false, _ct)).Select(d => d.Id).ShouldBe([draft.Id]);
        (await h.Drafts.ListAsync(Bob, true, _ct)).ShouldBeEmpty();
        var changed = await h.SetAsync(shared, Location, 11, EntryOperation.Create, LocationJson("By Bob"), actor: Bob);
        changed.Entries.Count.ShouldBe(2);
        h.Authorizer.Denied.Add((Bob, "config.registry.edit"));
        (await h.Drafts.SetEntryAsync(draft.Id, Bob, changed.Version, new EntryRequest(Ref(Location, 12), EntryOperation.Create, null, Doc(LocationJson())), _ct)).ShouldFailWith("common.forbidden");
        (await h.Drafts.SetEntryAsync(draft.Id, TestIdsUser(9), changed.Version, new EntryRequest(Ref(Location, 12), EntryOperation.Create, null, Doc(LocationJson())), _ct)).ShouldFailWith("config.draft.not_found");
    }

    private static UserId TestIdsUser(long n) => Watchpoint.Testing.Ids.TestIds.User(n);

    [Fact]
    public void CFG_U_018_Every_problem_code_has_a_plain_language_text_in_en_fr_and_ar()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CLAUDE.md")))
        {
            root = Path.GetDirectoryName(root);
        }

        root.ShouldNotBeNull();
        var codes = ConfigurationErrors.All
            .Concat(["common.forbidden", "common.version.conflict", "common.value.required", "common.value.invalid_format", "common.value.out_of_range", "common.value.too_long",
                "common.value.too_short", "common.value.not_allowed", "common.value.duplicate"])
            .Distinct()
            .ToList();

        foreach (var language in new[] { "en", "fr", "ar" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "src", "ui", "packages", "i18n", "locales", language, "errors.json")));
            foreach (var code in codes)
            {
                var node = document.RootElement;
                foreach (var part in code.Split('.'))
                {
                    node.TryGetProperty(part, out node).ShouldBeTrue($"{language}: errors.{code} is missing");
                }

                node.ValueKind.ShouldBe(JsonValueKind.String, $"{language}: errors.{code} must be a text");
                node.GetString().ShouldNotBeNullOrWhiteSpace();
            }
        }

        // Every code a module's schema can produce is among them: the validator only emits common.value.* codes and config.* codes.
        ConfigurationErrors.All.ShouldAllBe(c => c.StartsWith("config.", StringComparison.Ordinal) || c.StartsWith("api.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CFG_U_019_The_licence_hook_reports_a_projected_quota_error()
    {
        var h = new ConfigHarness();
        h.Licence.Allowed = 1;
        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, Device, 1, EntryOperation.Create, DeviceJson());
        draft.Problems.Any(p => p.Code == "config.licence.quota_exceeded").ShouldBeFalse();
        draft = await h.SetAsync(draft, Device, 2, EntryOperation.Create, DeviceJson());

        var quota = draft.Problems.Single(p => p.Code == "config.licence.quota_exceeded");
        (quota.Args["allowed"], quota.Args["projected"], quota.Args["unit"], quota.Object).ShouldBe((1, 2, "devices", null));

        // It follows the draft: dropping an entry clears it, and it blocks the change set while it stands.
        (await h.Drafts.BuildChangeSetAsync(draft.Id, Alice, _ct)).ShouldFailWith("config.publish.invalid");
        var removed = (await h.Drafts.RemoveEntryAsync(draft.Id, Alice, draft.Version, draft.Entries[0].EntryId, _ct)).ShouldBeSuccess();
        removed.Problems.Any(p => p.Code == "config.licence.quota_exceeded").ShouldBeFalse();
    }

    [Fact]
    public void CFG_U_020_Configuration_is_refused_to_clients_other_than_the_desktop()
    {
        ConfigurationClientRule.Check(Watchpoint.Identity.Contracts.ClientKind.Desktop).ShouldBeSuccess();
        foreach (var kind in new[] { Watchpoint.Identity.Contracts.ClientKind.Web, Watchpoint.Identity.Contracts.ClientKind.Mobile })
        {
            var error = ConfigurationClientRule.Check(kind).ShouldFailWith("api.client_kind.not_allowed");
            error.Kind.ShouldBe(ErrorKind.Forbidden);
            error.Args["required"].ShouldBe("desktop");
        }
    }
}
