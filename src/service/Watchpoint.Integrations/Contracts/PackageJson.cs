using System.Text.Json;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Contracts;

/// <summary>JSON form of the package contract types (API §10 responses and the <c>packages.problems</c> column).</summary>
public static class PackageJson
{
    /// <summary>Writes a package.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="package">The package.</param>
    public static void Write(Utf8JsonWriter writer, PackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(package);
        writer.WriteStartObject();
        writer.WriteString("id", package.Id.Value);
        writer.WriteString("version", package.Version.ToString());
        writer.WriteString("state", StateName(package.State));
        writer.WriteString("vendor", package.Vendor);
        writer.WriteString("category", package.Category);
        writer.WriteString("source", package.Source);
        writer.WriteString("filePath", package.FilePath);
        writer.WriteStartArray("connectors");
        foreach (var connector in package.Connectors)
        {
            writer.WriteStartObject();
            writer.WriteString("serverType", connector.ServerType);
            WriteStrings(writer, "features", connector.Features);
            writer.WriteString("supportedVendorVersions", connector.SupportedVendorVersions);
            writer.WriteBoolean("hasLocator", connector.HasLocator);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartObject("licence");
        writer.WriteString("class", package.Licence.Class);
        writer.WriteString("featureKey", package.Licence.FeatureKey);
        writer.WriteEndObject();
        WriteStrings(writer, "platforms", package.Platforms);
        writer.WriteString("isolation", package.Isolation);
        writer.WritePropertyName("problems");
        Write(writer, package.Problems);
        writer.WriteBoolean("signedByDevelopmentKey", package.SignedByDevelopmentKey);
        writer.WriteString("discoveredAt", package.DiscoveredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    /// <summary>Writes a list of problems.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="problems">The problems.</param>
    public static void Write(Utf8JsonWriter writer, IReadOnlyList<DocumentProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(problems);
        writer.WriteStartArray();
        foreach (var problem in problems)
        {
            writer.WriteStartObject();
            writer.WriteString("severity", problem.Severity == ProblemSeverity.Error ? "error" : "warning");
            writer.WriteString("source", problem.SourcePath);
            writer.WriteString("path", problem.JsonPath);
            writer.WriteString("code", problem.Code.Value);
            writer.WriteStartObject("args");
            foreach (var (key, value) in problem.Args)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>Writes a settings schema.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="schema">The schema.</param>
    public static void Write(Utf8JsonWriter writer, ConnectionSettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(schema);
        writer.WriteStartObject();
        writer.WriteString("serverType", schema.ServerType);
        writer.WritePropertyName("settings");
        schema.Settings.WriteTo(writer);
        writer.WriteStartArray("secrets");
        foreach (var field in schema.Secrets)
        {
            writer.WriteStartObject();
            writer.WriteString("id", field.Id);
            writer.WriteString("kind", field.Kind);
            writer.WriteBoolean("required", field.Required);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("uiHints");
        if (schema.UiHints is { } hints)
        {
            hints.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    /// <summary>The camelCase name of a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The name, e.g. <c>active</c>.</returns>
    public static string StateName(PackageState state) => state switch
    {
        PackageState.Found => "found",
        PackageState.Invalid => "invalid",
        PackageState.Verified => "verified",
        PackageState.Active => "active",
        PackageState.Disabled => "disabled",
        _ => "removed",
    };

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int or long or short or byte:
                writer.WriteNumberValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                writer.WriteNumberValue(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case IEnumerable<string> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
