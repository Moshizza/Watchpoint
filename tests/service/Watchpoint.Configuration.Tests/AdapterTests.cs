using Shouldly;
using Watchpoint.Configuration.Adapters;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Testing;
using Xunit;
using static Watchpoint.Configuration.Tests.ConfigHarness;

namespace Watchpoint.Configuration.Tests;

[Trait("layer", "U")]
[Trait("spec", "CFG")]
public sealed class AdapterTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static async Task<IReadOnlyList<Problem>> ValidateAsync(ConfigHarness h, string type, long n, string json, EntryOperation op = EntryOperation.Create)
    {
        var draft = await h.NewDraftAsync();
        var result = (await h.Drafts.SetEntryAsync(draft.Id, Alice, draft.Version, new EntryRequest(Ref(type, n), op, op == EntryOperation.Create ? null : 1, Doc(json)), _ct)).ShouldBeSuccess();
        return result.Problems;
    }

    private static string[] Codes(IEnumerable<Problem> problems) => [.. problems.Select(p => $"{p.Path}:{p.Code}")];

    [Fact]
    public async Task CFG_U_021_Registry_site_and_location_documents_are_validated_and_their_references_extracted()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.location", Id(1), 1, """{"name":{"default":"Root"}}""");

        (await ValidateAsync(h, "registry.site", 1, """{"name":{"default":"HQ"},"timeZone":"Europe/Paris"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "registry.site", 2, """{"timeZone":"UTC","x":1}""")).ShouldBe(["/x:common.value.not_allowed", "/name:common.value.required"], ignoreOrder: true);

        (await ValidateAsync(h, "registry.location", 2, $$"""{"name":{"default":"Floor 1"},"parentId":"{{Id(1)}}","kind":"floor"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "registry.location", 3, $$"""{"name":{"default":"Bad"},"parentId":"{{Id(1)}}","kind":"galaxy"}""")).ShouldBe(["/kind:common.value.not_allowed"]);
        var missing = await ValidateAsync(h, "registry.location", 4, $$"""{"name":{"default":"Orphan"},"parentId":"{{Id(99)}}"}""");
        missing.Single().Code.ShouldBe("config.reference.missing");
        h.Types.Find("registry.location")!.GetReferences(Doc($$"""{"name":{"default":"x"},"parentId":"{{Id(1)}}"}""")).ShouldBe([Ref("registry.location", 1)]);
        h.Types.Find("registry.site")!.PermissionFor(ConfigAccess.View).Value.ShouldBe("config.registry.view");
    }

    [Fact]
    public async Task CFG_U_022_Registry_tag_families_and_tags_refer_to_each_other()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.tagFamily", Id(1), 1, """{"key":"zone","name":{"default":"Zone"}}""");

        (await ValidateAsync(h, "registry.tag", 2, $$"""{"key":"lobby","name":{"default":"Lobby"},"familyId":"{{Id(1)}}","color":"#aabbcc"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "registry.tag", 3, $$"""{"key":"Bad Key","name":{"default":"B"},"familyId":"{{Id(1)}}","color":"red"}""")).ShouldBe(["/color:common.value.invalid_format", "/key:common.value.invalid_format"], ignoreOrder: true);
        (await ValidateAsync(h, "registry.tag", 4, $$"""{"key":"x","name":{"default":"B"},"familyId":"{{Id(77)}}"}""")).Single().Code.ShouldBe("config.reference.missing");
        Codes(await ValidateAsync(h, "registry.tagFamily", 5, """{"key":"k","name":{"default":"K"},"exclusive":"yes"}""")).ShouldBe(["/exclusive:common.value.invalid_format"]);
    }

    [Fact]
    public async Task CFG_U_023_Registry_servers_refer_to_a_connection_a_location_and_tags()
    {
        var h = new ConfigHarness();
        h.Source.Put("integrations.connection", Id(1), 1, """{"name":{"default":"C"},"package":"p"}""");
        h.Source.Put("registry.location", Id(2), 1, """{"name":{"default":"L"}}""");
        h.Source.Put("registry.tag", Id(3), 1, $$"""{"key":"t","name":{"default":"T"},"familyId":"{{Id(9)}}"}""");

        (await ValidateAsync(h, "registry.server", 4, $$"""{"type":"onvif.nvr@1.0.0","connectionId":"{{Id(1)}}","locationId":"{{Id(2)}}","tags":["{{Id(3)}}"]}""")).ShouldBeEmpty();
        var broken = await ValidateAsync(h, "registry.server", 5, $$"""{"type":"x","connectionId":"{{Id(90)}}","locationId":"{{Id(2)}}","tags":["{{Id(3)}}","{{Id(91)}}"]}""");
        broken.Count.ShouldBe(2);
        broken.Select(p => (Guid)p.Args["id"]!).ShouldBe([Id(90), Id(91)], ignoreOrder: true);
        broken.Select(p => (string)p.Args["type"]!).ShouldBe(["integrations.connection", "registry.tag"], ignoreOrder: true);
    }

    [Fact]
    public async Task CFG_U_024_Registry_devices_check_server_parent_location_and_tags_against_the_draft()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.server", Id(1), 1, "{}");
        h.Source.Put("registry.location", Id(2), 1, "{}");
        var draft = await h.NewDraftAsync();

        // A device and its new parent device in the same draft: the parent is found in the draft.
        draft = await h.SetAsync(draft, "registry.device", 10, EntryOperation.Create, $$"""{"type":"ac.reader@1.0.0","serverId":"{{Id(1)}}","locationId":"{{Id(2)}}"}""");
        draft = await h.SetAsync(draft, "registry.device", 11, EntryOperation.Create, $$"""{"type":"ac.door@1.0.0","serverId":"{{Id(1)}}","locationId":"{{Id(2)}}","parentId":"{{Id(10)}}","tags":[]}""");
        draft.Problems.ShouldBeEmpty();
        draft = await h.SetAsync(draft, "registry.device", 11, EntryOperation.Create, $$"""{"type":"ac.door@1.0.0","serverId":"{{Id(1)}}","locationId":"{{Id(2)}}","parentId":"{{Id(55)}}","enabled":"no"}""");
        Codes(draft.Problems).ShouldBe([":config.reference.missing", "/enabled:common.value.invalid_format"], ignoreOrder: true);
        h.Types.Find("registry.device")!.Tabular.ShouldBeTrue();
    }

    [Fact]
    public async Task CFG_U_025_Registry_links_and_custom_fields()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.device", Id(1), 1, "{}");
        h.Source.Put("registry.device", Id(2), 1, "{}");

        (await ValidateAsync(h, "registry.link", 3, $$"""{"from":"{{Id(1)}}","to":"{{Id(2)}}","kind":"cameraViews"}""")).ShouldBeEmpty();
        var broken = await ValidateAsync(h, "registry.link", 4, $$"""{"from":"{{Id(1)}}","to":"{{Id(9)}}","kind":"loves"}""");
        Codes(broken).ShouldBe(["/kind:common.value.not_allowed", ":config.reference.missing"], ignoreOrder: true);
        (await ValidateAsync(h, "registry.customField", 5, """{"key":"owner","name":{"default":"Owner"},"target":"device","valueType":"string"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "registry.customField", 6, """{"key":"owner","name":{"default":"Owner"},"target":"planet","valueType":"string"}""")).ShouldBe(["/target:common.value.not_allowed"]);
    }

    [Fact]
    public async Task CFG_U_026_Scheduling_calendars_refer_to_holiday_sets()
    {
        var h = new ConfigHarness();
        h.Source.Put("scheduling.holidaySet", Id(1), 1, """{"name":{"default":"FR"},"country":"FR"}""");

        (await ValidateAsync(h, "scheduling.holidaySet", 2, """{"name":{"default":"US"},"country":"US","days":[{"date":"2026-07-04","name":"Independence Day"}]}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "scheduling.holidaySet", 3, """{"name":{"default":"X"},"country":"usa","days":[{"date":"4 July"}]}""")).ShouldBe(["/country:common.value.invalid_format", "/days/0/date:common.value.invalid_format"], ignoreOrder: true);
        (await ValidateAsync(h, "scheduling.calendar", 4, $$"""{"key":"office-hours","name":{"default":"Office"},"holidaySets":["{{Id(1)}}"]}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "scheduling.calendar", 5, $$"""{"key":"Office Hours","name":{"default":"Office"},"holidaySets":["{{Id(8)}}"]}""")).ShouldBe(["/key:common.value.invalid_format", ":config.reference.missing"], ignoreOrder: true);
    }

    [Fact]
    public async Task CFG_U_027_Integration_connections_and_identity_users()
    {
        var h = new ConfigHarness();
        (await ValidateAsync(h, "integrations.connection", 1, """{"name":{"default":"Cameras"},"package":"onvif","settings":{"host":"10.0.0.2"}}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "integrations.connection", 2, """{"name":{"default":"Cameras"},"settings":"text"}""")).ShouldBe(["/package:common.value.required", "/settings:common.value.invalid_format"], ignoreOrder: true);
        (await ValidateAsync(h, "identity.user", 3, """{"username":"jane.doe","displayName":"Jane Doe","email":"jane@example.com"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "identity.user", 4, """{"username":"JD","displayName":""}""")).ShouldBe(["/username:common.value.invalid_format", "/displayName:common.value.too_short"], ignoreOrder: true);
        h.Types.Find("identity.user")!.Module.ShouldBe("Identity");
    }

    [Fact]
    public async Task CFG_U_028_History_policy_overrides()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.device", Id(1), 1, "{}");

        (await ValidateAsync(h, "history.policyOverride", 2, """{"scope":"type","typeId":"ac.door","path":"temperature.value","mode":"deadband","kind":"absolute","threshold":2}""")).ShouldBeEmpty();
        (await ValidateAsync(h, "history.policyOverride", 3, $$"""{"scope":"device","deviceId":"{{Id(1)}}","path":"temperature.value","mode":"none"}""")).ShouldBeEmpty();
        Codes(await ValidateAsync(h, "history.policyOverride", 4, """{"scope":"type","path":"temperature","mode":"sometimes","threshold":-1}""")).ShouldBe(
            ["/path:common.value.invalid_format", "/mode:common.value.not_allowed", "/threshold:common.value.out_of_range"], ignoreOrder: true);
        (await ValidateAsync(h, "history.policyOverride", 5, $$"""{"scope":"device","deviceId":"{{Id(50)}}","path":"a.b","mode":"none"}""")).Single().Code.ShouldBe("config.reference.missing");
    }

    [Fact]
    public async Task CFG_U_029_The_catalogue_of_module_types_is_complete_and_consistent()
    {
        var h = new ConfigHarness();
        var all = h.Types.All;

        all.Select(t => t.Type).Distinct().Count().ShouldBe(all.Count);
        all.Select(t => t.Type).ShouldBe(
        [
            "registry.site", "registry.location", "registry.tagFamily", "registry.tag", "registry.server", "registry.device", "registry.link", "registry.customField",
            "integrations.connection", "scheduling.holidaySet", "scheduling.calendar", "identity.user", "history.policyOverride",
        ]);
        foreach (var type in all)
        {
            type.Type.ShouldStartWith(type.Module.ToLowerInvariant() + ".");
            type.Schema.ShouldNotBeNull();
            new[] { ConfigAccess.View, ConfigAccess.Edit, ConfigAccess.Publish }.Select(a => type.PermissionFor(a).Value).Distinct().Count().ShouldBe(3);

            // Every reference of every type names a type of the catalogue.
            foreach (var definition in ModuleObjectTypes.All.Where(d => d.Type == type.Type))
            {
                definition.References.ShouldAllBe(r => all.Any(t => t.Type == r.TargetType), $"{type.Type} refers to a type that is not registered");
            }
        }

        // Live documents are read through the source, listed in id order, and Take limits the list.
        for (var i = 1; i <= 5; i++)
        {
            h.Source.Put("registry.tagFamily", Id(i), 1, """{"key":"k","name":{"default":"K"}}""");
        }

        var type1 = h.Types.Find("registry.tagFamily")!;
        (await type1.LoadAsync(Id(3), _ct))!.Version.ShouldBe(1);
        (await type1.LoadAsync(Id(30), _ct)).ShouldBeNull();
        var listed = new List<Guid>();
        await foreach (var d in type1.ListAsync(new ConfigListQuery(), _ct))
        {
            listed.Add(d.Ref.Id);
        }

        listed.ShouldBe([Id(1), Id(2), Id(3), Id(4), Id(5)]);
        var two = new List<Guid>();
        await foreach (var d in type1.ListAsync(new ConfigListQuery(2), _ct))
        {
            two.Add(d.Ref.Id);
        }

        two.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CFG_U_030_A_change_set_orders_entries_for_publishing_and_leaves_the_live_configuration_alone()
    {
        var h = new ConfigHarness();
        h.Source.Put("registry.location", Id(1), 1, """{"name":{"default":"Old"}}""");
        h.Source.Put("registry.location", Id(2), 1, """{"name":{"default":"Unused"}}""");
        h.Source.Put("registry.server", Id(5), 1, "{}");
        h.Source.Put("registry.tag", Id(6), 1, "{}");
        var before = h.Source.Documents.ToDictionary(d => d.Key, d => d.Value.Content.GetRawText());
        var loadsBefore = h.Source.Loads;

        var draft = await h.NewDraftAsync();
        draft = await h.SetAsync(draft, "registry.device", 30, EntryOperation.Create, $$"""{"type":"t@1.0.0","serverId":"{{Id(5)}}","locationId":"{{Id(10)}}"}""");
        draft = await h.SetAsync(draft, "registry.location", 10, EntryOperation.Create, """{"name":{"default":"New"}}""");
        draft = await h.SetAsync(draft, "registry.location", 2, EntryOperation.Delete, null, baseVersion: 1);
        draft = await h.SetAsync(draft, "registry.tag", 6, EntryOperation.Delete, null, baseVersion: 1);
        draft = await h.SetAsync(draft, "registry.location", 1, EntryOperation.Update, """{"name":{"default":"Renamed"}}""", baseVersion: 1);

        var changeSet = (await h.Drafts.BuildChangeSetAsync(draft.Id, Alice, _ct)).ShouldBeSuccess();

        // Creates and updates in catalogue order (locations before devices, by id within a type), deletes after them in reverse order.
        changeSet.Entries.Select(e => (e.Ref.Type, e.Operation)).ShouldBe(
        [
            ("registry.location", EntryOperation.Update),
            ("registry.location", EntryOperation.Create),
            ("registry.device", EntryOperation.Create),
            ("registry.tag", EntryOperation.Delete),
            ("registry.location", EntryOperation.Delete),
        ]);
        changeSet.Draft.ShouldBe(draft.Id);
        changeSet.DraftVersion.ShouldBe(draft.Version);
        changeSet.Origin.ShouldBe("publish:" + changeSet.Id.Value.ToString("D"));

        // Nothing live was written, and a draft that is not open gives no change set.
        h.Source.Documents.ToDictionary(d => d.Key, d => d.Value.Content.GetRawText()).ShouldBe(before);
        h.Source.Loads.ShouldBeGreaterThan(loadsBefore);
        var otherId = (await h.NewDraftAsync()).Id;
        (await h.Drafts.DiscardAsync(otherId, Alice, _ct)).ShouldBeSuccess();
        (await h.Drafts.BuildChangeSetAsync(otherId, Alice, _ct)).ShouldFailWith("config.draft.not_found");

        // A draft with errors cannot be frozen.
        var bad = await h.SetAsync(await h.NewDraftAsync(), "registry.location", 40, EntryOperation.Create, "{}");
        var invalid = (await h.Drafts.BuildChangeSetAsync(bad.Id, Alice, _ct)).ShouldFailWith("config.publish.invalid");
        invalid.Args["errors"].ShouldBe(1);
    }
}
