using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal.Api;

/// <summary>JSON forms of the registry API (REG §11).</summary>
internal static class RegistryJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(ApiJson.Options);
        options.Converters.Add(new SemVersionConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class SemVersionConverter : JsonConverter<SemVersion>
    {
        public override SemVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            SemVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException("Invalid semantic version.");

        public override void Write(Utf8JsonWriter writer, SemVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    /// <summary>
    /// Parses <c>{"changes":[{"op":"upsertDevice","device":{…},"expectedVersion":3}, …]}</c>: each change is the
    /// <see cref="RegistryChange"/> record in camelCase with an <c>op</c> discriminator; <c>patchDevice</c> carries a
    /// JSON Merge Patch in <c>patch</c>. Errors are field errors on <c>/changes/&lt;index&gt;</c>.
    /// </summary>
    public static Result<RegistryChangeBatch> ParseBatch(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return CommonErrors.ValidationFailed([FieldError.Create("/changes", CommonErrorCodes.ValueRequired)]);
        }

        var list = new List<RegistryChange>();
        var errors = new List<FieldError>();
        var index = 0;
        foreach (var element in changes.EnumerateArray())
        {
            var path = "/changes/" + index++;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String)
            {
                errors.Add(FieldError.Create(path + "/op", CommonErrorCodes.ValueRequired));
                continue;
            }

            try
            {
                var change = ParseChange(op.GetString()!, element, path, errors);
                if (change is not null)
                {
                    list.Add(change);
                }
            }
            catch (JsonException)
            {
                errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
            }
        }

        return errors.Count > 0 ? CommonErrors.ValidationFailed(errors) : new RegistryChangeBatch(list);
    }

    private static readonly Dictionary<string, Type> _ops = new(StringComparer.Ordinal)
    {
        ["upsertLocation"] = typeof(RegistryChange.UpsertLocation),
        ["deleteLocation"] = typeof(RegistryChange.DeleteLocation),
        ["upsertTag"] = typeof(RegistryChange.UpsertTag),
        ["deleteTag"] = typeof(RegistryChange.DeleteTag),
        ["upsertTagFamily"] = typeof(RegistryChange.UpsertTagFamily),
        ["upsertServer"] = typeof(RegistryChange.UpsertServer),
        ["deleteServer"] = typeof(RegistryChange.DeleteServer),
        ["upsertDevice"] = typeof(RegistryChange.UpsertDevice),
        ["deleteDevice"] = typeof(RegistryChange.DeleteDevice),
        ["upsertLink"] = typeof(RegistryChange.UpsertLink),
        ["deleteLink"] = typeof(RegistryChange.DeleteLink),
        ["upsertCustomField"] = typeof(RegistryChange.UpsertCustomField),
        ["updateSite"] = typeof(RegistryChange.UpdateSite),
    };

    private static RegistryChange? ParseChange(string op, JsonElement element, string path, List<FieldError> errors)
    {
        if (op == "patchDevice")
        {
            if (!element.TryGetProperty("id", out var idElement) || !Guid.TryParse(idElement.GetString(), out var id))
            {
                errors.Add(FieldError.Create(path + "/id", CommonErrorCodes.ValueRequired));
                return null;
            }

            if (!element.TryGetProperty("patch", out var patchElement))
            {
                errors.Add(FieldError.Create(path + "/patch", CommonErrorCodes.ValueRequired));
                return null;
            }

            var patch = ParseDevicePatch(patchElement, path + "/patch", errors);
            long? expected = element.TryGetProperty("expectedVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            return patch is null ? null : new RegistryChange.PatchDevice(DeviceId.From(id), patch, expected);
        }

        if (!_ops.TryGetValue(op, out var type))
        {
            errors.Add(FieldError.Create(path + "/op", CommonErrorCodes.ValueNotAllowed, ("value", op)));
            return null;
        }

        var change = (RegistryChange?)element.Deserialize(type, Options);
        return change switch
        {
            null => null,
            RegistryChange.UpsertDevice d => d with { Device = Normalize(d.Device) },
            RegistryChange.UpsertServer s => s with { Server = s.Server with { Tags = s.Server.Tags ?? [], CustomFields = s.Server.CustomFields ?? new Dictionary<string, WpValue>() } },
            RegistryChange.UpsertLocation l => l with { Location = l.Location with { Path = l.Location.Path ?? string.Empty } },
            _ => change,
        };
    }

    private static Device Normalize(Device device) => device with
    {
        Tags = device.Tags ?? [],
        Attributes = device.Attributes ?? new Dictionary<string, WpValue>(),
        CustomFields = device.CustomFields ?? new Dictionary<CustomFieldId, WpValue>(),
        UnsupportedCaps = device.UnsupportedCaps ?? new HashSet<CapabilityId>(),
        NativeId = device.NativeId ?? string.Empty,
        VendorName = device.VendorName ?? string.Empty,
    };

    /// <summary>
    /// JSON Merge Patch of a device (REG §11): <c>name</c> (null clears the override), <c>locationId</c>, <c>tags</c>
    /// (replace), <c>tagsAdd</c>, <c>tagsRemove</c>, <c>enabled</c>, <c>maintenance</c>, <c>attributes</c> (merge; null
    /// removes a key). Other members are refused.
    /// </summary>
    public static DevicePatch? ParseDevicePatch(JsonElement body, string prefix, List<FieldError> errors)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add(FieldError.Create(prefix.Length == 0 ? "/" : prefix, CommonErrorCodes.ValueInvalidFormat, ("expected", "JSON object")));
            return null;
        }

        var patch = new DevicePatch();
        var count = errors.Count;
        foreach (var property in body.EnumerateObject())
        {
            var path = prefix + "/" + property.Name;
            var value = property.Value;
            switch (property.Name)
            {
                case "name":
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        patch = patch with { Name = new Optional<LocalizedName?>(null) };
                    }
                    else if (value.ValueKind == JsonValueKind.String)
                    {
                        patch = patch with { Name = new Optional<LocalizedName?>(LocalizedName.Of(value.GetString()!)) };
                    }
                    else if (TryDeserialize<LocalizedName>(value, out var name) && name is not null)
                    {
                        patch = patch with { Name = new Optional<LocalizedName?>(name) };
                    }
                    else
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
                    }

                    break;
                case "locationId":
                    if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var location))
                    {
                        patch = patch with { Location = LocationId.From(location) };
                    }
                    else
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
                    }

                    break;
                case "tags":
                    if (Tags(value) is { } tags)
                    {
                        patch = patch with { TagsSet = new Optional<IReadOnlyList<TagId>>(tags) };
                    }
                    else
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
                    }

                    break;
                case "tagsAdd":
                case "tagsRemove":
                    if (Tags(value) is { } delta)
                    {
                        patch = property.Name == "tagsAdd" ? patch with { TagsAdd = delta } : patch with { TagsRemove = delta };
                    }
                    else
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
                    }

                    break;
                case "enabled":
                case "maintenance":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        patch = property.Name == "enabled" ? patch with { Enabled = value.GetBoolean() } : patch with { Maintenance = value.GetBoolean() };
                    }
                    else
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat, ("expected", "boolean")));
                    }

                    break;
                case "attributes":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
                        break;
                    }

                    var merge = new Dictionary<string, WpValue>(StringComparer.Ordinal);
                    foreach (var attribute in value.EnumerateObject())
                    {
                        if (attribute.Value.ValueKind == JsonValueKind.Null)
                        {
                            merge[attribute.Name] = WpValue.Null;
                        }
                        else if (TryDeserialize<WpValue>(attribute.Value, out var wp))
                        {
                            merge[attribute.Name] = wp;
                        }
                        else
                        {
                            errors.Add(FieldError.Create(path + "/" + attribute.Name, CommonErrorCodes.ValueInvalidFormat));
                        }
                    }

                    patch = patch with { AttributesMerge = merge };
                    break;
                default:
                    errors.Add(FieldError.Create(path, CommonErrorCodes.ValueNotAllowed));
                    break;
            }
        }

        return errors.Count > count ? null : patch;
    }

    private static List<TagId>? Tags(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tags = new List<TagId>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                return null;
            }

            tags.Add(TagId.From(id));
        }

        return tags;
    }

    private static bool TryDeserialize<T>(JsonElement value, out T result)
    {
        try
        {
            result = value.Deserialize<T>(Options)!;
            return result is not null;
        }
        catch (JsonException)
        {
            result = default!;
            return false;
        }
    }
}

