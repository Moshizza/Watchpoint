using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchpoint.Api.Internal.Auth;
using Watchpoint.Api.Internal.Idempotency;
using Watchpoint.Api.Internal.Jobs;
using Watchpoint.Api.Internal.OpenApi;
using Watchpoint.Api.Internal.Pipeline;
using Watchpoint.Api.Internal.Platform;

namespace Watchpoint.Api.Contracts;

/// <summary>Registration and pipeline of the REST framework (spec API §4).</summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>The ASP.NET Core category logging raw request lines; the host keeps it at Warning (Serilog override).</summary>
    public const string HostingDiagnosticsCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>The ASP.NET Core routing category (logs request paths at Debug); kept at Warning.</summary>
    public const string RoutingCategory = "Microsoft.AspNetCore.Routing";

    /// <summary>Registers the framework services. Needs IDN (<c>ITokenService</c>, <c>ISessionRegistry</c>) and a <see cref="TimeProvider"/>.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration (sections <c>Api</c>, <c>Http:TrustedProxies</c>).</param>
    /// <param name="info">The node facts of <c>/system/info</c>.</param>
    /// <param name="isProduction">Whether the node runs in Production.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddWatchpointApi(this IServiceCollection services, IConfiguration configuration, SystemInfo info, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(info);
        var options = new ApiOptions { IsProduction = isProduction };
        configuration.GetSection("Api").Bind(options);
        options.TrustedProxies = configuration.GetSection("Http:TrustedProxies").Get<List<string>>() ?? [];
        services.AddSingleton(Options.Create(options));

        // ASP.NET Core's own request lines carry the raw path and query string (a hub access_token, API §4 row 4,
        // §10): only the framework's request log (2002) records requests.
        services.AddLogging(b => b
            .AddFilter(HostingDiagnosticsCategory, LogLevel.Warning)
            .AddFilter(RoutingCategory, LogLevel.Warning));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(info);
        services.TryAddSingleton<IPermissionEvaluator, FallbackPermissionEvaluator>();
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.AddHostedService<IdempotencyPurgeService>();
        services.TryAddSingleton<IJobRegistry, JobRegistry>();
        services.TryAddSingleton<ApiMetrics>();
        services.AddMetrics();
        services.ConfigureHttpJsonOptions(o => ApiJson.Apply(o.SerializerOptions));
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddRateLimiter(o => RateLimits.Configure(o, options.RateLimits));
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ["application/json", "application/problem+json", "image/svg+xml"];
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        if (options.TrustedProxies.Count > 0)
        {
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
                foreach (var cidr in options.TrustedProxies)
                {
                    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
                }
            });
        }

        if (isProduction)
        {
            services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));
        }
        else
        {
            OpenApiSetup.Add(services);
        }

        return services;
    }

    /// <summary>Adds the API middleware in the order of spec API §4 (authentication precedes rate limiting, which partitions by session).</summary>
    /// <param name="app">The application.</param>
    /// <returns>The application.</returns>
    public static IApplicationBuilder UseWatchpointApi(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<IOptions<ApiOptions>>().Value;
        if (options.TrustedProxies.Count > 0)
        {
            app.UseForwardedHeaders();
        }

        if (options.IsProduction)
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseMiddleware<ExceptionHandlerMiddleware>();
        app.UseRouting();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseResponseCompression();
        app.UseMiddleware<AuthenticationMiddleware>();
        app.UseRateLimiter();
        app.UseMiddleware<BodyLimitMiddleware>();
        return app;
    }

    /// <summary>Maps the API's own endpoints (system, jobs) and, outside Production, <c>/openapi/v1.json</c>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The route builder.</returns>
    public static IEndpointRouteBuilder MapWatchpointApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        SystemEndpoints.Map(endpoints);
        if (!endpoints.ServiceProvider.GetRequiredService<IOptions<ApiOptions>>().Value.IsProduction)
        {
            endpoints.MapOpenApi("/openapi/{documentName}.json");
        }

        return endpoints;
    }

    internal static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
