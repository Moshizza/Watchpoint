using Microsoft.AspNetCore.Routing;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Host;
using Xunit;

namespace Watchpoint.Hosting.Tests;

/// <summary>Every module's endpoints, composed as the host composes them, pass the API conventions (API §5.1).</summary>
public sealed class HostEndpointConventionTests
{
    [Fact]
    public void API_U_030_Every_host_endpoint_has_one_guard_and_a_unique_operation_id()
    {
        using var app = Program.ComposeForInspection();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
        ApiConventions.Check(endpoints).ShouldBeEmpty();
        endpoints.OfType<RouteEndpoint>().Count(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)).ShouldBeGreaterThan(40);
    }
}