/// <summary>The JSON shape of a device (REG §11): <c>name</c> is the user override, <c>displayName</c> is resolved in the caller's culture.</summary>
internal sealed record DeviceDto(Guid Id, TypeRefDto Type, Guid ServerId, Guid? ParentId, string NativeId, LocalizedName? Name, string VendorName,
    string DisplayName, Guid LocationId, IReadOnlyList<Guid> Tags, IReadOnlyDictionary<string, WpValue> Attributes,
    IReadOnlyDictionary<string, WpValue> CustomFields, DeviceGeometry? Geometry, bool Enabled, bool Maintenance, bool Orphaned,
    DateTimeOffset CreatedAt, long Version)
{
    public static DeviceDto Of(Device d, string culture) => new(d.Id.Value, TypeRefDto.Of(d.Type), d.ServerId.Value, d.ParentId?.Value, d.NativeId,
        d.DisplayName, d.VendorName, d.DisplayName?.Resolve(culture) ?? d.VendorName, d.LocationId.Value, [.. d.Tags.Select(t => t.Value)],
        d.Attributes, d.CustomFields.ToDictionary(c => c.Key.Value.ToString(), c => c.Value), d.Geometry, d.Enabled, d.Maintenance, d.Orphaned,
        d.CreatedAt, d.Version);
}

