using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Internal;

/// <summary>
/// Flattens device types according to spec MOD §5.1.
/// </summary>
internal static class TypeResolver
{
    public static Result<DeviceTypeDefinition> Resolve(
        DeviceTypeDocument document,
        string source,
        Func<TypeId, SemVersionRange, Result<DeviceTypeDocument>> typeLookup,
        Func<CapabilityId, SemVersionRange, Result<CapabilityDefinition>> capabilityLookup)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(typeLookup);
        ArgumentNullException.ThrowIfNull(capabilityLookup);

        var chain = new List<DeviceTypeDocument>();
        var lineage = new List<TypeRef>();

        var current = document;
        while (current is not null)
        {
            chain.Add(current);
            lineage.Add(new TypeRef(current.Id, current.Version));
            if (current.Extends is { } ext)
            {
                var parentRes = typeLookup(ext.Id, ext.Version);
                if (!parentRes.TryGetValue(out var parentDoc, out var parentErr))
                {
                    return parentErr;
                }

                current = parentDoc;
            }
            else
            {
                current = null;
            }
        }

        chain.Reverse();

        var capUses = new Dictionary<CapabilityId, CapabilityUse>(chain.Count);
        foreach (var doc in chain)
        {
            foreach (var capUse in doc.Capabilities)
            {
                if (capUses.TryGetValue(capUse.Id, out var existingUse))
                {
                    var resolvedRange = ResolveRange(existingUse.Version, capUse.Version);
                    var mergedOptional = existingUse.Optional && capUse.Optional;
                    var mergedExclude = MergeExcludes(existingUse.Exclude, capUse.Exclude);
                    capUses[capUse.Id] = new CapabilityUse(capUse.Id, resolvedRange, mergedOptional, mergedExclude);
                }
                else
                {
                    capUses[capUse.Id] = capUse;
                }
            }
        }

        var resolvedCaps = new Dictionary<CapabilityId, CapabilityUseDefinition>();
        var allProperties = new Dictionary<PropertyPath, PropertyDefinition>();
        var allCommands = new Dictionary<CommandPath, CommandDefinition>();
        var allEvents = new Dictionary<EventPath, EventDefinition>();

        foreach (var (capId, capUse) in capUses)
        {
            var capRes = capabilityLookup(capId, capUse.Version);
            if (!capRes.TryGetValue(out var capDef, out var capErr))
            {
                return capErr;
            }

            resolvedCaps[capId] = new CapabilityUseDefinition(capId, capDef.Version, capUse.Optional);

            var excludedCommands = capUse.Exclude?.Commands ?? [];
            var excludedEvents = capUse.Exclude?.Events ?? [];
            var excludedEnums = capUse.Exclude?.EnumValues ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            foreach (var (mId, prop) in capDef.Properties)
            {
                var propPath = new PropertyPath(capId, mId);
                var enumValues = prop.EnumValues;

                if (excludedEnums.TryGetValue(mId.Value, out var excludedCodes))
                {
                    if (prop.Default is { Kind: Kernel.Values.WpValueKind.Enum } defVal
                        && excludedCodes.Contains(defVal.AsEnum(), StringComparer.Ordinal))
                    {
                        return ModelErrors.ExcludeDefaultError(propPath.Value, defVal.AsEnum());
                    }

                    enumValues = enumValues.Where(e => !excludedCodes.Contains(e.Code, StringComparer.Ordinal)).ToList();
                }

                allProperties[propPath] = prop with { Path = propPath, EnumValues = enumValues };
            }

            foreach (var (mId, cmd) in capDef.Commands)
            {
                if (excludedCommands.Contains(mId.Value, StringComparer.Ordinal))
                {
                    continue;
                }

                var cmdPath = new CommandPath(capId, mId);
                allCommands[cmdPath] = cmd with { Path = cmdPath };
            }

            foreach (var (mId, evt) in capDef.Events)
            {
                if (excludedEvents.Contains(mId.Value, StringComparer.Ordinal))
                {
                    continue;
                }

                var evtPath = new EventPath(capId, mId);
                allEvents[evtPath] = evt with { Path = evtPath };
            }
        }

        var mergedAttributes = MergeAttributes(chain);

        var leaf = chain[^1];
        var role = leaf.Role;
        var category = leaf.Category;
        var display = leaf.Display;
        var licenceClass = leaf.LicenceClass;
        var allowedChildren = leaf.AllowedChildTypes;

