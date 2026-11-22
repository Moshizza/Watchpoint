using System.Text.Json;
using System.Text.Json.Nodes;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents;

/// <summary>
/// Parsed restricted parameter schema with validation and default application.
/// </summary>
public sealed record ParameterSchema
{
    public JsonElement RawElement { get; }
    public JsonNode? RawNode { get; }

    public ParameterSchema(JsonElement rawElement)
    {
        RawElement = rawElement.Clone();
        RawNode = JsonNode.Parse(RawElement.GetRawText());
    }

    public static Result<ParameterSchema> Parse(JsonElement element)
    {
        var problems = ValidateSchemaStructure(element);
        if (problems.Count > 0)
        {
            return Error.Validation("common.validation.failed", problems);
        }

        return new ParameterSchema(element);
    }

    public static IReadOnlyList<FieldError> ValidateSchemaStructure(JsonElement element)
    {
        var errors = new List<FieldError>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add(CommonErrors.InvalidFormatAt(string.Empty, "object"));
            return errors;
        }

        CheckRestrictedKeywordsAndDepth(element, "", depth: 0, errors);
        return errors;
    }

    private static readonly HashSet<string> _allowedTopKeywords =
    [
        "type", "properties", "required", "x-wp-ui"
    ];

    private static readonly HashSet<string> _allowedFieldKeywords =
    [
        "type", "enum", "default", "minimum", "maximum", "minLength", "maxLength",
        "pattern", "format", "items", "maxItems", "minItems", "required", "properties",
        "entity", "deviceCategory", "deviceType", "capability"
    ];

    private static readonly HashSet<string> _allowedTypes =
    [
        "string", "integer", "number", "boolean", "duration", "timestamp", "entityRef", "array", "object"
    ];

    private static void CheckRestrictedKeywordsAndDepth(JsonElement element, string currentPath, int depth, List<FieldError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.StartsWith("x-", StringComparison.Ordinal))
            {
                continue;
            }

            string propPath = currentPath + "/" + prop.Name;

            if (depth == 0)
            {
                if (!_allowedTopKeywords.Contains(prop.Name))
                {
                    errors.Add(CommonErrors.NotAllowedAt(propPath, [.. _allowedTopKeywords]));
                }
            }
            else
            {
                if (!_allowedFieldKeywords.Contains(prop.Name))
                {
                    errors.Add(CommonErrors.NotAllowedAt(propPath, [.. _allowedFieldKeywords]));
                }
            }

            if (prop.NameEquals("properties") && prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (depth >= 2)
                {
                    errors.Add(CommonErrors.OutOfRangeAt(propPath, 0, 1));
                    continue;
                }

                foreach (var child in prop.Value.EnumerateObject())
                {
                    CheckFieldDefinition(child.Value, propPath + "/" + child.Name, depth + 1, errors);
                }
            }
        }

        // Validate visibleWhen references if present in x-wp-ui
        if (element.TryGetProperty("x-wp-ui", out var uiElem) && uiElem.ValueKind == JsonValueKind.Object)
        {
            if (uiElem.TryGetProperty("visibleWhen", out var visibleWhen) && visibleWhen.ValueKind == JsonValueKind.Object)
            {
                var declaredProperties = new HashSet<string>();
                if (element.TryGetProperty("properties", out var topProps) && topProps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in topProps.EnumerateObject())
                    {
                        declaredProperties.Add(p.Name);
                    }
                }

                foreach (var rule in visibleWhen.EnumerateObject())
                {
                    string rulePath = "/x-wp-ui/visibleWhen/" + rule.Name;
                    if (rule.Value.ValueKind == JsonValueKind.Object && rule.Value.TryGetProperty("field", out var fieldProp))
                    {
                        string? fieldName = fieldProp.GetString();
                        if (fieldName != null && !declaredProperties.Contains(fieldName))
                        {
                            errors.Add(CommonErrors.NotAllowedAt(rulePath + "/field", [.. declaredProperties]));
                        }
                    }
                }
            }
        }
    }

    private static void CheckFieldDefinition(JsonElement fieldElem, string fieldPath, int depth, List<FieldError> errors)
    {
        if (fieldElem.ValueKind != JsonValueKind.Object)
        {
            errors.Add(CommonErrors.InvalidFormatAt(fieldPath, "object"));
            return;
        }

        foreach (var prop in fieldElem.EnumerateObject())
        {
            if (prop.Name.StartsWith("x-", StringComparison.Ordinal)) continue;

            if (!_allowedFieldKeywords.Contains(prop.Name))
            {
                errors.Add(CommonErrors.NotAllowedAt(fieldPath + "/" + prop.Name, [.. _allowedFieldKeywords]));
            }
        }

        if (fieldElem.TryGetProperty("type", out var typeProp))
        {
            string? typeStr = typeProp.GetString();
            if (typeStr == null || !_allowedTypes.Contains(typeStr))
            {
                errors.Add(CommonErrors.NotAllowedAt(fieldPath + "/type", [.. _allowedTypes]));
            }

            if (typeStr == "object")
            {
                if (depth >= 2)
                {
                    errors.Add(CommonErrors.OutOfRangeAt(fieldPath, 0, 1));
                    return;
                }

                if (fieldElem.TryGetProperty("properties", out var nestedProps) && nestedProps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var nested in nestedProps.EnumerateObject())
                    {
                        CheckFieldDefinition(nested.Value, fieldPath + "/properties/" + nested.Name, depth + 1, errors);
                    }
                }
            }
            else if (typeStr == "array")
            {
                if (fieldElem.TryGetProperty("items", out var itemsProp))
                {
                    CheckFieldDefinition(itemsProp, fieldPath + "/items", depth + 1, errors);
                }
            }
        }
    }

    public IReadOnlyList<FieldError> Validate(JsonElement value)
    {
        var errors = new List<FieldError>();
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(CommonErrors.InvalidFormatAt(string.Empty, "object"));
            return errors;
        }

        if (!RawElement.TryGetProperty("properties", out var propsElem) || propsElem.ValueKind != JsonValueKind.Object)
        {
            return errors;
        }

        // Check required properties
        if (RawElement.TryGetProperty("required", out var reqElem) && reqElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reqElem.EnumerateArray())
            {
                string? reqName = item.GetString();
                if (reqName != null && !value.TryGetProperty(reqName, out _))
                {
                    errors.Add(CommonErrors.RequiredAt("/" + reqName));
                }
            }
        }

        // Check each provided property
        foreach (var prop in value.EnumerateObject())
        {
            string propPath = "/" + prop.Name;
            if (!propsElem.TryGetProperty(prop.Name, out var fieldDef))
            {
                errors.Add(CommonErrors.NotAllowedAt(propPath, []));
                continue;
            }

            ValidateFieldValue(prop.Value, fieldDef, propPath, errors);
        }

        return errors;
    }

    private static void ValidateFieldValue(JsonElement val, JsonElement def, string path, List<FieldError> errors)
    {
        if (def.TryGetProperty("enum", out var enumArray) && enumArray.ValueKind == JsonValueKind.Array)
        {
            bool matches = false;
            var allowedValues = new List<string>();
            foreach (var e in enumArray.EnumerateArray())
            {
                string? s = e.ToString();
                if (s != null) allowedValues.Add(s);
                if (e.ValueKind == val.ValueKind && e.GetRawText() == val.GetRawText())
                {
                    matches = true;
                }
            }

            if (!matches)
            {
                errors.Add(CommonErrors.NotAllowedAt(path, [.. allowedValues]));
            }
        }

        if (def.TryGetProperty("type", out var typeElem))
        {
            string? expectedType = typeElem.GetString();
            switch (expectedType)
            {
                case "string":
                case "duration":
                case "timestamp":
                    if (val.ValueKind != JsonValueKind.String)
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, expectedType));
                    }
                    break;
                case "integer":
                    if (val.ValueKind != JsonValueKind.Number || !val.TryGetInt64(out _))
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, "integer"));
                    }
                    else
                    {
                        CheckRange(val, def, path, errors);
                    }
                    break;
                case "number":
                    if (val.ValueKind != JsonValueKind.Number)
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, "number"));
                    }
                    else
                    {
                        CheckRange(val, def, path, errors);
                    }
                    break;
                case "boolean":
                    if (val.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, "boolean"));
                    }
                    break;
                case "array":
                    if (val.ValueKind != JsonValueKind.Array)
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, "array"));
                    }
                    break;
                case "object":
                    if (val.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add(CommonErrors.InvalidFormatAt(path, "object"));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Checks a numeric value against the field's <c>minimum</c> and <c>maximum</c> (both inclusive, each optional; bounds that are not
    /// numbers, such as the ISO bounds of durations, are not checked here). A value outside gives <c>common.value.out_of_range</c> with the
    /// arguments <c>min</c> and <c>max</c> (null when that side is unbounded).
    /// </summary>
    private static void CheckRange(JsonElement val, JsonElement def, string path, List<FieldError> errors)
    {
        double? min = def.TryGetProperty("minimum", out var minElem) && minElem.ValueKind == JsonValueKind.Number ? minElem.GetDouble() : null;
        double? max = def.TryGetProperty("maximum", out var maxElem) && maxElem.ValueKind == JsonValueKind.Number ? maxElem.GetDouble() : null;
        var number = val.GetDouble();
        if ((min is { } lower && number < lower) || (max is { } upper && number > upper))
        {
            errors.Add(FieldError.Create(path, CommonErrorCodes.ValueOutOfRange, ("min", min), ("max", max)));
        }
    }

    public JsonElement ApplyDefaults(JsonElement value)
    {
        JsonNode? node = JsonNode.Parse(value.GetRawText());
        if (node is not JsonObject obj)
        {
            return value;
        }

        if (RawElement.TryGetProperty("properties", out var propsElem) && propsElem.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsElem.EnumerateObject())
            {
                if (!obj.ContainsKey(prop.Name) && prop.Value.TryGetProperty("default", out var defaultVal))
                {
                    obj[prop.Name] = JsonNode.Parse(defaultVal.GetRawText());
                }
            }
        }

        return JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone();
    }
}