/// <summary>A type reference: <c>{ id, version }</c>.</summary>
internal sealed record TypeRefDto(string Id, string Version)
{
    public static TypeRefDto Of(TypeRef t) => new(t.Id.Value, t.Version.ToString());
}

/// <summary>The JSON shape of a server.</summary>
internal sealed record ServerDto(Guid Id, TypeRefDto Type, Guid ConnectionId, Guid? ParentId, string NativeId, LocalizedName? Name, string VendorName,
    string DisplayName, Guid LocationId, IReadOnlyList<Guid> Tags, IReadOnlyDictionary<string, WpValue> CustomFields, bool Enabled, bool Maintenance, long Version)
{
    public static ServerDto Of(Server s, string culture) => new(s.Id.Value, TypeRefDto.Of(s.Type), s.ConnectionId.Value, s.ParentId?.Value, s.NativeId,
        s.DisplayName, s.VendorName, s.DisplayName?.Resolve(culture) ?? s.VendorName, s.LocationId.Value, [.. s.Tags.Select(t => t.Value)],
        s.CustomFields, s.Enabled, s.Maintenance, s.Version);
}

/// <summary>A discovered item.</summary>
internal sealed record DiscoveredDto(string NativeId, string Kind, string ServerNativeId, string? ParentNativeId, TypeRefDto Type, string VendorName,
    IReadOnlyDictionary<string, WpValue> VendorAttributes, string Status, DateTimeOffset FirstSeen, DateTimeOffset LastSeen)
{
    public static DiscoveredDto Of(DiscoveredItem i) => new(i.NativeId, i.Kind, i.ServerNativeId, i.ParentNativeId, TypeRefDto.Of(i.Type), i.VendorName,
        i.VendorAttributes, JsonNamingPolicy.CamelCase.ConvertName(i.Status.ToString()), i.FirstSeen, i.LastSeen);
}
