using System.Text.Json;
using Json.Schema;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Host.Dev;

/// <summary>
/// Reads and validates a development seed before anything is applied (spec HST §9): the JSON Schema
/// <c>schemas/dev-seed.schema.json</c>, then the references between entries.
/// </summary>
public static class DevSeedReader
{
    /// <summary>The code of the validation error; its field errors name the JSON paths.</summary>
    public const string InvalidCode = "host.seed.invalid";

    /// <summary>The code returned by a seed step asked to run outside Development.</summary>
    public const string NotDevelopmentCode = "host.seed.not_development";

    private const string SchemaResource = "schemas/dev-seed.schema.json";

    private static readonly Lazy<JsonSchema> _schema = new(LoadSchema);

    /// <summary>Parses and validates a seed document.</summary>
    /// <param name="utf8Json">The document.</param>
    /// <returns>The seed, or a validation error (<c>host.seed.invalid</c>).</returns>
    public static Result<DevSeedDocument> Read(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return Invalid([CommonErrors.InvalidFormatAt(string.Empty, "JSON: " + ex.Message)]);
        }

        using (document)
        {
            var root = document.RootElement;
            var evaluation = _schema.Value.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                var schemaErrors = new List<FieldError>();
                Collect(evaluation, schemaErrors);
                return Invalid(schemaErrors);
            }

            var seed = Map(root);
            var errors = CheckReferences(seed);
            return errors.Count > 0 ? Invalid(errors) : seed;
        }
    }

    private static Error Invalid(IReadOnlyList<FieldError> fields) => Error.Validation(InvalidCode, fields);

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(DevSeedReader).Assembly.GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException($"Embedded resource '{SchemaResource}' is missing.");
        using var reader = new StreamReader(stream);

        // A private registry: the schema's $id is not registered globally.
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    }

    private static void Collect(EvaluationResults results, List<FieldError> errors)
    {
        if (!results.IsValid && results.Errors is not null)
        {
            foreach (var error in results.Errors)
            {
                errors.Add(CommonErrors.InvalidFormatAt(results.InstanceLocation.ToString(), error.Value));
            }
        }

        foreach (var detail in results.Details ?? [])
        {
            Collect(detail, errors);
        }
    }

    private static DevSeedDocument Map(JsonElement root)
    {
        var site = root.GetProperty("site");
        return new DevSeedDocument(
            new DevSeedSite(site.GetProperty("name").GetString()!, site.GetProperty("timeZone").GetString()!),
            [.. Items(root, "locations").Select(l => new DevSeedLocation(
                l.GetProperty("key").GetString()!,
                l.TryGetProperty("parent", out var parent) ? parent.GetString() : null,
                l.GetProperty("name").GetString()!,
                l.GetProperty("kind").GetString()!))],
            [.. Items(root, "tags").Select(t => new DevSeedTag(
                t.GetProperty("family").GetString()!, t.GetProperty("key").GetString()!, t.GetProperty("name").GetString()!))],
            [.. Items(root, "users").Select(u => new DevSeedUser(
                u.GetProperty("login").GetString()!,
                u.GetProperty("password").GetString()!,
                u.TryGetProperty("displayName", out var displayName) ? displayName.GetString()! : u.GetProperty("login").GetString()!,
                u.TryGetProperty("administrator", out var administrator) && administrator.GetBoolean()))],
            [.. Items(root, "connections").Select(DevSeedConnection.From)]);
    }

    private static List<JsonElement> Items(JsonElement root, string property) =>
        root.TryGetProperty(property, out var array) ? [.. array.EnumerateArray()] : [];

    /// <summary>What a schema cannot express: unique keys, known parents and locations, parents before children, one root.</summary>
    private static List<FieldError> CheckReferences(DevSeedDocument seed)
    {
        var errors = new List<FieldError>();
        var locations = new HashSet<string>(StringComparer.Ordinal);
        var roots = 0;
        for (var i = 0; i < seed.Locations.Count; i++)
        {
            var location = seed.Locations[i];
            if (location.Parent is null)
            {
                if (++roots > 1)
                {
                    errors.Add(Reference($"/locations/{i}/parent", "a single root location (the others need a parent)"));
                }
            }
            else if (!locations.Contains(location.Parent))
            {
                // Also catches a parent declared later and cycles: a parent must come before its children.
                errors.Add(Reference($"/locations/{i}/parent", $"the key of an earlier location (unknown location key '{location.Parent}')"));
            }

            if (!locations.Add(location.Key))
            {
                errors.Add(CommonErrors.DuplicateAt($"/locations/{i}/key", location.Key));
            }
        }

        var tags = new HashSet<(string, string)>();
        for (var i = 0; i < seed.Tags.Count; i++)
        {
            if (!tags.Add((seed.Tags[i].Family, seed.Tags[i].Key)))
            {
                errors.Add(CommonErrors.DuplicateAt($"/tags/{i}/key", seed.Tags[i].Key));
            }
        }

        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < seed.Users.Count; i++)
        {
            if (!logins.Add(seed.Users[i].Login))
            {
                errors.Add(CommonErrors.DuplicateAt($"/users/{i}/login", seed.Users[i].Login));
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < seed.Connections.Count; i++)
        {
            var connection = seed.Connections[i];
            if (connection.Location is not null && !locations.Contains(connection.Location))
            {
                errors.Add(Reference($"/connections/{i}/location", $"the key of a location (unknown location key '{connection.Location}')"));
            }

            if (!names.Add(connection.Name))
            {
                errors.Add(CommonErrors.DuplicateAt($"/connections/{i}/name", connection.Name));
            }
        }

        return errors;
    }

    private static FieldError Reference(string path, string expected) =>
        FieldError.Create(path, ErrorCode.FromTrusted("host.seed.reference_unknown"), ("expected", expected));
}
