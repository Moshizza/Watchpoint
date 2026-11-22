using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Adapters;

/// <summary>
/// The object types of the modules that exist (spec CFG §4): schemas and references of their documents. Documents read from an
/// <see cref="IConfigDocumentSource"/>; the alarm (ALM) and rights (RGT) types are described with those modules. Registering them
/// keeps the catalogue order of the spec, which is the order publishing applies them in.
/// </summary>
public static class ModuleObjectTypes
{
    private const string Name = """{"type":"object","required":["default"],"properties":{"default":{"type":"string","minLength":1,"maxLength":200}}}""";
    private const string Uuid = """{"type":"string","format":"uuid"}""";

    private static string S(string schema) => schema.Replace("@NAME", Name, StringComparison.Ordinal).Replace("@UUID", Uuid, StringComparison.Ordinal);

    /// <summary>The registry types (REG): site, location, tag family, tag, server, device, link, custom field.</summary>
    public static IReadOnlyList<ObjectTypeDefinition> Registry { get; } =
    [
        new("registry.site", "Registry", "config.registry", S("""{"type":"object","required":["name"],"additionalProperties":false,"properties":{"name":@NAME,"timeZone":{"type":"string","minLength":1,"maxLength":64},"address":{"type":"string","maxLength":500}}}"""), []),
        new("registry.location", "Registry", "config.registry", S("""{"type":"object","required":["name"],"additionalProperties":false,"properties":{"name":@NAME,"parentId":@UUID,"kind":{"type":"string","enum":["site","building","floor","zone","room","area"]},"timeZone":{"type":"string","maxLength":64}}}"""), [new("/parentId", "registry.location")]),
        new("registry.tagFamily", "Registry", "config.registry", S("""{"type":"object","required":["key","name"],"additionalProperties":false,"properties":{"key":{"type":"string","pattern":"^[a-z][a-zA-Z0-9]*$","maxLength":64},"name":@NAME,"exclusive":{"type":"boolean","default":false}}}"""), []),
        new("registry.tag", "Registry", "config.registry", S("""{"type":"object","required":["key","name","familyId"],"additionalProperties":false,"properties":{"key":{"type":"string","pattern":"^[a-z][a-zA-Z0-9]*$","maxLength":64},"name":@NAME,"familyId":@UUID,"color":{"type":"string","pattern":"^#[0-9a-fA-F]{6}$"}}}"""), [new("/familyId", "registry.tagFamily")]),
        new("registry.server", "Registry", "config.registry", S("""{"type":"object","required":["type","connectionId","locationId"],"additionalProperties":false,"properties":{"type":{"type":"string","minLength":1},"connectionId":@UUID,"locationId":@UUID,"name":@NAME,"tags":{"type":"array","items":@UUID},"enabled":{"type":"boolean","default":true}}}"""),
            [new("/connectionId", "integrations.connection"), new("/locationId", "registry.location"), new("/tags", "registry.tag")]),
        new("registry.device", "Registry", "config.registry", S("""{"type":"object","required":["type","serverId","locationId"],"additionalProperties":false,"properties":{"type":{"type":"string","minLength":1},"serverId":@UUID,"parentId":@UUID,"locationId":@UUID,"name":@NAME,"tags":{"type":"array","items":@UUID},"enabled":{"type":"boolean","default":true},"maintenance":{"type":"boolean","default":false}}}"""),
            [new("/serverId", "registry.server"), new("/parentId", "registry.device"), new("/locationId", "registry.location"), new("/tags", "registry.tag")]) { Tabular = true },
        new("registry.link", "Registry", "config.registry", S("""{"type":"object","required":["from","to","kind"],"additionalProperties":false,"properties":{"from":@UUID,"to":@UUID,"kind":{"type":"string","enum":["cameraViews","controls","contains","dependsOn"]}}}"""), [new("/from", "registry.device"), new("/to", "registry.device")]),
        new("registry.customField", "Registry", "config.registry", S("""{"type":"object","required":["key","name","target","valueType"],"additionalProperties":false,"properties":{"key":{"type":"string","pattern":"^[a-z][a-zA-Z0-9]*$","maxLength":64},"name":@NAME,"target":{"type":"string","enum":["device","server","location"]},"valueType":{"type":"string","enum":["string","integer","decimal","boolean","enum"]}}}"""), []),
    ];

