extern alias OtelRuntime;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Systemd;
using Serilog;
using Watchpoint.Api.Contracts;
using Watchpoint.Host.Dev;
using Watchpoint.Host.Nats;
using Watchpoint.Hosting;
using Watchpoint.Hosting.Supervision;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Host;

/// <summary>
/// Main entry point for the Watchpoint host service executable.
/// </summary>
public static class Program
{
    /// <summary>Exit code for invalid configuration settings (EX_CONFIG).</summary>
    public const int ExitCodeConfig = 78;

    /// <summary>Exit code for elevated execution refusal in production (EX_CONFIG).</summary>
    public const int ExitCodeElevated = 78;

    /// <summary>Readiness reason while start-up has not passed <c>Edge</c> or the node is draining (HST §6.2).</summary>
    public const string StartupReason = "startup";

    /// <summary>Readiness reason when the <c>storage</c> health check is unhealthy (HST §6.2).</summary>
    public const string DatabaseReason = "database";
    private static readonly JsonSerializerOptions _indentedJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Configures the application configuration builder with all settings sources in precedence order.
    /// appsettings.json &lt; watchpoint.json &lt; WATCHPOINT__* env &lt; CLI
    /// </summary>
    public static void ConfigureConfiguration(
        IConfigurationBuilder configBuilder,
        string? watchpointJsonPath = null,
        IReadOnlyDictionary<string, string?>? envSource = null,
        string[]? args = null)
    {
        watchpointJsonPath ??= ResolveWatchpointJsonPath(args ?? []);

        configBuilder.AddJsonFile(watchpointJsonPath, optional: true, reloadOnChange: true);

        if (envSource is not null)
        {
            configBuilder.Add(new WatchpointEnvConfigurationSource(envSource));
        }
        else
        {
            configBuilder.AddEnvironmentVariables(prefix: "WATCHPOINT__");
        }

        if (args is not null)
        {
            configBuilder.AddCommandLine(args);
        }
    }

    /// <summary>Main entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Security check (spec HST §12, HST-U-014)
        var allowElevated = args.Contains("--allow-elevated", StringComparer.OrdinalIgnoreCase);
        var isPrivileged = IsPrivilegedProcess();

        // 1. Configuration sources: appsettings.json -> watchpoint.json -> WATCHPOINT__* env -> CLI
        ConfigureConfiguration(builder.Configuration, args: args);

        var watchpointJsonPath = ResolveWatchpointJsonPath(args);

        // Bind NodeSettings
        var settings = NodeSettings.Bind(builder.Configuration);

        // Until Serilog is configured, start-up problems are logged through a console bootstrap logger.
        using (var bootstrapLoggers = new Serilog.Extensions.Logging.SerilogLoggerFactory(
            new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateLogger(),
            dispose: true))
        {
            var bootstrap = bootstrapLoggers.CreateLogger("Watchpoint.Host");

            // Check privileged execution in Production (spec HST §12, HST-U-014)
            if (CheckElevation(isPrivileged, settings.Node.Environment, allowElevated, bootstrap) is { } elevatedExit)
            {
                return elevatedExit;
            }

            // Development is refused on a production install (spec HST §12, HST-U-048)
            if (CheckDevelopmentInstall(settings, RunsAsService(System.Environment.GetEnvironmentVariable), NodePaths.Resolve().Data, bootstrap) is { } devExit)
            {
                return devExit;
            }

            // Validate settings (spec HST §4, HST-U-003)
            var validationResults = settings.Validate(new ValidationContext(settings)).ToList();
            if (validationResults.Count > 0)
            {
                var problems = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                HostingLog.LogInvalidNodeSettings(bootstrap, problems);
                return ExitCodeConfig;
            }

            // Node and site ID persistence (spec HST §4, HST-U-004, HST-U-046)
            EnsureNodeId(settings, watchpointJsonPath, bootstrap);
            EnsureSiteId(settings, watchpointJsonPath, bootstrap);
        }

