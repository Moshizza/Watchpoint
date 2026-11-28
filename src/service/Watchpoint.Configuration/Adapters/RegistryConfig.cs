using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Configuration.Adapters;

/// <summary>
/// The configuration documents of the registry (spec CFG §4, types <c>registry.*</c> of <see cref="ModuleObjectTypes.Registry"/>):
/// live documents read from the registry, and the mapping of documents back to registry changes. A document holds the
/// fields an operator configures; fields owned by the integration (native ids, vendor names, attributes) stay as they are.
/// </summary>
public static class RegistryDocuments
{
    /// <summary>The registry document of an object, or <see langword="null"/> when it does not exist.</summary>
    /// <param name="registry">The registry (live, or staged).</param>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <returns>The document.</returns>
    public static ConfigDocument? Load(IRegistryReader registry, string type, Guid id)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(type);
        var reference = new ConfigObjectRef(type, id);
        return type switch
        {
            "registry.site" => registry.Site is { } site && site.Id.Value == id ? Doc(reference, site.Version, Site(site)) : null,
            "registry.location" => registry.GetLocation(LocationId.From(id)).TryGetValue(out var l, out _) ? Doc(reference, l.Version, Location(l)) : null,
            "registry.tagFamily" => registry.GetTagFamilies().FirstOrDefault(f => f.Id.Value == id) is { } f ? Doc(reference, f.Version, TagFamily(f)) : null,
            "registry.tag" => registry.GetTags().FirstOrDefault(t => t.Id.Value == id) is { } t ? Doc(reference, t.Version, Tag(t)) : null,
            "registry.server" => registry.GetServer(ServerId.From(id)).TryGetValue(out var s, out _) ? Doc(reference, s.Version, Server(s)) : null,
            "registry.device" => registry.GetDevice(DeviceId.From(id)).TryGetValue(out var d, out _) ? Doc(reference, d.Version, Device(d)) : null,
            "registry.link" => Links(registry).FirstOrDefault(k => k.Id.Value == id) is { } k ? Doc(reference, k.Version, Link(k)) : null,
            "registry.customField" => CustomFields(registry).FirstOrDefault(c => c.Id.Value == id) is { } c ? Doc(reference, c.Version, CustomField(c)) : null,
            _ => null,
        };
    }

    /// <summary>Every live document of a type.</summary>
    /// <param name="registry">The registry.</param>
    /// <param name="type">The object type.</param>
    /// <returns>The documents.</returns>
    public static IEnumerable<ConfigDocument> List(IRegistryReader registry, string type)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IEnumerable<Guid> ids = type switch
        {
            "registry.site" => [registry.Site.Id.Value],
            "registry.location" => registry.GetLocationTree().Select(l => l.Id.Value),
            "registry.tagFamily" => registry.GetTagFamilies().Select(f => f.Id.Value),
            "registry.tag" => registry.GetTags().Select(t => t.Id.Value),
            "registry.server" => Servers(registry).Select(s => s.Id.Value),
            "registry.device" => Devices(registry).Select(d => d.Id.Value),
            "registry.link" => Links(registry).Select(k => k.Id.Value),
            "registry.customField" => CustomFields(registry).Select(c => c.Id.Value),
            _ => [],
        };
        foreach (var id in ids)
        {
            if (Load(registry, type, id) is { } document)
            {
                yield return document;
            }
        }
    }

    /// <summary>
    /// The registry change of a draft entry against the live registry, or the reason it cannot be applied (devices and
    /// servers are created by their integration: only updated or deleted here).
    /// </summary>
    /// <param name="registry">The live registry.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>The change.</returns>
    public static Result<RegistryChange> ToChange(IRegistryReader registry, DraftEntry entry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(entry);
        var id = entry.Ref.Id;
        var c = entry.Content is { } content ? JsonNode.Parse(content.GetRawText())!.AsObject() : null;
        var expected = entry.BaseVersion;
        try
        {
            return (entry.Ref.Type, entry.Operation) switch
            {
                ("registry.site", EntryOperation.Update) => new RegistryChange.UpdateSite(registry.Site with
                {
                    Name = Name(c!) ?? registry.Site.Name,
                    DefaultTimezone = Text(c!, "timeZone") ?? registry.Site.DefaultTimezone,
                }, expected),
                ("registry.location", EntryOperation.Create or EntryOperation.Update) => new RegistryChange.UpsertLocation(
                    LocationOf(registry, id, c!), entry.Operation == EntryOperation.Create ? null : expected),
                ("registry.location", EntryOperation.Delete) => new RegistryChange.DeleteLocation(LocationId.From(id), null, expected),
                ("registry.tagFamily", EntryOperation.Create or EntryOperation.Update) => new RegistryChange.UpsertTagFamily(
                    new TagFamily(TagFamilyId.From(id), Text(c!, "key")!, Name(c!)!, Text(c!, "color"),
                        c!["exclusive"]?.GetValue<bool>() == true ? TagCardinality.ZeroOrOne : TagCardinality.ZeroOrMore, 0),
                    entry.Operation == EntryOperation.Create ? null : expected),
                ("registry.tagFamily", EntryOperation.Delete) => new RegistryChange.DeleteTagFamily(TagFamilyId.From(id), expected),
                ("registry.tag", EntryOperation.Create or EntryOperation.Update) => new RegistryChange.UpsertTag(
                    new Tag(TagId.From(id), TagFamilyId.From(Guid.Parse(Text(c!, "familyId")!)), Text(c!, "key")!, Name(c!)!, Text(c!, "color"), 0),
                    entry.Operation == EntryOperation.Create ? null : expected),
                ("registry.tag", EntryOperation.Delete) => new RegistryChange.DeleteTag(TagId.From(id)),
                ("registry.server", EntryOperation.Update) => new RegistryChange.UpsertServer(ServerOf(registry, id, c!), expected),
                ("registry.server", EntryOperation.Delete) => new RegistryChange.DeleteServer(ServerId.From(id), DeleteDevices: false),
                ("registry.device", EntryOperation.Update) => DeviceChange(registry, id, c!, expected),
                ("registry.device", EntryOperation.Delete) => new RegistryChange.DeleteDevice(DeviceId.From(id), DeleteChildren: false),
                ("registry.link", EntryOperation.Create or EntryOperation.Update) => new RegistryChange.UpsertLink(new DeviceLink(LinkId.From(id),
                    Enum.Parse<LinkKind>(Text(c!, "kind")!, ignoreCase: true), DeviceId.From(Guid.Parse(Text(c!, "from")!)), DeviceId.From(Guid.Parse(Text(c!, "to")!)), 0)),
                ("registry.link", EntryOperation.Delete) => new RegistryChange.DeleteLink(LinkId.From(id)),
                ("registry.customField", EntryOperation.Create or EntryOperation.Update) => new RegistryChange.UpsertCustomField(
                    new CustomFieldDefinition(CustomFieldId.From(id), Enum.Parse<CustomFieldTarget>(Text(c!, "target")!, ignoreCase: true), Text(c!, "key")!, Name(c!)!,
                        ValueKind(Text(c!, "valueType")!), Required: false, DefaultValue: null, 0),
                    entry.Operation == EntryOperation.Create ? null : expected),
                ("registry.customField", EntryOperation.Delete) => new RegistryChange.DeleteCustomField(CustomFieldId.From(id), expected),
                _ => NotSupported(entry),
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            return Error.Create(ErrorKind.Validation, ConfigurationErrors.EntryInvalid, ("reason", "content_invalid"), ("type", entry.Ref.Type), ("id", id));
        }
    }

    private static Error NotSupported(DraftEntry entry) =>
        Error.Create(ErrorKind.Unprocessable, ConfigurationErrors.PublishNotSupported, ("type", entry.Ref.Type), ("module", "Registry"),
            ("operation", entry.Operation.ToString().ToLowerInvariant()));

    private static Location LocationOf(IRegistryReader registry, Guid id, JsonObject c)
    {
        var existing = registry.GetLocation(LocationId.From(id)).TryGetValue(out var l, out _) ? l : null;
        var parent = Text(c, "parentId") is { } p ? LocationId.From(Guid.Parse(p)) : (LocationId?)null;
        var kind = Text(c, "kind") is { } k ? Enum.Parse<LocationKind>(k, ignoreCase: true) : existing?.Kind ?? LocationKind.Zone;
        return new Location(LocationId.From(id), parent, Name(c)!, kind, existing?.Path ?? string.Empty, existing?.Depth ?? 0, Text(c, "timeZone"),
            existing?.Address, existing?.Notes, existing?.SortOrder ?? 0, existing?.Version ?? 0);
    }

    private static Server ServerOf(IRegistryReader registry, Guid id, JsonObject c)
    {
        var server = registry.GetServer(ServerId.From(id)).Value;
        Same(server.Type.ToString(), Text(c, "type"), "type");
        Same(server.ConnectionId.Value.ToString("D"), Text(c, "connectionId"), "connectionId");
        return server with
        {
            DisplayName = Name(c),
            LocationId = LocationId.From(Guid.Parse(Text(c, "locationId")!)),
            Tags = Ids(c, "tags", TagId.From),
            Enabled = c["enabled"]?.GetValue<bool>() ?? true,
        };
    }

    private static RegistryChange.PatchDevice DeviceChange(IRegistryReader registry, Guid id, JsonObject c, long? expected)
    {
        var device = registry.GetDevice(DeviceId.From(id)).Value;
        Same(device.Type.ToString(), Text(c, "type"), "type");
        Same(device.ServerId.Value.ToString("D"), Text(c, "serverId"), "serverId");
        Same(device.ParentId?.Value.ToString("D"), Text(c, "parentId"), "parentId");
        var patch = new DevicePatch(
            Name: new Optional<LocalizedName?>(Name(c)),
            Location: new Optional<LocationId>(LocationId.From(Guid.Parse(Text(c, "locationId")!))),
            TagsSet: new Optional<IReadOnlyList<TagId>>(Ids(c, "tags", TagId.From)),
            Enabled: new Optional<bool>(c["enabled"]?.GetValue<bool>() ?? true),
            Maintenance: new Optional<bool>(c["maintenance"]?.GetValue<bool>() ?? false));
        return new RegistryChange.PatchDevice(device.Id, patch, expected);
    }

    /// <summary>Fields owned by the integration cannot change through configuration.</summary>
    private static void Same(string? live, string? document, string field)
    {
        if (!string.Equals(live, document, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{field}' is owned by the integration.");
        }
    }

    private static ConfigDocument Doc(ConfigObjectRef reference, long version, JsonObject content) =>
        new(reference, version, Internal.NodeJson.ToElement(content));

    private static JsonObject Site(Site s) => new() { ["name"] = NameJson(s.Name), ["timeZone"] = s.DefaultTimezone };

    private static JsonObject Location(Location l)
    {
        var o = new JsonObject { ["name"] = NameJson(l.Name), ["kind"] = Camel(l.Kind.ToString()) };
        if (l.ParentId is { } parent)
        {
            o["parentId"] = parent.Value.ToString("D");
        }

        if (l.Tz is { } tz)
        {
            o["timeZone"] = tz;
        }

        return o;
    }

    private static JsonObject TagFamily(TagFamily f) =>
        new() { ["key"] = f.Key, ["name"] = NameJson(f.Name), ["exclusive"] = f.Cardinality == TagCardinality.ZeroOrOne };

    private static JsonObject Tag(Tag t)
    {
        var o = new JsonObject { ["key"] = t.Key, ["name"] = NameJson(t.Name), ["familyId"] = t.FamilyId.Value.ToString("D") };
        if (t.Color is { } color)
        {
            o["color"] = color;
        }

        return o;
    }

    private static JsonObject Server(Server s)
    {
        var o = new JsonObject
        {
            ["type"] = s.Type.ToString(),
            ["connectionId"] = s.ConnectionId.Value.ToString("D"),
            ["locationId"] = s.LocationId.Value.ToString("D"),
            ["tags"] = new JsonArray([.. s.Tags.Select(t => (JsonNode)t.Value.ToString("D"))]),
            ["enabled"] = s.Enabled,
        };
        if (s.DisplayName is { } name)
        {
            o["name"] = NameJson(name);
        }

        return o;
    }

    private static JsonObject Device(Device d)
    {
        var o = new JsonObject
        {
            ["type"] = d.Type.ToString(),
            ["serverId"] = d.ServerId.Value.ToString("D"),
            ["locationId"] = d.LocationId.Value.ToString("D"),
            ["tags"] = new JsonArray([.. d.Tags.Select(t => (JsonNode)t.Value.ToString("D"))]),
            ["enabled"] = d.Enabled,
            ["maintenance"] = d.Maintenance,
        };
        if (d.ParentId is { } parent)
        {
            o["parentId"] = parent.Value.ToString("D");
        }

        if (d.DisplayName is { } name)
        {
            o["name"] = NameJson(name);
        }

        return o;
    }

    private static JsonObject Link(DeviceLink k) =>
        new() { ["from"] = k.From.Value.ToString("D"), ["to"] = k.To.Value.ToString("D"), ["kind"] = Camel(k.Kind.ToString()) };

    private static JsonObject CustomField(CustomFieldDefinition c) =>
        new() { ["key"] = c.Key, ["name"] = NameJson(c.Label), ["target"] = Camel(c.Target.ToString()), ["valueType"] = ValueType(c.Kind) };

    private static JsonObject NameJson(LocalizedName name)
    {
        var o = new JsonObject { ["default"] = name.Default };
        if (name.Translations.Count > 0)
        {
            o["translations"] = new JsonObject([.. name.Translations.OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => KeyValuePair.Create(t.Key, (JsonNode?)t.Value))]);
        }

        return o;
    }

    private static LocalizedName? Name(JsonObject c)
    {
        if (c["name"] is not JsonObject name || name["default"]?.GetValue<string>() is not { } text)
        {
            return null;
        }

        var translations = name["translations"] is JsonObject t
            ? t.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new LocalizedName(text, translations);
    }

    private static string? Text(JsonObject c, string field) => c[field]?.GetValue<string>();

    private static List<T> Ids<T>(JsonObject c, string field, Func<Guid, T> make) =>
        c[field] is JsonArray items ? [.. items.Select(i => make(Guid.Parse(i!.GetValue<string>())))] : [];

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string ValueType(WpValueKind kind) => kind switch
    {
        WpValueKind.Int => "integer",
        WpValueKind.Decimal => "decimal",
        WpValueKind.Bool => "boolean",
        WpValueKind.Enum => "enum",
        _ => "string",
    };

    private static WpValueKind ValueKind(string type) => type switch
    {
        "integer" => WpValueKind.Int,
        "decimal" => WpValueKind.Decimal,
        "boolean" => WpValueKind.Bool,
        "enum" => WpValueKind.Enum,
        _ => WpValueKind.String,
    };

    private static IEnumerable<Server> Servers(IRegistryReader registry) =>
        registry.QueryServers(new ServerQuery(), new Kernel.Paging.PageRequest(Kernel.Paging.PageRequest.MaxLimit)).Items;

    private static IEnumerable<Device> Devices(IRegistryReader registry)
    {
        string? cursor = null;
        do
        {
            var page = registry.QueryDevices(new DeviceQuery(), new Kernel.Paging.PageRequest(Kernel.Paging.PageRequest.MaxLimit, cursor));
            foreach (var device in page.Items)
            {
                yield return device;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);
    }

    private static IEnumerable<DeviceLink> Links(IRegistryReader registry) =>
        Devices(registry).SelectMany(d => registry.GetLinks(d.Id, null, LinkDirection.Outgoing)).DistinctBy(k => k.Id);

    private static IEnumerable<CustomFieldDefinition> CustomFields(IRegistryReader registry) =>
        Enum.GetValues<CustomFieldTarget>().SelectMany(registry.GetCustomFields);
}

/// <summary>The live documents of the registry for <see cref="DocumentObjectType"/> (spec CFG §4).</summary>
/// <param name="registry">The registry.</param>
public sealed class RegistryConfigSource(IRegistryReader registry) : IConfigDocumentSource
{
    /// <inheritdoc />
    public Task<ConfigDocument?> LoadAsync(string type, Guid id, CancellationToken ct) => Task.FromResult(RegistryDocuments.Load(registry, type, id));

    /// <inheritdoc />
    public async IAsyncEnumerable<ConfigDocument> ListAsync(string type, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var document in RegistryDocuments.List(registry, type))
        {
            ct.ThrowIfCancellationRequested();
            yield return document;
            await Task.Yield();
        }
    }
}

