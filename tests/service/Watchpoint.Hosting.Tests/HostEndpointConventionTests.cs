using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var builder = WebApplication.CreateSlimBuilder();
        var data = Path.Combine(Path.GetTempPath(), "wp-conv-" + Guid.NewGuid().ToString("N"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = "localhost",
            ["Database:Databases:default:Name"] = "watchpoint",
            ["Database:Databases:default:Username"] = "watchpoint",
            ["Database:Databases:default:PasswordDev"] = "dev",
            ["Database:Databases:default:Tls"] = "Disable",
        });
        var node = new NodeSettings
        {
            Node = new NodeSection { Id = Guid.CreateVersion7(), Environment = NodeEnvironment.Development },
            Site = new SiteSection { Id = Guid.CreateVersion7() },
            Paths = new PathsSection { Data = data },
        };
        var paths = NodePaths.Resolve(node.Paths);
        builder.Services.AddSingleton(node);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(new NatsEndpoint("nats://127.0.0.1:4222", null));
        var context = new ModuleContext { Node = node, Configuration = builder.Configuration, Paths = paths, IsDevelopment = true };
        foreach (var module in ModuleCatalog.All)
        {
            module.ConfigureServices(builder.Services, context);
        }

        using var app = builder.Build();
        foreach (var module in ModuleCatalog.All)
        {
            module.MapEndpoints(app);
        }

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
        ApiConventions.Check(endpoints).ShouldBeEmpty();
        endpoints.OfType<RouteEndpoint>().Count(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)).ShouldBeGreaterThan(40);
    }
}