        // Resolve paths and ensure directories
        var paths = NodePaths.Resolve(settings.Paths);
        paths.EnsureDirectories();

        // Configure Serilog (spec HST §6.3, HST-U-012, HST-U-013)
        var isContainer = System.Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var logFilePath = Path.Combine(paths.Logs, "watchpoint-.log");

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(settings.Logging.MinimumLevel, true, out var minLevel) ? minLevel : Serilog.Events.LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("NodeId", settings.Node.Id)
            .Enrich.WithProperty("NodeName", settings.Node.Name)
            .Enrich.With<ModuleEnricher>()
            .Enrich.With<RedactionEnricher>();

        // ASP.NET Core's raw request lines carry query strings (hub access_token): only API event 2002 logs requests (API §10).
        loggerConfig.MinimumLevel.Override(Api.Contracts.ApiServiceCollectionExtensions.HostingDiagnosticsCategory, Serilog.Events.LogEventLevel.Warning);
        loggerConfig.MinimumLevel.Override(Api.Contracts.ApiServiceCollectionExtensions.RoutingCategory, Serilog.Events.LogEventLevel.Warning);

        foreach (var (category, levelStr) in settings.Logging.Overrides)
        {
            if (Enum.TryParse<Serilog.Events.LogEventLevel>(levelStr, true, out var overrideLevel))
            {
                loggerConfig.MinimumLevel.Override(category, overrideLevel);
            }
        }

        if (isContainer)
        {
            loggerConfig.WriteTo.Async(a => a.Console(new Serilog.Formatting.Json.JsonFormatter()));
        }
        else
        {
            loggerConfig.WriteTo.Async(a => a.Console(formatProvider: CultureInfo.InvariantCulture));
        }

        loggerConfig.WriteTo.Async(a => a.File(
            new Serilog.Formatting.Json.JsonFormatter(),
            logFilePath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: 100 * 1024 * 1024,
            retainedFileCountLimit: settings.Logging.RetainedFileCount));

        builder.Host.UseSerilog(loggerConfig.CreateLogger());

        // Hosting extensions: Windows Service & Systemd
        builder.Host.UseWindowsService(options => options.ServiceName = "Watchpoint");
        // systemd: no UseSystemd(). Its SystemdLifetime sends READY=1 when the host has started,
        // which is before Edge now that start-up runs after Kestrel listens (HST §5.3).
        // StartupOrchestrator sends READY=1 after Edge and STOPPING=1 on shutdown; SIGTERM is
        // handled by the default console lifetime; Serilog replaces the systemd console format.
        // SystemdWatchdog sends WATCHDOG=1 every WATCHDOG_USEC / 2 when the unit sets WatchdogSec.

        // Kestrel configuration
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

            // Bind Http:Urls
            foreach (var urlStr in settings.Http.Urls)
            {
                if (Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                {
                    options.Listen(
                        uri.Host == "0.0.0.0" ? IPAddress.Any : (IPAddress.TryParse(uri.Host, out var ip) ? ip : IPAddress.Loopback),
                        uri.Port,
                        listenOptions =>
                        {
                            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                            if (uri.Scheme == "https")
                            {
                                listenOptions.UseHttps();
                            }
                        });
                }
            }

            // Bind Management:Urls
            foreach (var urlStr in settings.Management.Urls)
            {
                if (Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
                {
                    options.Listen(
                        uri.Host == "0.0.0.0" ? IPAddress.Any : (IPAddress.TryParse(uri.Host, out var ip) ? ip : IPAddress.Loopback),
                        uri.Port,
                        listenOptions =>
                        {
                            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
                        });
                }
            }
        });