/// <summary>
/// Applies the published <c>registry.*</c> entries (spec CFG §7.1) as one registry batch written in the publish
/// transaction (revision and <c>registry.changed</c> outbox record included); the registry shows it after the commit.
/// </summary>
/// <param name="registry">The live registry.</param>
/// <param name="writer">The registry's transactional writer.</param>
public sealed class RegistryConfigApplier(IRegistryReader registry, IRegistryTransactionalWriter writer) : IConfigApplier
{
    private readonly Dictionary<ChangeSetId, IPreparedRegistryBatch> _prepared = [];

    /// <inheritdoc />
    public string Module => "Registry";

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<AppliedEntry>>> PrepareApplyAsync(IReadOnlyList<DraftEntry> entries, DbConnection connection, DbTransaction transaction,
        ConfigApplyContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);
        var changes = new List<RegistryChange>(entries.Count);
        foreach (var entry in entries)
        {
            if (!RegistryDocuments.ToChange(registry, entry).TryGetValue(out var change, out var error))
            {
                return error;
            }

            changes.Add(change);
        }

        var before = entries.ToDictionary(e => e.Ref, e => RegistryDocuments.Load(registry, e.Ref.Type, e.Ref.Id));
        var batchContext = new ChangeContext(context.Actor, context.Origin, context.ChangeSet, context.Comment);
        if (!(await writer.PrepareAsync(new RegistryChangeBatch(changes), batchContext, connection, transaction, ct).ConfigureAwait(false))
            .TryGetValue(out var prepared, out var refused))
        {
            return refused;
        }

        lock (_prepared)
        {
            _prepared[context.ChangeSet] = prepared;
        }

        return entries.Select(e =>
        {
            var after = e.Operation == EntryOperation.Delete ? null : RegistryDocuments.Load(prepared.Staged, e.Ref.Type, e.Ref.Id);
            return new AppliedEntry(e.Ref, e.Operation, after?.Version ?? 0, before[e.Ref]?.Content, after?.Content);
        }).ToList();
    }

    /// <inheritdoc />
    public ValueTask CompleteApplyAsync(IReadOnlyList<AppliedEntry> applied, ConfigApplyContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        Take(context.ChangeSet)?.Complete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AbortApplyAsync(ConfigApplyContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        Take(context.ChangeSet)?.Abort();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReloadAsync(IReadOnlyList<ConfigObjectRef> objects, CancellationToken ct) => ValueTask.CompletedTask; // the registry reloads from its own records (M9)

    private IPreparedRegistryBatch? Take(ChangeSetId changeSet)
    {
        lock (_prepared)
        {
            return _prepared.Remove(changeSet, out var prepared) ? prepared : null;
        }
    }
}
