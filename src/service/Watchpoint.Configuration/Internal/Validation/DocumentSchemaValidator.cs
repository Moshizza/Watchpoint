using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Internal.Validation;

/// <summary>
/// Validates a document against the JSON Schema subset configuration object types use: <c>type</c>, <c>properties</c>,
/// <c>required</c>, <c>additionalProperties: false</c>, <c>items</c>, <c>enum</c>, <c>minimum</c>, <c>maximum</c>, <c>minLength</c>,
/// <c>maxLength</c>, <c>pattern</c>, <c>format: uuid</c>, <c>default</c>. Pure and deterministic: the problems are data (code, arguments,
/// JSON pointer), never message text, and come in document order.
/// </summary>
internal static class DocumentSchemaValidator
{
    private static readonly TimeSpan _patternTimeout = TimeSpan.FromMilliseconds(100);

    public static IReadOnlyList<Problem> Validate(JsonElement schema, JsonElement document, ConfigObjectRef subject)
    {
        List<Problem> problems = [];
        Check(schema, document, string.Empty, subject, problems);
        return problems;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, ConfigObjectRef subject, List<Problem> problems)
    {
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && !TypeMatches(type.GetString()!, value))
        {
            problems.Add(Error(CommonErrorCodes.ValueInvalidFormat.Value, Args.Of(("expected", type.GetString())), subject, path));
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(a => JsonElement.DeepEquals(a, value)))
        {
            problems.Add(Error(
                CommonErrorCodes.ValueNotAllowed.Value,
                Args.Of(("allowed", allowed.EnumerateArray().Select(a => a.ToString()).ToList())),
                subject,
                path));
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(schema, value, path, subject, problems);
                break;
            case JsonValueKind.Array:
                if (schema.TryGetProperty("items", out var items))
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Check(items, item, path + "/" + index.ToString(CultureInfo.InvariantCulture), subject, problems);
                        index++;
                    }
                }

                break;
            case JsonValueKind.String:
                CheckString(schema, value.GetString()!, path, subject, problems);
                break;
            case JsonValueKind.Number:
                CheckNumber(schema, value, path, subject, problems);
                break;
        }
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, ConfigObjectRef subject, List<Problem> problems)
    {
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    List<FixSuggestion> fixes = [];
                    if (properties is { } props && props.TryGetProperty(name, out var propertySchema) && propertySchema.TryGetProperty("default", out var def))
                    {
                        fixes.Add(new FixSuggestion(
                            ConfigurationErrors.FixSetDefault, Args.Of(("field", name)), JsonPatch.One("add", path + "/" + Escape(name), JsonNode.Parse(def.GetRawText()))));
                    }

                    problems.Add(Error(CommonErrorCodes.ValueRequired.Value, Args.Of(), subject, path + "/" + Escape(name), fixes));
                }
            }
        }

        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            var childPath = path + "/" + Escape(property.Name);
            if (properties is { } props && props.TryGetProperty(property.Name, out var childSchema))
            {
                Check(childSchema, property.Value, childPath, subject, problems);
            }
            else if (closed)
            {
                problems.Add(Error(
                    CommonErrorCodes.ValueNotAllowed.Value,
                    Args.Of(("allowed", properties is { } all ? all.EnumerateObject().Select(x => x.Name).ToList() : [])),
                    subject,
                    childPath,
                    [new FixSuggestion(ConfigurationErrors.FixRemoveField, Args.Of(("field", property.Name)), JsonPatch.One("remove", childPath))]));
            }
        }
    }

    private static void CheckString(JsonElement schema, string text, string path, ConfigObjectRef subject, List<Problem> problems)
    {
        if (schema.TryGetProperty("minLength", out var min) && min.TryGetInt32(out var minLength) && text.Length < minLength)
        {
            problems.Add(Error(CommonErrorCodes.ValueTooShort.Value, Args.Of(("min", minLength)), subject, path));
        }

        if (schema.TryGetProperty("maxLength", out var max) && max.TryGetInt32(out var maxLength) && text.Length > maxLength)
        {
            problems.Add(Error(CommonErrorCodes.ValueTooLong.Value, Args.Of(("max", maxLength)), subject, path));
        }

        if (schema.TryGetProperty("format", out var format) && format.GetString() == "uuid" && !Guid.TryParseExact(text, "D", out _))
        {
            problems.Add(Error(CommonErrorCodes.ValueInvalidFormat.Value, Args.Of(("expected", "uuid")), subject, path));
        }

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.GetString() is { } expression)
        {
            bool matches;
            try
            {
                matches = Regex.IsMatch(text, expression, RegexOptions.CultureInvariant, _patternTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                matches = false;
            }

            if (!matches)
            {
                problems.Add(Error(CommonErrorCodes.ValueInvalidFormat.Value, Args.Of(("expected", expression)), subject, path));
            }
        }
    }

    private static void CheckNumber(JsonElement schema, JsonElement value, string path, ConfigObjectRef subject, List<Problem> problems)
    {
        var number = value.GetDouble();
        var hasMin = schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number;
        var hasMax = schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number;
        if ((hasMin && number < min.GetDouble()) || (hasMax && number > max.GetDouble()))
        {
            var nearest = hasMin && number < min.GetDouble() ? min : max;
            problems.Add(Error(
                CommonErrorCodes.ValueOutOfRange.Value,
                Args.Of(("min", hasMin ? min.GetDouble() : null), ("max", hasMax ? max.GetDouble() : null)),
                subject,
                path,
                [new FixSuggestion(ConfigurationErrors.FixSetNearest, Args.Of(("value", nearest.GetDouble())), JsonPatch.One("replace", path, JsonNode.Parse(nearest.GetRawText())))]));
        }
    }

    private static bool TypeMatches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static Problem Error(string code, IReadOnlyDictionary<string, object?> args, ConfigObjectRef subject, string path, IReadOnlyList<FixSuggestion>? fixes = null) =>
        new(ProblemSeverity.Error, code, args, subject, path.Length == 0 ? string.Empty : path, fixes ?? []);
}