    /// <summary>The integration runtime types (IRT).</summary>
    public static IReadOnlyList<ObjectTypeDefinition> Integrations { get; } =
    [
        new("integrations.connection", "Integrations", "config.integrations", S("""{"type":"object","required":["name","package"],"additionalProperties":false,"properties":{"name":@NAME,"package":{"type":"string","minLength":1},"settings":{"type":"object"},"enabled":{"type":"boolean","default":true}}}"""), []),
    ];

    /// <summary>The scheduling types (SCH).</summary>
    public static IReadOnlyList<ObjectTypeDefinition> Scheduling { get; } =
    [
        new("scheduling.holidaySet", "Scheduling", "config.scheduling", S("""{"type":"object","required":["name"],"additionalProperties":false,"properties":{"name":@NAME,"country":{"type":"string","pattern":"^[A-Z]{2}$"},"days":{"type":"array","items":{"type":"object","required":["date"],"properties":{"date":{"type":"string","pattern":"^\\d{4}-\\d{2}-\\d{2}$"},"name":{"type":"string","maxLength":200}}}}}}"""), []),
        new("scheduling.calendar", "Scheduling", "config.scheduling", S("""{"type":"object","required":["key","name"],"additionalProperties":false,"properties":{"key":{"type":"string","pattern":"^[a-z][a-z0-9-]{1,63}$"},"name":@NAME,"timeZone":{"type":"string","maxLength":64},"holidaySets":{"type":"array","items":@UUID},"weekly":{"type":"object"}}}"""), [new("/holidaySets", "scheduling.holidaySet")]),
    ];

    /// <summary>The identity types (IDN).</summary>
    public static IReadOnlyList<ObjectTypeDefinition> Identity { get; } =
    [
        new("identity.user", "Identity", "config.identity", """{"type":"object","required":["username","displayName"],"additionalProperties":false,"properties":{"username":{"type":"string","pattern":"^[a-z0-9._-]{3,64}$"},"displayName":{"type":"string","minLength":1,"maxLength":200},"email":{"type":"string","maxLength":320},"enabled":{"type":"boolean","default":true}}}""", []),
    ];

    /// <summary>The history types (HIS): per-device or per-type property history policy overrides.</summary>
    public static IReadOnlyList<ObjectTypeDefinition> History { get; } =
    [
        new("history.policyOverride", "History", "config.history", S("""{"type":"object","required":["scope","path","mode"],"additionalProperties":false,"properties":{"scope":{"type":"string","enum":["device","type"]},"deviceId":@UUID,"typeId":{"type":"string","minLength":1},"path":{"type":"string","pattern":"^[a-z][a-zA-Z0-9]*\\.[a-z][a-zA-Z0-9]*$"},"mode":{"type":"string","enum":["everyChange","deadband","periodic","none"]},"kind":{"type":"string","enum":["absolute","percent"]},"threshold":{"type":"number","minimum":0},"interval":{"type":"string"}}}"""), [new("/deviceId", "registry.device")]),
    ];

    /// <summary>All the types, in the order publishing applies them (catalogue order: registry first).</summary>
    public static IReadOnlyList<ObjectTypeDefinition> All { get; } = [.. Registry, .. Integrations, .. Scheduling, .. Identity, .. History];

    /// <summary>Describes the object types of the existing modules over a document source.</summary>
    /// <param name="source">The live documents.</param>
    /// <returns>The types.</returns>
    public static IReadOnlyList<IConfigurableObjectType> Create(IConfigDocumentSource source) => [.. All.Select(d => new DocumentObjectType(d, source))];
}
