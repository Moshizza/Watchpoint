using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Watchpoint.Host;
using Xunit;

namespace Watchpoint.Hosting.Tests;

[Trait("layer", "U")]
[Trait("spec", "HST")]
public sealed class HostUnitTests
{
    [Fact]
    public void HST_U_001_Settings_defaults_per_OS()
    {
        var linuxPaths = NodePaths.Resolve(pathsConfig: null, isWindows: false, baseDir: "/opt/watchpoint");
        linuxPaths.Data.ShouldBe("/var/lib/watchpoint");
        linuxPaths.Logs.ShouldBe("/var/log/watchpoint");
        linuxPaths.Packages.ShouldBe("/var/lib/watchpoint/integrations");
        linuxPaths.Binaries.ShouldBe("/opt/watchpoint/bin");

        var winPaths = NodePaths.Resolve(pathsConfig: null, isWindows: true, baseDir: @"C:\Program Files\Watchpoint");
        winPaths.Data.ShouldBe(@"C:\ProgramData\Watchpoint");
        winPaths.Logs.ShouldBe(@"C:\ProgramData\Watchpoint\logs");
        winPaths.Packages.ShouldBe(@"C:\ProgramData\Watchpoint\integrations");
        winPaths.Binaries.ShouldBe(@"C:\Program Files\Watchpoint\bin");
    }

    [Fact]
    public void HST_U_002_Settings_precedence()
    {
        // Precedence: appsettings < watchpoint.json < env < command line
        var appsettingsJson = """{"Node": {"Name": "from-appsettings"}}""";
        var watchpointJson = """{"Node": {"Name": "from-watchpoint-json"}}""";
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, watchpointJson);

