using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Watchpoint.Api.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Contracts;

/// <summary>The package endpoints owned by the integration runtime (spec IRT §10).</summary>
public static class PackageEndpoints
{

    /// <summary>Permission of install, enable and disable (IRT §10).</summary>
    public const string InstallPermission = "config.integrations.install";

    /// <summary>The package upload limit (API §4.1): 500 MB.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Maps <c>/api/v1/packages</c>: reads need <c>config.integrations.view</c>; install, enable and disable need
    /// <c>config.integrations.install</c> and the desktop client.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/v1/packages").WithTags("Packages").RequirePermission(ConnectionEndpoints.ViewPermission);

        group.MapGet(string.Empty, (IPackageCatalog catalog) => Json(StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var package in catalog.List())
            {
                PackageJson.Write(writer, package);
            }

            writer.WriteEndArray();
            writer.WriteNull("nextCursor");
            writer.WriteEndObject();
        })).WithName("Packages_ListPackages");

        group.MapGet("{id}/versions/{version}", (string id, string version, PackageCatalog catalog) =>
            Parse(id, version, out var packageId, out var semVersion) is { } invalid ? invalid
            : catalog.Get(packageId, semVersion).TryGetValue(out var package, out var error)
                ? Json(StatusCodes.Status200OK, writer => WriteDetails(writer, package, catalog.ListDocumentation(packageId, semVersion)))
                : Problem(error)).WithName("Packages_GetPackageVersion");

        group.MapGet("{id}/versions/{version}/docs/{**path}", (string id, string version, string path, PackageCatalog catalog) =>
            Parse(id, version, out var packageId, out var semVersion) is { } invalid ? invalid
            : catalog.GetDocumentationFile(packageId, semVersion, path).TryGetValue(out var file, out var error)
                ? Results.File(file, "text/markdown; charset=utf-8")
                : Problem(error)).WithName("Packages_GetPackageDocument");

        group.MapGet("{id}/versions/{version}/connectors/{serverType}/settings-schema", (string id, string version, string serverType, IPackageCatalog catalog) =>
            Parse(id, version, out var packageId, out var semVersion) is { } invalid ? invalid
            : catalog.GetSettingsSchema(packageId, semVersion, serverType).TryGetValue(out var schema, out var error)
                ? Json(StatusCodes.Status200OK, writer => PackageJson.Write(writer, schema))
                : Problem(error)).WithName("Packages_GetSettingsSchema");

        // Install, enable, disable (IRT §10): config.integrations.install, desktop client only; upload ≤ 500 MB (API §4.1).
        var changes = endpoints.MapGroup("/api/v1/packages").WithTags("Packages").RequirePermission(InstallPermission).ConfigurationEndpoint();
        changes.MapPost(string.Empty, InstallAsync).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes + (1024 * 1024)))
            .DisableAntiforgery().WithName("Packages_InstallPackage");
        changes.MapPost("{id}/versions/{version}/enable", (string id, string version, IPackageCatalog catalog, CancellationToken ct) => SetEnabledAsync(catalog, id, version, true, ct))
            .WithName("Packages_EnablePackageVersion");
        changes.MapPost("{id}/versions/{version}/disable", (string id, string version, IPackageCatalog catalog, CancellationToken ct) => SetEnabledAsync(catalog, id, version, false, ct))
            .WithName("Packages_DisablePackageVersion");
        return endpoints;
    }

    private static async Task<IResult> InstallAsync(HttpRequest request, IPackageCatalog catalog, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return Problem(Error.Create(ErrorKind.Validation, "common.value.invalid_format", ("expected", "multipart/form-data with one .wpkg file")));
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxUploadBytes + (1024 * 1024);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes, ValueCountLimit = 16 }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Problem(IntegrationErrors.UnsafeArchive("tooLarge"));
        }

        if (form.Files.Count != 1)
        {
            return Problem(Error.Create(ErrorKind.Validation, "common.value.invalid_format", ("expected", "multipart/form-data with one .wpkg file")));
        }

        var file = form.Files[0];
        Result<PackageInfo> installed;
        var stream = file.OpenReadStream();
        await using (stream.ConfigureAwait(false))
        {
            // No authenticated user before the API framework (T1.19): the actor is the empty id.
            installed = await catalog.InstallAsync(stream, file.FileName, default, ct).ConfigureAwait(false);
        }

        if (!installed.TryGetValue(out var package, out var error))
        {
            return Problem(error);
        }

        // An invalid package is a business refusal: 422 with the verification problems.
        var status = package.State == PackageState.Invalid ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status201Created;
        var location = status == StatusCodes.Status201Created ? $"/api/v1/packages/{package.Id.Value}/versions/{package.Version}" : null;
        return Json(status, writer => PackageJson.Write(writer, package), location);
    }

    private static async Task<IResult> SetEnabledAsync(IPackageCatalog catalog, string id, string version, bool enabled, CancellationToken ct)
    {
        if (Parse(id, version, out var packageId, out var semVersion) is { } invalid)
        {
            return invalid;
        }

        var result = await catalog.SetEnabledAsync(packageId, semVersion, enabled, default, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? Json(StatusCodes.Status200OK, writer => PackageJson.Write(writer, catalog.Get(packageId, semVersion).Value))
            : Problem(result.Error);
    }

    private static IResult? Parse(string id, string version, out PackageId packageId, out SemVersion semVersion)
    {
        semVersion = null!;
        if (!PackageId.Parse(id).TryGetValue(out packageId, out _) || !SemVersion.TryParse(version, out var parsed))
        {
            return Problem(Error.NotFound(IntegrationErrors.PackageNotFound, ("package", id), ("version", version)));
        }

        semVersion = parsed;
        return null;
    }

    private static void WriteDetails(Utf8JsonWriter writer, PackageInfo package, IReadOnlyList<string> docs)
    {
        // The package fields, plus the documentation index.
        using var buffer = new MemoryStream();
        using (var inner = new Utf8JsonWriter(buffer))
        {
            PackageJson.Write(inner, package);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        writer.WriteStartObject();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteStartArray("docs");
        foreach (var doc in docs)
        {
            writer.WriteStringValue(doc);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IResult Problem(Error error) => EndpointResults.Problem(error);

    private static IResult Json(int status, Action<Utf8JsonWriter> write, string? location = null, string contentType = "application/json") =>
        EndpointResults.Json(status, write, location, contentType);
}