        IReadOnlyList<string> defaultAlarmRules = [];
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].DefaultAlarmRules is { Count: > 0 } rules)
            {
                defaultAlarmRules = rules;
                break;
            }
        }

        var stateOverrides = new List<StateOverride>();
        var defaultLinks = new List<DefaultLink>();

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var doc = chain[i];
            if (doc.StateSummary?.Overrides is { } ovs)
            {
                foreach (var ov in ovs)
                {
                    var propPath = PropertyPath.FromTrusted(ov.Property);
                    var values = ov.Values.Select(v => ParseJsonValueToWpValue(v)).ToList();
                    var overallState = Enum.Parse<OverallState>(ov.State, ignoreCase: true);
                    stateOverrides.Add(new StateOverride(propPath, values, overallState));
                }
            }

            if (doc.DefaultLinks is { } links)
            {
                defaultLinks.AddRange(links);
            }
        }

        return new DeviceTypeDefinition
        {
            Ref = new TypeRef(leaf.Id, leaf.Version),
            Role = role,
            Category = category,
            Source = source,
            Lineage = lineage,
            Capabilities = resolvedCaps,
            Properties = allProperties,
            Commands = allCommands,
            Events = allEvents,
            Attributes = mergedAttributes,
            AllowedChildTypes = allowedChildren,
            LicenceClass = licenceClass,
            Display = display,
            StateOverrides = stateOverrides,
            DefaultAlarmRules = defaultAlarmRules,
            DefaultLinks = defaultLinks,
        };
    }

    private static SemVersionRange ResolveRange(SemVersionRange parentRange, SemVersionRange childRange)
    {
        var pStr = parentRange.ToString();
        var cStr = childRange.ToString();
        return string.CompareOrdinal(pStr, cStr) >= 0 ? parentRange : childRange;
    }

    private static CapabilityExclude? MergeExcludes(CapabilityExclude? a, CapabilityExclude? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        var cmds = (a.Commands ?? []).Concat(b.Commands ?? []).Distinct(StringComparer.Ordinal).ToList();
        var evts = (a.Events ?? []).Concat(b.Events ?? []).Distinct(StringComparer.Ordinal).ToList();

        var enumDict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (a.EnumValues is not null)
        {
            foreach (var (k, v) in a.EnumValues)
            {
                enumDict[k] = v;
            }
        }

        if (b.EnumValues is not null)
        {
            foreach (var (k, v) in b.EnumValues)
            {
                if (enumDict.TryGetValue(k, out var existing))
                {
                    enumDict[k] = existing.Concat(v).Distinct(StringComparer.Ordinal).ToList();
                }
                else
                {
                    enumDict[k] = v;
                }
            }
        }

        return new CapabilityExclude(cmds, evts, enumDict);
    }

    private static ParameterSchema MergeAttributes(IReadOnlyList<DeviceTypeDocument> chain)
    {
        var propertiesMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var requiredSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var doc in chain)
        {
            if (doc.Attributes is null)
            {
                continue;
            }

            var elem = doc.Attributes.RawElement;
            if (elem.ValueKind == JsonValueKind.Object)
            {
                if (elem.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in props.EnumerateObject())
                    {
                        propertiesMap[prop.Name] = prop.Value;
                    }
                }

                if (elem.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in req.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String)
                        {
                            requiredSet.Add(r.GetString()!);
                        }
                    }
                }
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (var (name, propElem) in propertiesMap)
            {
                writer.WritePropertyName(name);
                propElem.WriteTo(writer);
            }

            writer.WriteEndObject();

            if (requiredSet.Count > 0)
            {
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                foreach (var req in requiredSet.OrderBy(r => r, StringComparer.Ordinal))
                {
                    writer.WriteStringValue(req);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        using var jsonDoc = JsonDocument.Parse(stream.ToArray());
        return new ParameterSchema(jsonDoc.RootElement.Clone());
    }

    private static Kernel.Values.WpValue ParseJsonValueToWpValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => Kernel.Values.WpValue.Bool(true),
            JsonValueKind.False => Kernel.Values.WpValue.Bool(false),
            JsonValueKind.Number when element.TryGetInt64(out var l) => Kernel.Values.WpValue.Int(l),
            JsonValueKind.Number when element.TryGetDecimal(out var d) => Kernel.Values.WpValue.Decimal(d),
            JsonValueKind.String => Kernel.Values.WpValue.Enum(element.GetString()!),
            _ => Kernel.Values.WpValue.Null,
        };
    }
}
