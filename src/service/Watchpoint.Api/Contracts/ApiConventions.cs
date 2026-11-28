using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace Watchpoint.Api.Contracts;

/// <summary>
/// Start-up check of the endpoint conventions (spec API §5.1, API-U-005, API-U-029, API-U-030): every endpoint under
/// <c>/api/</c> has exactly one of <c>RequirePermission</c>, <c>RequireAuthentication</c> or <c>AllowAnonymous</c>, and
/// a unique operation id <c>&lt;Tag&gt;_&lt;Verb&gt;&lt;Noun&gt;</c> (<c>WithName</c>).
/// </summary>
public static partial class ApiConventions
{
    [GeneratedRegex("^[A-Z][A-Za-z]*_[A-Z][a-z]+[A-Za-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationIdPattern();

    /// <summary>The convention violations of <paramref name="endpoints"/> (empty when none).</summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <returns>One message per violation.</returns>
    public static IReadOnlyList<string> Check(IEnumerable<Microsoft.AspNetCore.Http.Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var problems = new List<string>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!route.StartsWith("/api/", StringComparison.Ordinal))
            {
                continue;
            }

            var name = endpoint.DisplayName ?? route;
            var guards = (endpoint.Metadata.GetMetadata<PermissionRequirement>() is null ? 0 : 1)
                + (endpoint.Metadata.GetMetadata<AuthenticationRequired>() is null ? 0 : 1)
                + (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null ? 0 : 1);
            if (guards != 1)
            {
                problems.Add($"{name}: needs exactly one of RequirePermission, RequireAuthentication or AllowAnonymous (has {guards}).");
            }

            var id = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            if (id is null || !OperationIdPattern().IsMatch(id))
            {
                problems.Add($"{name}: operation id '{id}' does not follow <Tag>_<Verb><Noun> (WithName).");
            }
            else if (!ids.TryAdd(id, name))
            {
                problems.Add($"{name}: operation id '{id}' already used by {ids[id]}.");
            }
        }

        return problems;
    }

    /// <summary>Throws when <see cref="Check"/> finds violations (called by the host before it starts listening).</summary>
    /// <param name="endpoints">The route builder holding the mapped endpoints.</param>
    public static void Validate(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var problems = Check(endpoints.DataSources.SelectMany(d => d.Endpoints));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("API endpoint conventions violated (API §5.1):" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }
}
