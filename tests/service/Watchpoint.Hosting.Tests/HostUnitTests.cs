using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Watchpoint.Host;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
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
        using var logs = new LogCollector();
        var logger = logs.CreateLogger("Watchpoint.Host");

        try
        {
            File.WriteAllText(watchpointJsonPath, """{"Node": {"Name": "n1"}}""");

            // First start: no Node:Id -> generated v7 and written back, other keys kept.
            var first = Bind(watchpointJsonPath);
            var generated = Program.EnsureNodeId(first, watchpointJsonPath, logger);
            NodeSettings.IsValidV7(generated).ShouldBeTrue();
            var written = JsonNode.Parse(File.ReadAllText(watchpointJsonPath))!;
            written["Node"]!["Id"]!.GetValue<string>().ShouldBe(generated.ToString());
            written["Node"]!["Name"]!.GetValue<string>().ShouldBe("n1");
            var writtenAt = File.GetLastWriteTimeUtc(watchpointJsonPath);

            // Second start: reads the persisted id and does not write again.
            File.SetLastWriteTimeUtc(watchpointJsonPath, writtenAt.AddMinutes(-5));
            var second = Bind(watchpointJsonPath);
            second.Node.Id.ShouldBe(generated);
            Program.EnsureNodeId(second, watchpointJsonPath, logger).ShouldBe(generated);
            File.GetLastWriteTimeUtc(watchpointJsonPath).ShouldBe(writtenAt.AddMinutes(-5));
            logs.WithEventId(HostingLog.NodeIdPersistFailed.Id).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        static NodeSettings Bind(string path)
        {
            var settings = new NodeSettings();
            new ConfigurationBuilder().AddJsonFile(path).Build().Bind(settings);
            return settings;
        }
    }

    [Fact]
    public void HST_U_046_Site_id_generated_once_persisted_and_validated()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wp_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var watchpointJsonPath = Path.Combine(tempDir, "watchpoint.json");
        using var logs = new LogCollector();
        var logger = logs.CreateLogger("Watchpoint.Host");

        try
        {
            File.WriteAllText(watchpointJsonPath, """{"Node": {"Name": "n1"}}""");

            // First start: no Site:Id -> generated v7 and written back next to Node:Id, other keys kept.
            var first = Bind(watchpointJsonPath);
            var nodeId = Program.EnsureNodeId(first, watchpointJsonPath, logger);
            var siteId = Program.EnsureSiteId(first, watchpointJsonPath, logger);
            NodeSettings.IsValidV7(siteId).ShouldBeTrue();
            siteId.ShouldNotBe(nodeId);
            var written = JsonNode.Parse(File.ReadAllText(watchpointJsonPath))!;
            written["Site"]!["Id"]!.GetValue<string>().ShouldBe(siteId.ToString());
            written["Node"]!["Id"]!.GetValue<string>().ShouldBe(nodeId.ToString());
            written["Node"]!["Name"]!.GetValue<string>().ShouldBe("n1");

            // Second start: the persisted id is read and kept (the token issuer must not change over a restart).
            var second = Bind(watchpointJsonPath);
            second.Site.Id.ShouldBe(siteId);
            Program.EnsureSiteId(second, watchpointJsonPath, logger).ShouldBe(siteId);
            logs.WithEventId(HostingLog.NodeIdPersistFailed.Id).ShouldBeEmpty();

            // Validation: a set id must be a v7 UUID.
            var invalid = new NodeSettings();
            invalid.Site.Id = Guid.NewGuid();
            invalid.Validate(new ValidationContext(invalid)).ShouldContain(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Site:Id"));
            var empty = new NodeSettings();
            empty.Validate(new ValidationContext(empty)).ShouldNotContain(r => r.ErrorMessage != null && r.ErrorMessage.Contains("Site:Id"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        static NodeSettings Bind(string path)
        {
            var settings = new NodeSettings();
            new ConfigurationBuilder().AddJsonFile(path).Build().Bind(settings);
            return settings;
        }
    }

    [Fact]
    public void HST_U_047_Module_with_invalid_settings_stops_the_start()
    {
        var siteId = Guid.CreateVersion7();
        var valid = Context([("Site:Id", siteId.ToString()), ("Identity:Lockout:Threshold", "3")]);
        var settings = Watchpoint.Host.Modules.IdentityModule.BindSettings(valid);
        settings.Lockout.Threshold.ShouldBe(3);
        settings.Jwt.SiteId.ShouldBe(siteId.ToString());

        // An invalid or unknown Identity key is refused with the key named (never a value), as HST §4 does for node settings.
        var invalid = Context([("Site:Id", siteId.ToString()), ("Identity:Lockout:Threshold", "0"), ("Identity:Lockuot:Window", "PT1M")]);
        var ex = Should.Throw<InvalidSettingsException>(() => Watchpoint.Host.Modules.IdentityModule.BindSettings(invalid));
        ex.Module.ShouldBe("Identity");
        ex.Problems.ShouldContain("Identity:Lockout:Threshold: common.value.out_of_range");
        ex.Problems.ShouldContain("Identity:Lockuot:Window: identity.settings.unknown_key");
        ex.Problems.ShouldNotContain("PT1M");

        // Without a site id there is no issuer: refused as well (the host generates one before the modules are composed).
        Should.Throw<InvalidSettingsException>(() => Watchpoint.Host.Modules.IdentityModule.BindSettings(Context([])))
            .Problems.ShouldContain("Site:Id");

        static ModuleContext Context(IEnumerable<(string Key, string Value)> values)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                .Build();
            var node = new NodeSettings();
            configuration.Bind(node);
            return new ModuleContext { Node = node, Configuration = configuration, Paths = NodePaths.Resolve(node.Paths), IsDevelopment = true };
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
        var clock = new TestClock();
        var start = clock.GetTimestamp();

        IStartupTask DelayedTask(string id, int order) => new TestStartupTask("Task" + id, StartupPhase.Runtime, order, async () =>
        {
            lock (log)
            {
                log.Add($"start-{id}@{clock.GetElapsedTime(start).TotalSeconds:0}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock);
            lock (log)
            {
                log.Add($"end-{id}@{clock.GetElapsedTime(start).TotalSeconds:0}");
            }
        });

        var orchestrator = new StartupOrchestrator(
            [DelayedTask("2", 2), DelayedTask("1", 1)], [], new NodeReadiness(), new FakeHostApplicationLifetime(),
            Options.Create(new NodeSettings()), NullLogger<StartupOrchestrator>.Instance, clock);

        var run = orchestrator.RunStartupTasksAsync(CancellationToken.None);
        for (var i = 0; i < 500 && !run.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        await run;
        log.ShouldBe(["start-1@0", "end-1@1", "start-2@1", "end-2@2"]);
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
        try
        {
            await orchestrator.RunStartupTasksAsync(CancellationToken.None);

            schemaRan.ShouldBeTrue();
            runtimeRan.ShouldBeFalse();
            readiness.IsReady.ShouldBeFalse();
            lifetime.StopRequested.ShouldBeTrue();
            Environment.ExitCode.ShouldBe(StartupOrchestrator.ExitCodeStartupFailure);
        }
        finally
        {
            Environment.ExitCode = 0;
        }
    }

    [Fact]
    public async Task HST_U_008_Readiness_flips_after_Edge()
    {
        var readiness = new NodeReadiness();
        var healthy = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["storage"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);
        (int StatusCode, IReadOnlyList<string> Reasons)? beforeEdge = null;

        var taskRuntime = new TestStartupTask("Runtime", StartupPhase.Runtime, 0, () =>
        {
            beforeEdge = Program.EvaluateReadiness(readiness.IsReady, healthy);
        });
        var taskEdge = new TestStartupTask("Edge", StartupPhase.Edge, 0, () => { });

        var orchestrator = new StartupOrchestrator(
            [taskEdge, taskRuntime], [], readiness, new FakeHostApplicationLifetime(),
            Options.Create(new NodeSettings()), NullLogger<StartupOrchestrator>.Instance);
        await orchestrator.RunStartupTasksAsync(CancellationToken.None);

        beforeEdge.ShouldNotBeNull();
        beforeEdge.Value.StatusCode.ShouldBe(503);
        beforeEdge.Value.Reasons.ShouldBe([Program.StartupReason]);

        var afterEdge = Program.EvaluateReadiness(readiness.IsReady, healthy);
        afterEdge.StatusCode.ShouldBe(200);
        afterEdge.Reasons.ShouldBeEmpty();

        // An unhealthy storage check keeps the node not ready, with reason "database".
        var databaseDown = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["storage"] = new(HealthStatus.Unhealthy, "database unreachable: default", TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);
        var notReady = Program.EvaluateReadiness(readiness.IsReady, databaseDown);
        notReady.StatusCode.ShouldBe(503);
        notReady.Reasons.ShouldBe([Program.DatabaseReason]);
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
        var slowStarted = false;
        var nextRan = false;
        var clock = new TestClock();
        using var logs = new LogCollector();

        // Ignores its cancellation token and would run for 5 minutes; the budget is 30 s.
        var slowTask = new TestShutdownTask("SlowTask", StartupPhase.Edge, async () =>
        {
            slowStarted = true;
            await Task.Delay(TimeSpan.FromMinutes(5), clock);
        });
        var nextTask = new TestShutdownTask("NextTask", StartupPhase.Infrastructure, () => nextRan = true);

        var orchestrator = new StartupOrchestrator(
            [], [slowTask, nextTask], new NodeReadiness(), new FakeHostApplicationLifetime(),
            Options.Create(new NodeSettings { Shutdown = new ShutdownSection { Timeout = "PT30S" } }),
            new Logger<StartupOrchestrator>(new LoggerFactory([logs])), clock);

        var stopping = orchestrator.StoppingAsync(CancellationToken.None);
        slowStarted.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(31));
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        logs.WithEventId(HostingLog.ShutdownTaskAbandoned.Id).Select(e => e.Properties["Task"]).ShouldBe(["SlowTask"]);
        nextRan.ShouldBeTrue();
        logs.WithEventId(HostingLog.ShutdownTaskCompleted.Id).Select(e => e.Properties["Task"]).ShouldBe(["NextTask"]);
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
        using var logs = new LogCollector();
        var logger = logs.CreateLogger("Watchpoint.Host");

        // Privileged in Production without --allow-elevated -> exit 78 with a critical log.
        Program.CheckElevation(isPrivileged: true, NodeEnvironment.Production, allowElevated: false, logger).ShouldBe(Program.ExitCodeElevated);
        logs.WithEventId(HostingLog.ElevatedRefused.Id).Single().Level.ShouldBe(LogLevel.Critical);

        // With --allow-elevated -> warning only, start-up continues.
        logs.Clear();
        Program.CheckElevation(isPrivileged: true, NodeEnvironment.Production, allowElevated: true, logger).ShouldBeNull();
        logs.Entries.Single().EventId.ShouldBe(HostingLog.ElevatedAllowed.Id);
        logs.Entries.Single().Level.ShouldBe(LogLevel.Warning);

        // Not privileged, or Development -> nothing logged.
        logs.Clear();
        Program.CheckElevation(isPrivileged: false, NodeEnvironment.Production, allowElevated: false, logger).ShouldBeNull();
        Program.CheckElevation(isPrivileged: true, NodeEnvironment.Development, allowElevated: false, logger).ShouldBeNull();
        logs.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void HST_U_048_Development_refused_on_a_production_install()
    {
        using var logs = new LogCollector();
        var logger = logs.CreateLogger("Watchpoint.Host");
        var defaultData = NodePaths.Resolve().Data;
        var custom = Path.Combine(Path.GetTempPath(), "wp-dev-" + Guid.NewGuid().ToString("N"));
        NodeSettings Dev(string? data) => new()
        {
            Node = new NodeSection { Environment = NodeEnvironment.Development },
            Paths = new PathsSection { Data = data },
        };

        // Service host (Windows service / systemd) → exit 78.
        Program.CheckDevelopmentInstall(Dev(custom), runsAsService: true, defaultData, logger).ShouldBe(Program.ExitCodeConfig);
        logs.WithEventId(HostingLog.InvalidNodeSettings.Id).Single().Level.ShouldBe(LogLevel.Critical);

        // Console run with the OS default data path → exit 78.
        logs.Clear();
        Program.CheckDevelopmentInstall(Dev(null), runsAsService: false, defaultData, logger).ShouldBe(Program.ExitCodeConfig);
        Program.CheckDevelopmentInstall(Dev(defaultData), runsAsService: false, defaultData, logger).ShouldBe(Program.ExitCodeConfig);

        // Console run with a custom data path → starts, 1102 at Warning.
        logs.Clear();
        Program.CheckDevelopmentInstall(Dev(custom), runsAsService: false, defaultData, logger).ShouldBeNull();
        logs.Entries.ShouldBeEmpty();
        Program.NodeStartingLevel(NodeEnvironment.Development).ShouldBe(LogLevel.Warning);
        Program.NodeStartingLevel(NodeEnvironment.Production).ShouldBe(LogLevel.Information);

        // Production is never refused by this check; systemd is detected by its variables.
        var production = new NodeSettings { Node = new NodeSection { Environment = NodeEnvironment.Production } };
        Program.CheckDevelopmentInstall(production, runsAsService: true, defaultData, logger).ShouldBeNull();
        Program.RunsAsService(name => name == "NOTIFY_SOCKET" ? "/run/systemd/notify" : null).ShouldBeFalse();
        Program.RunsAsService(name => name == "INVOCATION_ID" ? "abc" : null).ShouldBeTrue();
        Program.RunsAsService(_ => null).ShouldBeFalse();
    }

    [Fact]
    public void HST_U_015_Dev_seed_skipped_outside_Development()
    {
        using var logs = new LogCollector();
        var logger = logs.CreateLogger("Watchpoint.Host");

        var production = new NodeSettings
        {
            Node = new NodeSection { Environment = NodeEnvironment.Production },
            Dev = new DevSection { SeedFile = "seed/sample-site.json" }
        };
        DevSeedGate.ShouldRun(production, logger).ShouldBeFalse();
        logs.WithEventId(HostingLog.DevSeedFailed.Id).ShouldHaveSingleItem();

        logs.Clear();
        var development = new NodeSettings
        {
            Node = new NodeSection { Environment = NodeEnvironment.Development },
            Dev = new DevSection { SeedFile = "seed/sample-site.json" }
        };
        DevSeedGate.ShouldRun(development, logger).ShouldBeTrue();
        logs.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void HST_U_049_Configured_lists_replace_the_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Http:Urls:0"] = "http://127.0.0.1:5000",
            ["Management:Urls:0"] = "http://127.0.0.1:5001",
            ["Management:Urls:1"] = "http://[::1]:5001",
            ["Node:Roles:0"] = "front",
        }).Build();

        var settings = NodeSettings.Bind(configuration);

        settings.Http.Urls.ShouldBe(["http://127.0.0.1:5000"]);
        settings.Management.Urls.ShouldBe(["http://127.0.0.1:5001", "http://[::1]:5001"]);
        settings.Node.Roles.ShouldBe(["front"]);

        // Lists the configuration does not set keep their defaults.
        var defaults = NodeSettings.Bind(new ConfigurationBuilder().Build());
        defaults.Http.Urls.ShouldBe(["https://0.0.0.0:8443"]);
        defaults.Management.Urls.ShouldBe(["http://127.0.0.1:9464"]);
        defaults.Node.Roles.ShouldBe(["front", "integration", "pipeline", "history", "media"]);
    }

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
