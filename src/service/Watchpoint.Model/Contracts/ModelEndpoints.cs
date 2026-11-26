using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>The model endpoints (MOD §7) on the API framework.</summary>
public static class ModelEndpoints
{
    /// <summary>Headers of a symbol (MOD §7): a symbol is untrusted package content.</summary>
    public const string SymbolContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";

    private static readonly JsonSerializerOptions _json = CreateJson();

    /// <summary>Maps the model endpoints.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var types = endpoints.MapGroup("/api/v1").WithTags("Types").RequireAuthentication();
        types.MapGet("/types", ListTypes).WithName("Types_ListTypes").WithSummary("Type summaries (role, category, capability, source filters)");
        types.MapGet("/types/{id}/versions/{version}", GetTypeDefinition).WithName("Types_GetType").WithSummary("Full flattened type definition");
        types.MapGet("/capabilities/{id}/versions/{version}", GetCapability).WithName("Types_GetCapability").WithSummary("Capability definition");
        types.MapGet("/symbols/{symbolId}/{state}.svg", GetSymbol).WithName("Types_GetSymbol").WithSummary("Symbol SVG for an overall state");
        endpoints.MapGet("/api/v1/i18n/types/{culture}", GetBundle).WithTags("Types").AllowAnonymous().WithName("Types_GetTranslations")
            .WithSummary("Merged translation bundle (ETag, revalidate)");
        return endpoints;
    }

    private static IResult ListTypes(string? role, string? category, string? capability, string? source, [FromServices] ITypeCatalog catalog)
    {
        var errors = new List<FieldError>();
        var query = new TypeQuery(
            Enum<TypeRole>(role, "/query/role", errors),
            Enum<Category>(category, "/query/category", errors),
            string.IsNullOrEmpty(source) ? null : source,
            string.IsNullOrEmpty(capability) ? null : CapabilityId.Parse(capability).TryGetValue(out var c, out _) ? c : Invalid<CapabilityId>("/query/capability", errors));
        if (errors.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(errors));
        }

        var items = catalog.List(query).Select(t => new TypeSummaryDto(new TypeRefDto(t.Ref.Id.Value, t.Ref.Version.ToString()), Camel(t.Role), Camel(t.Category),
            t.Source, t.Display.Icon, [.. t.Capabilities.Keys.Select(k => k.Value)])).ToList();
        return Results.Json(new PageDto<TypeSummaryDto>(items, null), _json);
    }

    private static IResult GetTypeDefinition(string id, string version, [FromServices] ITypeCatalog catalog)
    {
        if (!TypeId.Parse(id).TryGetValue(out var typeId, out var error) || !SemVersion.Parse(version).TryGetValue(out var semVersion, out error))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/path", error.Code)]));
        }

        return HttpResults.From(catalog.Get(new TypeRef(typeId, semVersion)), t => Results.Json(t, _json));
    }

    private static IResult GetCapability(string id, string version, [FromServices] ITypeCatalog catalog)
    {
        if (!CapabilityId.Parse(id).TryGetValue(out var capabilityId, out var error) || !SemVersion.Parse(version).TryGetValue(out var semVersion, out error))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/path", error.Code)]));
        }

        return HttpResults.From(catalog.GetCapability(capabilityId, semVersion), c => Results.Json(c, _json));
    }

    private static IResult GetSymbol(string symbolId, string state, HttpContext http, [FromServices] ITypeResources resources)
    {
        if (!System.Enum.TryParse<OverallState>(state, ignoreCase: true, out var overall) || !System.Enum.IsDefined(overall) || int.TryParse(state, out _))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/path/state", CommonErrorCodes.ValueNotAllowed)]));
        }

        return HttpResults.From(resources.GetSymbol(symbolId, overall), svg =>
        {
            http.Response.Headers.ContentSecurityPolicy = SymbolContentSecurityPolicy;
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(svg, "image/svg+xml");
        });
    }

    private static IResult GetBundle(string culture, HttpContext http, [FromServices] ITypeResources resources)
    {
        if (!SupportedCultures.IsSupported(culture))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/path/culture", CommonErrorCodes.ValueNotAllowed, ("value", culture))]));
        }

        var bundle = resources.GetTranslationBundle(culture, out var etag);
        var tag = "\"" + etag.Trim('"') + "\"";
        http.Response.Headers.ETag = tag;
        http.Response.Headers.CacheControl = "no-cache";
        if (http.Request.Headers.IfNoneMatch.Any(v => string.Equals(v?.Trim(), tag, StringComparison.Ordinal) || v == "*"))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(bundle, "application/json");
    }

    private static T? Enum<T>(string? text, string path, List<FieldError> errors)
        where T : struct, System.Enum
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value) && !int.TryParse(text, out _))
        {
            return value;
        }

        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueNotAllowed, ("value", text)));
        return null;
    }

    private static T? Invalid<T>(string path, List<FieldError> errors)
        where T : struct
    {
        errors.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat));
        return null;
    }

    private static string Camel<T>(T value)
        where T : struct, System.Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(ApiJson.Options);
        options.Converters.Add(new SemVersionConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class SemVersionConverter : System.Text.Json.Serialization.JsonConverter<SemVersion>
    {
        public override SemVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            SemVersion.TryParse(reader.GetString(), out var v) ? v : throw new JsonException("Invalid semantic version.");

        public override void Write(Utf8JsonWriter writer, SemVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    internal sealed record TypeRefDto(string Id, string Version);

    internal sealed record TypeSummaryDto(TypeRefDto Ref, string Role, string Category, string Source, string Icon, IReadOnlyList<string> Capabilities);
}
