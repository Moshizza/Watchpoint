extern alias OtelRuntime;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Watchpoint.Hosting;

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

    /// <summary>Exit code for temporary failure when required dependency stayed unreachable (EX_TEMPFAIL).</summary>
    public const int ExitCodeTempFail = 75;

    private static readonly string[] StartupReasons = ["startup"];
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

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
        var pathsEarly = NodePaths.Resolve();
        watchpointJsonPath ??= Path.Combine(pathsEarly.Data, "watchpoint.json");

        // Check for --config custom argument
        if (args is not null)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    watchpointJsonPath = args[i]["--config=".Length..];
                }
                else if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    watchpointJsonPath = args[i + 1];
                }
            }
        }

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

        var pathsEarly = NodePaths.Resolve();
        var watchpointJsonPath = Path.Combine(pathsEarly.Data, "watchpoint.json");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                watchpointJsonPath = args[i]["--config=".Length..];
            }
            else if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                watchpointJsonPath = args[i + 1];
            }
        }

        // Bind NodeSettings
        var settings = new NodeSettings();
        builder.Configuration.Bind(settings);

        // Check privileged execution in Production
        if (isPrivileged && settings.Node.Environment == NodeEnvironment.Production)
        {
            if (!allowElevated)
            {
                Console.Error.WriteLine("CRITICAL: Watchpoint refusing to start as privileged process (root/Administrator) in Production without --allow-elevated.");
                return ExitCodeElevated;
            }

            Console.WriteLine("WARNING: Running as privileged process in Production (--allow-elevated passed).");
        }

        // Validate settings (spec HST §4, HST-U-003)
        var validationContext = new ValidationContext(settings);
        var validationResults = settings.Validate(validationContext).ToList();
        if (validationResults.Count > 0)
        {
            var problems = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
            Console.Error.WriteLine($"CRITICAL [HST1101]: Invalid node settings: {problems}");
            return ExitCodeConfig;
        }

        // Node ID persistence (spec HST §4, HST-U-004)
        if (settings.Node.Id == Guid.Empty)
        {
            settings.Node.Id = Guid.CreateVersion7();
            try
            {
                PersistNodeId(watchpointJsonPath, settings.Node.Id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to persist Node:Id to '{watchpointJsonPath}': {ex.Message}");
            }
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

        var healthBuilder = builder.Services.AddHealthChecks();
        var dbKey = builder.Configuration["Database:Stores:Configuration"] ?? "default";
        var dbHost = builder.Configuration[$"Database:Databases:{dbKey}:Host"];
        var dbPortStr = builder.Configuration[$"Database:Databases:{dbKey}:Port"];
        if (!string.IsNullOrEmpty(dbHost) && int.TryParse(dbPortStr, out var dbPort))
        {
            builder.Services.AddStartupTask<DatabaseConnectionStartupTask>();
            healthBuilder.AddAsyncCheck("database", async ct =>
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(1));
                    await tcp.ConnectAsync(dbHost, dbPort, cts.Token);
                    return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
                }
                catch (Exception ex)
                {
                    return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Database unreachable", ex);
                }
            }, tags: ["ready"]);
        }

        builder.Services.AddHostedService<StartupOrchestrator>();

        // Configure Modules
        var moduleContext = new ModuleContext
        {
            Node = settings,
            Configuration = builder.Configuration,
            Paths = paths,
            IsDevelopment = settings.Node.Environment == NodeEnvironment.Development
        };

        foreach (var module in ModuleCatalog.All)
        {
            module.ConfigureServices(builder.Services, moduleContext);
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

        // Log node starting (HST1102)
        var loggerFactory = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("Watchpoint.Host");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        HostingLog.LogNodeStarting(logger, settings.Node.Id, settings.Node.Name, version, settings.Node.Environment);

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
        app.MapGet("/health/ready", async (INodeReadiness readiness, Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService healthCheckService) =>
        {
            var report = await healthCheckService.CheckHealthAsync(check => check.Tags.Contains("ready"));
            var reasons = new List<string>();

            if (report.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
            {
                foreach (var entry in report.Entries)
                {
                    if (entry.Value.Status != Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)
                    {
                        reasons.Add(entry.Key);
                    }
                }
            }

            if (!readiness.IsReady && !reasons.Contains("startup"))
            {
                reasons.Add("startup");
            }

            if (reasons.Count > 0)
            {
                return Microsoft.AspNetCore.Http.Results.Json(new { status = "notReady", reasons }, statusCode: 503);
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new { status = "ready" });
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

    private static void PersistNodeId(string path, Guid id)
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

        if (!root.ContainsKey("Node"))
        {
            root["Node"] = new JsonObject();
        }

        var nodeObj = root["Node"]?.AsObject();
        if (nodeObj is not null)
        {
            nodeObj["Id"] = id.ToString();
        }

        File.WriteAllText(path, JsonSerializer.Serialize(root, IndentedJsonOptions));
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

    private sealed class DatabaseConnectionStartupTask(
        IConfiguration configuration,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime lifetime,
        TimeProvider? timeProvider = null) : IStartupTask
    {
        private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

        public StartupPhase Phase => StartupPhase.Infrastructure;
        public int Order => 0;
        public string Name => "DatabaseConnection";

        public async Task ExecuteAsync(CancellationToken ct)
        {
            var dbKey = configuration["Database:Stores:Configuration"] ?? "default";
            var dbHost = configuration[$"Database:Databases:{dbKey}:Host"] ?? "localhost";
            var dbPort = int.TryParse(configuration[$"Database:Databases:{dbKey}:Port"], out var p) ? p : 5432;
            var waitDurationStr = configuration["Database:StartupWait"] ?? "PT5M";
            var waitDuration = System.Xml.XmlConvert.ToTimeSpan(waitDurationStr);

            var started = _clock.GetTimestamp();
            while (_clock.GetElapsedTime(started) < waitDuration)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(1));
                    await tcp.ConnectAsync(dbHost, dbPort, cts.Token);
                    return;
                }
                catch
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, ct);
                }
            }

            System.Environment.ExitCode = ExitCodeTempFail;
            lifetime.StopApplication();
            throw new InvalidOperationException("Database unreachable after startup wait period.");
        }
    }
}