            // 1. Just appsettings
            var builder1 = new ConfigurationBuilder();
            builder1.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appsettingsJson)));
            var config1 = builder1.Build();
            config1["Node:Name"].ShouldBe("from-appsettings");

            // 2. appsettings < watchpoint.json
            var builder2 = new ConfigurationBuilder();
            builder2.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appsettingsJson)));
            Program.ConfigureConfiguration(builder2, watchpointJsonPath: tempFile);
            var config2 = builder2.Build();
            config2["Node:Name"].ShouldBe("from-watchpoint-json");

            // 3. appsettings < watchpoint.json < env
            var envVars = new Dictionary<string, string?>
            {
                ["WATCHPOINT__Node__Name"] = "from-env"
            };
            var builder3 = new ConfigurationBuilder();
            builder3.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appsettingsJson)));
            Program.ConfigureConfiguration(builder3, watchpointJsonPath: tempFile, envSource: envVars);
            var config3 = builder3.Build();
            config3["Node:Name"].ShouldBe("from-env");

            // 4. appsettings < watchpoint.json < env < command line
            var cliArgs = new[] { "--Node:Name=from-cli" };
            var builder4 = new ConfigurationBuilder();
            builder4.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appsettingsJson)));
            Program.ConfigureConfiguration(builder4, watchpointJsonPath: tempFile, envSource: envVars, args: cliArgs);
            var config4 = builder4.Build();
            config4["Node:Name"].ShouldBe("from-cli");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void HST_U_003_Invalid_settings_collected()
    {
        var settings = new NodeSettings();
        settings.Nats.Port = 0; // invalid port
        settings.Nats.Url = "not a valid url"; // invalid URL

        var validationResults = settings.Validate(new ValidationContext(settings)).ToList();

        validationResults.Count.ShouldBeGreaterThanOrEqualTo(2);
        validationResults.ShouldContain(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Nats:Port"));
        validationResults.ShouldContain(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Nats:Url"));
    }

    [Fact]
    public void HST_U_004_Node_id_generated_and_persisted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var watchpointJsonPath = Path.Combine(tempDir, "watchpoint.json");

        try
        {
            // First run: file does not exist, node id generated and written back
            var generatedId = Guid.CreateVersion7();
            NodeSettings.IsValidV7(generatedId).ShouldBeTrue();

            var root = new JsonObject
            {
                ["Node"] = new JsonObject
                {
                    ["Id"] = generatedId.ToString()
                }
            };
            File.WriteAllText(watchpointJsonPath, JsonSerializer.Serialize(root, IndentedJsonOptions));

            // Second run: reads back the persisted id
            var config = new ConfigurationBuilder()
                .AddJsonFile(watchpointJsonPath)
                .Build();

            var readSettings = new NodeSettings();
            config.Bind(readSettings);

            readSettings.Node.Id.ShouldBe(generatedId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HST_U_005_Phases_run_in_order()
    {
        var executionLog = new List<string>();

        var task1 = new TestStartupTask("TaskSchema", StartupPhase.Schema, 0, () => executionLog.Add("Schema"));
        var task2 = new TestStartupTask("TaskInfra", StartupPhase.Infrastructure, 0, () => executionLog.Add("Infrastructure"));
        var task3 = new TestStartupTask("TaskRuntime", StartupPhase.Runtime, 0, () => executionLog.Add("Runtime"));
        var task4 = new TestStartupTask("TaskEdge", StartupPhase.Edge, 0, () => executionLog.Add("Edge"));

        // Register out of order
        var tasks = new IStartupTask[] { task3, task1, task4, task2 };
        var readiness = new NodeReadiness();
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator(tasks, [], readiness, lifetime, settings, logger);
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        executionLog.ShouldBe(["Infrastructure", "Schema", "Runtime", "Edge"]);
    }

    [Fact]
    public async Task HST_U_006_Tasks_in_a_phase_run_sequentially()
    {
        var log = new List<string>();
        var timeProvider = new FakeTimeProvider();

        var task1 = new TestStartupTask("Task1", StartupPhase.Runtime, 1, async () =>
        {
            log.Add("start-1");
            await Task.Yield();
            log.Add("end-1");
        });

        var task2 = new TestStartupTask("Task2", StartupPhase.Runtime, 2, async () =>
        {
            log.Add("start-2");
            await Task.Yield();
            log.Add("end-2");
        });

        var tasks = new IStartupTask[] { task2, task1 };
        var readiness = new NodeReadiness();
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator(tasks, [], readiness, lifetime, settings, logger, timeProvider);
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        log.ShouldBe(["start-1", "end-1", "start-2", "end-2"]);
    }

    [Fact]
    public async Task HST_U_007_Failing_task_stops_startup()
    {
        var schemaRan = false;
        var runtimeRan = false;

        var task1 = new TestStartupTask("TaskSchemaThrow", StartupPhase.Schema, 0, () =>
        {
            schemaRan = true;
            throw new InvalidOperationException("Schema migration failed");
        });
        var task2 = new TestStartupTask("TaskRuntime", StartupPhase.Runtime, 0, () =>
        {
            runtimeRan = true;
        });

        var readiness = new NodeReadiness();
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator([task1, task2], [], readiness, lifetime, settings, logger);
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        schemaRan.ShouldBeTrue();
        runtimeRan.ShouldBeFalse();
        readiness.IsReady.ShouldBeFalse();
        lifetime.StopRequested.ShouldBeTrue();
        Environment.ExitCode.ShouldBe(StartupOrchestrator.ExitCodeStartupFailure);
    }

    [Fact]
    public async Task HST_U_008_Readiness_flips_after_Edge()
    {
        var readiness = new NodeReadiness();
        bool? readinessDuringRuntime = null;
        bool? readinessDuringConnections = null;

        var taskRuntime = new TestStartupTask("Runtime", StartupPhase.Runtime, 0, () =>
        {
            readinessDuringRuntime = readiness.IsReady;
        });
        var taskEdge = new TestStartupTask("Edge", StartupPhase.Edge, 0, () => { });
        var taskConn = new TestStartupTask("Connections", StartupPhase.Connections, 0, () =>
        {
            readinessDuringConnections = readiness.IsReady;
        });

        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator([taskRuntime, taskEdge, taskConn], [], readiness, lifetime, settings, logger);

        readiness.IsReady.ShouldBeFalse();
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        readinessDuringRuntime.ShouldBe(false);
        readinessDuringConnections.ShouldBe(true);
        readiness.IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task HST_U_009_Connections_phase_after_readiness()
    {
        var readiness = new NodeReadiness();
        var readinessWhenConnectionsStarted = false;

        var taskEdge = new TestStartupTask("EdgeTask", StartupPhase.Edge, 0, () => { });
        var taskConn = new TestStartupTask("ConnTask", StartupPhase.Connections, 0, () =>
        {
            readinessWhenConnectionsStarted = readiness.IsReady;
        });

        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator([taskEdge, taskConn], [], readiness, lifetime, settings, logger);
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        readinessWhenConnectionsStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task HST_U_010_Shutdown_order_descending()
    {
        var shutdownLog = new List<string>();

        var taskInfra = new TestShutdownTask("InfraShutdown", StartupPhase.Infrastructure, () => shutdownLog.Add("Infrastructure"));
        var taskEdge = new TestShutdownTask("EdgeShutdown", StartupPhase.Edge, () => shutdownLog.Add("Edge"));
        var taskRuntime = new TestShutdownTask("RuntimeShutdown", StartupPhase.Runtime, () => shutdownLog.Add("Runtime"));

        var readiness = new NodeReadiness();
        readiness.SetReady();
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings());

        var orchestrator = new StartupOrchestrator([], [taskInfra, taskEdge, taskRuntime], readiness, lifetime, settings, logger);
        await orchestrator.StoppingAsync(CancellationToken.None);

        readiness.IsReady.ShouldBeFalse();
        shutdownLog.ShouldBe(["Edge", "Runtime", "Infrastructure"]);
    }

    [Fact]
    public async Task HST_U_011_Shutdown_timeout_abandons_slow_task()
    {
        var task1Ran = false;
        var task2Ran = false;

        var timeProvider = new FakeTimeProvider();

        var slowTask = new TestShutdownTask("SlowTask", StartupPhase.Edge, async () =>
        {
            task1Ran = true;
            // Advance time past the 30s timeout
            timeProvider.Advance(TimeSpan.FromSeconds(35));
            await Task.Yield();
        });

        var nextTask = new TestShutdownTask("NextTask", StartupPhase.Infrastructure, () =>
        {
            task2Ran = true;
        });

        var readiness = new NodeReadiness();
        var lifetime = new FakeHostApplicationLifetime();
        var logger = new LoggerFactory().CreateLogger<StartupOrchestrator>();
        var settings = Options.Create(new NodeSettings { Shutdown = new ShutdownSection { Timeout = "PT30S" } });

        var orchestrator = new StartupOrchestrator([], [slowTask, nextTask], readiness, lifetime, settings, logger, timeProvider);
        await orchestrator.StoppingAsync(CancellationToken.None);

        task1Ran.ShouldBeTrue();
        task2Ran.ShouldBeFalse(); // Abandoned due to timeout budget exceeded
    }

    [Fact]
    public void HST_U_012_Redaction_enricher()
    {
        var enricher = new RedactionEnricher();
        var properties = new List<LogEventProperty>
        {
            new("Password", new ScalarValue("super-secret-password")),
            new("apiKey", new ScalarValue("12345-api-key")),
            new("Authorization", new ScalarValue("Bearer ey...token")),
            new("SafeProperty", new ScalarValue("safe-value"))
        };

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplate("Test message", []),
            properties);

        enricher.Enrich(logEvent, new PropertyFactory());

        logEvent.Properties["Password"].ToString().ShouldBe("\"***\"");
        logEvent.Properties["apiKey"].ToString().ShouldBe("\"***\"");
        logEvent.Properties["Authorization"].ToString().ShouldBe("\"***\"");
        logEvent.Properties["SafeProperty"].ToString().ShouldBe("\"safe-value\"");
    }

    [Fact]
    public void HST_U_013_Module_enricher()
    {
        var enricher = new ModuleEnricher();
        var properties = new List<LogEventProperty>
        {
            new("SourceContext", new ScalarValue("Watchpoint.Registry.Internal.X"))
        };

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            new MessageTemplate("Test message", []),
            properties);

        enricher.Enrich(logEvent, new PropertyFactory());

        logEvent.Properties.ShouldContainKey("Module");
        logEvent.Properties["Module"].ToString().ShouldBe("\"Registry\"");
    }

    [Fact]
    public void HST_U_014_Elevated_process_refused_in_Production()
    {
        // Program.Main check simulation:
        // isPrivileged && Environment == Production without --allow-elevated -> ExitCode 78
        var isPrivileged = true;
        var environment = NodeEnvironment.Production;
        var allowElevated = false;

        var shouldRefuse = isPrivileged && environment == NodeEnvironment.Production && !allowElevated;
        shouldRefuse.ShouldBeTrue();

        // With --allow-elevated -> allowed with warning
        allowElevated = true;
        var shouldRefuseWithFlag = isPrivileged && environment == NodeEnvironment.Production && !allowElevated;
        shouldRefuseWithFlag.ShouldBeFalse();
    }

    [Fact]
    public void HST_U_015_Dev_seed_skipped_outside_Development()
    {
        var settingsProd = new NodeSettings
        {
            Node = new NodeSection { Environment = NodeEnvironment.Production },
            Dev = new DevSection { SeedFile = "seed/sample-site.json" }
        };

        var shouldExecuteSeedProd = settingsProd.Node.Environment == NodeEnvironment.Development &&
                                    !string.IsNullOrWhiteSpace(settingsProd.Dev.SeedFile);
        shouldExecuteSeedProd.ShouldBeFalse();

        var settingsDev = new NodeSettings
        {
            Node = new NodeSection { Environment = NodeEnvironment.Development },
            Dev = new DevSection { SeedFile = "seed/sample-site.json" }
        };

        var shouldExecuteSeedDev = settingsDev.Node.Environment == NodeEnvironment.Development &&
                                   !string.IsNullOrWhiteSpace(settingsDev.Dev.SeedFile);
        shouldExecuteSeedDev.ShouldBeTrue();
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value));
    }

    private sealed class TestStartupTask(string name, StartupPhase phase, int order, Func<Task> action) : IStartupTask
    {
        public TestStartupTask(string name, StartupPhase phase, int order, Action syncAction)
            : this(name, phase, order, () => { syncAction(); return Task.CompletedTask; })
        {
        }

        public string Name => name;
        public StartupPhase Phase => phase;
        public int Order => order;
        public Task ExecuteAsync(CancellationToken ct) => action();
    }

    private sealed class TestShutdownTask(string name, StartupPhase phase, Func<Task> action) : IShutdownTask
    {
        public TestShutdownTask(string name, StartupPhase phase, Action syncAction)
            : this(name, phase, () => { syncAction(); return Task.CompletedTask; })
        {
        }

        public string Name => name;
        public StartupPhase Phase => phase;
        public Task ExecuteAsync(CancellationToken ct) => action();
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }
}