        // OpenTelemetry setup (spec HST §6.3)
        var otel = builder.Services.AddOpenTelemetry();
        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter("Watchpoint.*");
            OpenTelemetry.Metrics.AspNetCoreInstrumentationMeterProviderBuilderExtensions.AddAspNetCoreInstrumentation(metrics);
            OtelRuntime::OpenTelemetry.Metrics.MeterProviderBuilderExtensions.AddRuntimeInstrumentation(metrics);
            OpenTelemetry.Metrics.PrometheusExporterMeterProviderBuilderExtensions.AddPrometheusExporter(metrics);
        });

        if (!string.IsNullOrEmpty(settings.Telemetry.Otlp.Endpoint) &&
            Uri.TryCreate(settings.Telemetry.Otlp.Endpoint, UriKind.Absolute, out var otlpUri))
        {
            otel.WithTracing(tracing =>
            {
                OpenTelemetry.Trace.AspNetCoreInstrumentationTracerProviderBuilderExtensions.AddAspNetCoreInstrumentation(tracing);
                OpenTelemetry.Trace.OtlpTraceExporterHelperExtensions.AddOtlpExporter(tracing, o => o.Endpoint = otlpUri);
            });
        }

        // Register core hosting services
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(settings));
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<INodeReadiness, NodeReadiness>();

        // Process supervisor and NATS (spec HST §7): bundled server started by the host, or an external one.
        builder.Services.AddProcessSupervisor(paths);
        builder.Services.AddSingleton<NatsProcessSpecFactory>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<NatsProcessSpecFactory>().Endpoint());
        if (settings.Nats.Mode == NatsMode.Bundled)
        {
            builder.Services.AddStartupTask<BundledNatsStartupTask>();
        }

        // Development seed (spec HST §9): refused outside Development by the task itself (HST1140).
        if (!string.IsNullOrWhiteSpace(settings.Dev.SeedFile))
        {
            builder.Services.AddSingleton<IDevSeedStep, SiteDevSeedStep>();
            builder.Services.AddSingleton<IDevSeedStep, LocationsDevSeedStep>();
            builder.Services.AddSingleton<IDevSeedStep, TagsDevSeedStep>();
            builder.Services.AddSingleton<IDevSeedStep, UsersDevSeedStep>();
            builder.Services.AddSingleton<IDevSeedStep, ConnectionsDevSeedStep>();
            builder.Services.AddStartupTask<DevSeedStartupTask>();
        }

        builder.Services.AddHealthChecks();
        builder.Services.AddHostedService<StartupOrchestrator>();
        builder.Services.TryAddSingleton<ISystemdNotifier, SystemdNotifier>();
        builder.Services.AddHostedService<SystemdWatchdog>();

        // Configure Modules
        var moduleContext = new ModuleContext
        {
            Node = settings,
            Configuration = builder.Configuration,
            Paths = paths,
            IsDevelopment = settings.Node.Environment == NodeEnvironment.Development
        };

        try
        {
            foreach (var module in ModuleCatalog.All)
            {
                module.ConfigureServices(builder.Services, moduleContext);
            }
        }
        catch (InvalidSettingsException ex)
        {
            // A module refused its settings (spec HST §4): same outcome as invalid node settings, exit code 78.
            using var loggers = new Serilog.Extensions.Logging.SerilogLoggerFactory(
                new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateLogger(),
                dispose: true);
            var problems = ex.Module + ": " + ex.Problems;
            var settingsLogger = loggers.CreateLogger("Watchpoint.Host");
            HostingLog.LogInvalidNodeSettings(settingsLogger, problems);
            return ExitCodeConfig;
        }

        var app = builder.Build();

        // Non-health endpoint traffic barrier before Edge (spec HST §6.1):
        // Until Edge phase is reached, non-health endpoints return 503 Service Unavailable
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var readiness = context.RequestServices.GetRequiredService<INodeReadiness>();
            if (!readiness.IsReady)
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            await next();
        });

        // REST pipeline (spec API §4), after the readiness barrier
        app.UseWatchpointApi();

        // Log node starting (HST1102)
        var loggerFactory = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Watchpoint.Host");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var startingLevel = NodeStartingLevel(settings.Node.Environment);
        HostingLog.LogNodeStarting(logger, startingLevel, settings.Node.Id, settings.Node.Name, version, settings.Node.Environment);

        // Management URLs set for routing filter
        var managementPorts = new HashSet<int>();
        foreach (var u in settings.Management.Urls)
        {
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
            {
                managementPorts.Add(uri.Port);
            }
        }

        // Map Endpoints for Modules
        foreach (var module in ModuleCatalog.All)
        {
            module.MapEndpoints(app);
        }

        // Map Health and Prometheus endpoints
        MapHostEndpoints(app, managementPorts);

        // Every /api endpoint declares its guard and operation id (spec API §5.1, API-U-030)
        ApiConventions.Validate(app);

        try
        {
            await app.RunAsync();
        }
        catch (Exception) when (System.Environment.ExitCode != 0)
        {
            return System.Environment.ExitCode;
        }

        return System.Environment.ExitCode;
    }

    private static void MapHostEndpoints(WebApplication app, HashSet<int> managementPorts)
    {
        // /health/live: Always 200 unless shutting down
        app.MapGet("/health/live", (INodeReadiness readiness) =>
        {
            return Microsoft.AspNetCore.Http.Results.Ok(new { status = "live" });
        });

        // /health/ready: 200 {"status":"ready"} or 503 {"status":"notReady","reasons":[...]}
        app.MapGet("/health/ready", async (INodeReadiness readiness, HealthCheckService healthCheckService, CancellationToken ct) =>
        {
            var report = await healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"), ct);
            var (statusCode, reasons) = EvaluateReadiness(readiness.IsReady, report);
            return statusCode == StatusCodes.Status200OK
                ? Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready" })
                : Microsoft.AspNetCore.Http.Results.Json(new { status = "notReady", reasons }, statusCode: statusCode);
        });

        // /health/details: management port only
        app.MapGet("/health/details", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            if (!managementPorts.Contains(context.Connection.LocalPort))
            {
                return Microsoft.AspNetCore.Http.Results.NotFound();
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                status = "ok",
                checks = Array.Empty<object>()
            });
        });

        // /metrics: OpenTelemetry Prometheus exporter, management port only (HST-I-003)
        app.UseOpenTelemetryPrometheusScrapingEndpoint(context =>
            context.Request.Path == "/metrics" && managementPorts.Contains(context.Connection.LocalPort));

        app.MapGet("/metrics", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            // If request reached here (not handled by branched prometheus pipeline because port mismatch), return 404
            return Microsoft.AspNetCore.Http.Results.NotFound();
        });
    }

    /// <summary>
    /// Composes every module as the host does (Development, nothing started, no database or NATS contacted) and maps their
    /// endpoints: for the OpenAPI export (<c>tools/export-openapi</c>, API §5.7) and the endpoint convention tests.
    /// </summary>
    /// <returns>The application, not started; the caller disposes it.</returns>
    public static WebApplication ComposeForInspection()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = "localhost",
            ["Database:Databases:default:Name"] = "watchpoint",
            ["Database:Databases:default:Username"] = "watchpoint",
            ["Database:Databases:default:PasswordDev"] = "inspection",
            ["Database:Databases:default:Tls"] = "Disable",
        });
        var node = new NodeSettings
        {
            Node = new NodeSection { Id = Guid.CreateVersion7(), Name = "inspection", Environment = NodeEnvironment.Development },
            Site = new SiteSection { Id = Guid.CreateVersion7() },
            Paths = new PathsSection { Data = Path.Combine(Path.GetTempPath(), "watchpoint-inspection") },
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

        var app = builder.Build();
        foreach (var module in ModuleCatalog.All)
        {
            module.MapEndpoints(app);
        }

        return app;
    }

    /// <summary>Path of <c>watchpoint.json</c>: <c>--config</c> / <c>--config=</c> when given, else <c>&lt;Data&gt;/watchpoint.json</c>.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The path.</returns>
    public static string ResolveWatchpointJsonPath(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var path = Path.Combine(NodePaths.Resolve().Data, "watchpoint.json");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                path = args[i]["--config=".Length..];
            }
            else if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                path = args[i + 1];
            }
        }

        return path;
    }

    /// <summary>
    /// Refuses a privileged process in Production unless <c>--allow-elevated</c> is passed (spec HST §12): logs
    /// <c>HST1103</c> and returns exit code 78, or logs the warning <c>HST1104</c> and returns <see langword="null"/>.
    /// </summary>
    /// <param name="isPrivileged">Whether the process runs as root/Administrator.</param>
    /// <param name="environment">The node environment.</param>
    /// <param name="allowElevated">Whether <c>--allow-elevated</c> was passed.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The exit code to stop with, or <see langword="null"/> to continue.</returns>
    public static int? CheckElevation(bool isPrivileged, NodeEnvironment environment, bool allowElevated, Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!isPrivileged || environment != NodeEnvironment.Production)
        {
            return null;
        }

        if (!allowElevated)
        {
            HostingLog.LogElevatedRefused(logger);
            return ExitCodeElevated;
        }

        HostingLog.LogElevatedAllowed(logger);
        return null;
    }

    /// <summary>
    /// Refuses <c>Node:Environment = Development</c> on a production install (spec HST §12, HST-U-048): when the host runs
    /// as a service (Windows service, or systemd: <c>NOTIFY_SOCKET</c> / <c>INVOCATION_ID</c>) or <c>Paths:Data</c> is the
    /// OS default, logs <c>HST1101</c> and returns exit code 78; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="settings">The node settings.</param>
    /// <param name="runsAsService">Whether the process runs as a service.</param>
    /// <param name="defaultDataPath">The production default of <c>Paths:Data</c> on this OS.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The exit code to stop with, or <see langword="null"/> to continue.</returns>
    public static int? CheckDevelopmentInstall(NodeSettings settings, bool runsAsService, string defaultDataPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            return null;
        }

        string? problem = null;
        if (runsAsService)
        {
            problem = "Node:Environment=Development is refused when running as a service (production install)";
        }
        else
        {
            var data = NodePaths.Resolve(settings.Paths).Data;
            if (string.Equals(Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(defaultDataPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                problem = "Node:Environment=Development is refused with the production data path " + defaultDataPath + " (set Paths:Data)";
            }
        }

        if (problem is null)
        {
            return null;
        }

        HostingLog.LogInvalidNodeSettings(logger, problem);
        return ExitCodeConfig;
    }

    /// <summary>Whether the process runs as a service: Windows service, or systemd (<c>NOTIFY_SOCKET</c> or <c>INVOCATION_ID</c> set).</summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns><see langword="true"/> for a service.</returns>
    public static bool RunsAsService(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
            || !string.IsNullOrEmpty(environment("NOTIFY_SOCKET"))
            || !string.IsNullOrEmpty(environment("INVOCATION_ID"));
    }

    /// <summary>Level of event 1102: Warning in Development (every relaxation is public, HST §12), else Information.</summary>
    /// <param name="environment">The node environment.</param>
    /// <returns>The level.</returns>
    public static Microsoft.Extensions.Logging.LogLevel NodeStartingLevel(NodeEnvironment environment) =>
        environment == NodeEnvironment.Development ? Microsoft.Extensions.Logging.LogLevel.Warning : Microsoft.Extensions.Logging.LogLevel.Information;

    /// <summary>
    /// Generates a v7 <c>Node:Id</c> when none is set and writes it to <paramref name="watchpointJsonPath"/> (spec HST §4);
    /// a write failure logs <c>HST1105</c> and keeps the generated id for this run.
    /// </summary>
    /// <param name="settings">The node settings (updated in place).</param>
    /// <param name="watchpointJsonPath">The <c>watchpoint.json</c> path.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The node id.</returns>
    public static Guid EnsureNodeId(NodeSettings settings, string watchpointJsonPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (settings.Node.Id != Guid.Empty)
        {
            return settings.Node.Id;
        }

        settings.Node.Id = Guid.CreateVersion7();
        try
        {
            PersistId(watchpointJsonPath, "Node", settings.Node.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            HostingLog.LogNodeIdPersistFailed(logger, watchpointJsonPath, ex);
        }

        return settings.Node.Id;
    }

    /// <summary>
    /// Generates a v7 <c>Site:Id</c> when none is set and writes it to <paramref name="watchpointJsonPath"/> (spec HST §4,
    /// IDN §6.2: the id forms the token issuer, so it must be stable across restarts and shared by the nodes of a site);
    /// a write failure logs <c>HST1105</c> and keeps the generated id for this run.
    /// </summary>
    /// <param name="settings">The node settings (updated in place).</param>
    /// <param name="watchpointJsonPath">The <c>watchpoint.json</c> path.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The site id.</returns>
    public static Guid EnsureSiteId(NodeSettings settings, string watchpointJsonPath, Microsoft.Extensions.Logging.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (settings.Site.Id != Guid.Empty)
        {
            return settings.Site.Id;
        }

        settings.Site.Id = Guid.CreateVersion7();
        try
        {
            PersistId(watchpointJsonPath, "Site", settings.Site.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            HostingLog.LogNodeIdPersistFailed(logger, watchpointJsonPath, ex);
        }

        return settings.Site.Id;
    }

    /// <summary>
    /// Readiness response (spec HST §6.2): 200 when start-up passed <c>Edge</c>, the node is not draining and every
    /// <c>ready</c> check is healthy; else 503 with the reasons (<c>storage</c> check → <c>database</c>, start-up →
    /// <c>startup</c>, other checks by name).
    /// </summary>
    /// <param name="isReady">Whether start-up passed <c>Edge</c> and the node is not draining.</param>
    /// <param name="report">The report of the checks tagged <c>ready</c>.</param>
    /// <returns>The status code and reasons.</returns>
    public static (int StatusCode, IReadOnlyList<string> Reasons) EvaluateReadiness(bool isReady, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var reasons = new List<string>();
        foreach (var (name, entry) in report.Entries)
        {
            if (entry.Status != HealthStatus.Healthy)
            {
                var reason = string.Equals(name, StorageServiceCollectionExtensions.HealthCheckName, StringComparison.Ordinal)
                    ? DatabaseReason
                    : name;
                if (!reasons.Contains(reason))
                {
                    reasons.Add(reason);
                }
            }
        }

        if (!isReady)
        {
            reasons.Add(StartupReason);
        }

        return (reasons.Count == 0 ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, reasons);
    }

    private static void PersistId(string path, string section, Guid id)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var content = File.ReadAllText(path);
            root = JsonSerializer.Deserialize<JsonObject>(content) ?? new JsonObject();
        }
        else
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            root = new JsonObject();
        }

        if (root[section] is not JsonObject sectionObj)
        {
            sectionObj = new JsonObject();
            root[section] = sectionObj;
        }

        sectionObj["Id"] = id.ToString();

        File.WriteAllText(path, JsonSerializer.Serialize(root, _indentedJsonOptions));
    }

    private static bool IsPrivilegedProcess()
    {
        try
        {
            return System.Environment.IsPrivilegedProcess;
        }
        catch
        {
            return false;
        }
    }

    private sealed class WatchpointEnvConfigurationSource(IReadOnlyDictionary<string, string?> env) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) =>
            new WatchpointEnvConfigurationProvider(env);
    }

    private sealed class WatchpointEnvConfigurationProvider(IReadOnlyDictionary<string, string?> env) : ConfigurationProvider
    {
        public override void Load()
        {
            const string prefix = "WATCHPOINT__";
            foreach (var (key, value) in env)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedKey = key[prefix.Length..].Replace("__", ":");
                    Data[normalizedKey] = value;
                }
            }
        }
    }
}
