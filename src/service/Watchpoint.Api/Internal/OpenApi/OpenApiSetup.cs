using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Watchpoint.Api.Contracts;

namespace Watchpoint.Api.Internal.OpenApi;

/// <summary>OpenAPI document <c>v1</c> (spec API §5.7): operation ids from <c>WithName</c>, <c>x-wp-permission</c>, <c>x-wp-config</c>, problem responses.</summary>
internal static class OpenApiSetup
{
    public const string DocumentName = "v1";

    public static void Add(IServiceCollection services) => services.AddOpenApi(DocumentName, options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo { Title = "Watchpoint API", Version = SystemEndpointsVersion };
            document.Servers = [];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
            if (metadata.OfType<PermissionRequirement>().FirstOrDefault() is { } permission)
            {
                operation.Extensions["x-wp-permission"] = new JsonNodeExtension(JsonValue.Create(permission.Permission.Value));
            }

            if (metadata.OfType<ConfigurationEndpointMarker>().Any())
            {
                operation.Extensions["x-wp-config"] = new JsonNodeExtension(JsonValue.Create(true));
            }

            operation.Responses ??= [];
            foreach (var status in ProblemStatuses(metadata))
            {
                operation.Responses.TryAdd(status, new OpenApiResponse
                {
                    Description = "Problem details (RFC 9457)",
                    Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() },
                });
            }

            return Task.CompletedTask;
        });
    });

    private const string SystemEndpointsVersion = "1.0";

    private static IEnumerable<string> ProblemStatuses(IList<object> metadata)
    {
        if (!metadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any())
        {
            yield return "401";
        }

        if (metadata.OfType<PermissionRequirement>().Any() || metadata.OfType<ConfigurationEndpointMarker>().Any())
        {
            yield return "403";
        }

        if (metadata.OfType<VersionedMarker>().Any())
        {
            yield return "412";
            yield return "428";
        }

        yield return "400";
        yield return "429";
        yield return "500";
    }
}
