using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Shouldly;
using Watchpoint.Host.Nats;
using Watchpoint.Hosting;
using Watchpoint.Hosting.Supervision;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>
/// The supervisor with real processes (spec HST §7.3, §7.4): the pinned nats-server binary and, on Linux, shell
/// children. No container is needed. Waits are bounded polls or signals, never fixed sleeps.
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "HST")]
public sealed class SupervisorIntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HST_I_005_Bundled_NATS_starts_and_restarts()
    {
        var data = HostProcess.NewDataDirectory("i005");
        var logs = new LogCollector();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        try
        {
            await NatsServerBinary.InstallAsync(Path.Combine(data, "bin"), Ct);
            var settings = new NodeSettings { Node = { Id = Guid.CreateVersion7() }, Nats = { Port = HostProcess.GetFreePort() } };
            var paths = new NodePaths(data, Path.Combine(data, "logs"), Path.Combine(data, "integrations"), Path.Combine(data, "bin"));
            var factory = new NatsProcessSpecFactory(settings, paths);
            (await factory.ReadVersionAsync(TimeProvider.System, Ct)).ShouldBe(Version.Parse(NatsServerBinary.Version));

            await using var supervisor = new Supervisor(new SupervisorOptions { RunDirectory = Path.Combine(data, "run") }, loggerFactory);
            var handle = await supervisor.StartAsync(factory.Create(), Ct).WaitAsync(Limit, Ct);
            handle.State.ShouldBe(SupervisedProcessState.Running);
            var firstPid = handle.Pid.ShouldNotBeNull();
            var pidFile = Path.Combine(data, "run", "nats.pid");
            (await File.ReadAllLinesAsync(pidFile, Ct))[0].ShouldBe(firstPid.ToString(CultureInfo.InvariantCulture));

            // Ready means a client can connect with the token and use JetStream; without the token it cannot.
            var endpoint = factory.Endpoint();
            await NatsClientCheck.WaitUntilUsableAsync(endpoint, Limit, TimeProvider.System, Ct);
            await Should.ThrowAsync<Exception>(() => NatsClientCheck.ConnectOnceAsync(endpoint with { Token = "wrong" }, Ct));

            // One long-lived connection, as the pipeline will hold.
            await using var connection = new NatsConnection(NatsOpts.Default with
            {
                Url = endpoint.Url,
                AuthOpts = new NatsAuthOpts { Token = endpoint.Token },
                ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
                ReconnectWaitMax = TimeSpan.FromMilliseconds(500),
            });
            var js = new NatsJSContext(connection);
            await js.CreateStreamAsync(new StreamConfig("I005", ["i005.>"]), Ct);
            (await js.PublishAsync("i005.a", "before", cancellationToken: Ct)).EnsureSuccess();

            // Kill the server: the supervisor restarts it (back-off 1 s ± 20 %).
            var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handle.StateChanged += (_, state) =>
            {
                if (state == SupervisedProcessState.Running)
                {
                    restarted.TrySetResult();
                }
            };
            using (var server = Process.GetProcessById(firstPid))
            {
                server.Kill();
            }

            await restarted.Task.WaitAsync(Limit, Ct);
            handle.RestartCount.ShouldBe(1);
            var secondPid = handle.Pid.ShouldNotBeNull();
            secondPid.ShouldNotBe(firstPid);
            logs.WithEventId(1151).ShouldHaveSingleItem().Properties["Name"].ShouldBe("nats");
            logs.WithEventId(1150).Count().ShouldBe(2);

            // The same connection recovers, and the acknowledged message survived the kill.
            await PublishEventuallyAsync(js, "i005.a", "after");
            var stream = await js.GetStreamAsync("I005", cancellationToken: Ct);
            stream.Info.State.Messages.ShouldBe(2L);

            // Shutdown: graceful, nothing left behind.
            await supervisor.StopAllAsync(Ct).WaitAsync(Limit, Ct);
            handle.State.ShouldBe(SupervisedProcessState.Stopped);
            handle.LastExitReason.ShouldBe("stopped");
            IsRunning(secondPid).ShouldBeFalse();
            File.Exists(pidFile).ShouldBeFalse();
            supervisor.Processes.ShouldBeEmpty();

            // The server's output went to the structured log, and the token did not.
            var output = logs.Entries.Where(e => e.Category == NatsProcessSpecFactory.LogCategory).ToList();
            output.ShouldContain(e => e.RenderedMessage.Contains("Server is ready", StringComparison.Ordinal));
            output.ShouldAllBe(e => e.Level == LogLevel.Information && (string?)e.Properties["Child"] == "nats");
            logs.Entries.ShouldAllBe(e => !e.RenderedMessage.Contains(endpoint.Token!, StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                // SIGTERM reached the server: it shut down by itself within the grace period.
                output.ShouldContain(e => e.RenderedMessage.Contains("Initiating Shutdown", StringComparison.Ordinal));
            }
        }
        finally
        {
            TryDelete(data);
        }
    }

    [Fact]
    public async Task HST_I_006_Children_die_with_the_host()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: the Job Object kills the children with the host. Linux: see HST-I-007.");
        }

        var data = HostProcess.NewDataDirectory("i006");
        try
        {
            await NatsServerBinary.InstallAsync(Path.Combine(data, "bin"), Ct);
            var natsPort = HostProcess.GetFreePort();
            await using var host = HostProcess.Start("i006", BundledSections(natsPort), dataDirectory: data);
            var natsPid = await WaitForBundledNatsAsync(host, natsPort);

            await host.KillHostOnlyAsync(Ct);

            await Eventually.UntilAsync(() => !IsRunning(natsPid), TimeSpan.FromSeconds(5), "nats-server is gone within 5 s of the host's death", ct: Ct);
        }
        finally
        {
            TryDelete(data);
        }
    }

    [Fact]
    public async Task HST_I_007_Leftover_cleanup()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only: on Windows the Job Object leaves nothing behind (HST-I-006).");
        }

        var data = HostProcess.NewDataDirectory("i007");
        try
        {
            await NatsServerBinary.InstallAsync(Path.Combine(data, "bin"), Ct);
            var natsPort = HostProcess.GetFreePort();
            var pidFile = Path.Combine(data, "run", "nats.pid");
            int leftover;
            await using (var crashed = HostProcess.Start("i007", BundledSections(natsPort), dataDirectory: data))
            {
                leftover = await WaitForBundledNatsAsync(crashed, natsPort);

                // The host dies without any chance to stop its child: the server and its pid file stay behind.
                await crashed.KillHostOnlyAsync(Ct);
            }

            IsRunning(leftover).ShouldBeTrue("the stale pid file must point to a running nats-server");
            (await File.ReadAllLinesAsync(pidFile, Ct))[0].ShouldBe(leftover.ToString(CultureInfo.InvariantCulture));

            // The next start kills it (it holds the port and the store) and starts a fresh server.
            await using var next = HostProcess.Start("i007", BundledSections(natsPort), dataDirectory: data);
            await Eventually.UntilAsync(() => !IsRunning(leftover), Limit, "the leftover nats-server is killed at start", ct: Ct);
            var fresh = await WaitForBundledNatsAsync(next, natsPort, differentFrom: leftover);
            fresh.ShouldNotBe(leftover);

            var killedLog = await Eventually.UntilAsync(
                () => ReadHostLog(data).FirstOrDefault(l => l["Properties"]?["EventId"]?["Id"]?.GetValue<int>() == 1154),
                Limit,
                "event 1154 is logged",
                ct: Ct);
            killedLog["Level"]!.GetValue<string>().ShouldBe("Warning");
            killedLog["Properties"]!["Pid"]!.GetValue<int>().ShouldBe(leftover);
            killedLog["Properties"]!["Name"]!.GetValue<string>().ShouldBe("nats");
        }
        finally
        {
            TryDelete(data);
        }
    }

    [Fact]
    public async Task HST_I_009_Real_child_process_on_Linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Linux only: process group, signals and /proc.");
        }

        var data = HostProcess.NewDataDirectory("i009");
        var logs = new LogCollector();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        try
        {
            await using var supervisor = new Supervisor(new SupervisorOptions { RunDirectory = Path.Combine(data, "run") }, loggerFactory);

            // A chatty child: 20,000 lines are read to the last one (the readiness line comes after them) although
            // only 1,024 may wait for the logger; what is not logged is counted.
            var chatty = await supervisor.StartAsync(
                new ProcessSpec
                {
                    Name = "chatty",
                    Executable = "/bin/sh",
                    Arguments = ["-c", "i=0; while [ $i -lt 20000 ]; do echo \"line $i\"; i=$((i+1)); done; echo ready; exec sleep 600"],
                    Readiness = new ReadinessProbe.StdoutLine("ready", Limit),
                    LogCategory = "Test.Chatty",
                },
                Ct).WaitAsync(Limit, Ct);
            chatty.State.ShouldBe(SupervisedProcessState.Running);
            await chatty.StopAsync(Ct).WaitAsync(Limit, Ct);
            var logged = logs.Entries.Count(e => e.Category == "Test.Chatty");
            var dropped = logs.WithEventId(1155).Sum(e => (long)e.Properties["Dropped"]!);
            (logged + dropped).ShouldBe(20001);

            // A child that ignores SIGTERM and has a descendant: stopping it needs the kill of the whole process group.
            const string Script =
                "trap '' TERM; echo \"env=$WP_TEST\"; pwd; sleep 600 & echo \"descendant=$!\"; echo oops >&2; echo ready; wait";
            var spec = new ProcessSpec
            {
                Name = "shell",
                Executable = "/bin/sh",
                Arguments = ["-c", Script],
                Environment = new Dictionary<string, string> { ["WP_TEST"] = "from-spec" },
                WorkingDirectory = data,
                Readiness = new ReadinessProbe.StdoutLine("ready", Limit),
                StopGracePeriod = TimeSpan.FromMilliseconds(300),
                LogCategory = "Test.Shell",
            };
            var handle = await supervisor.StartAsync(spec, Ct).WaitAsync(Limit, Ct);
            handle.State.ShouldBe(SupervisedProcessState.Running);
            var pid = handle.Pid.ShouldNotBeNull();

            // The child leads its own process group (the host's Ctrl+C does not reach it).
            ProcessGroup(pid).ShouldBe(pid);
            ProcessGroup(pid).ShouldNotBe(ProcessGroup(Environment.ProcessId));

            IEnumerable<CapturedLog> Output() => logs.Entries.Where(e => e.Category == "Test.Shell");
            var descendantLine = await Eventually.UntilAsync(
                () => Output().Select(e => (string)e.Properties["Line"]!).FirstOrDefault(l => l.StartsWith("descendant=", StringComparison.Ordinal)),
                Limit,
                ct: Ct);
            var descendant = int.Parse(descendantLine["descendant=".Length..], CultureInfo.InvariantCulture);
            IsRunning(descendant).ShouldBeTrue();

            var stopwatch = Stopwatch.StartNew();
            await handle.StopAsync(Ct).WaitAsync(Limit, Ct);

            // Killed after the grace period, descendant included; environment, working directory and stderr arrived.
            stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            IsRunning(pid).ShouldBeFalse();
            await Eventually.UntilAsync(() => !IsRunning(descendant), Limit, "the descendant died with the process group", ct: Ct);
            var lines = Output().ToList();
            lines.ShouldContain(e => (string?)e.Properties["Line"] == "env=from-spec" && e.Level == LogLevel.Information);
            lines.ShouldContain(e => (string?)e.Properties["Line"] == data && e.Level == LogLevel.Information);
            lines.ShouldContain(e => (string?)e.Properties["Line"] == "oops" && e.Level == LogLevel.Warning);

            // The exit code of a failing child is reported.
            var failing = await supervisor.StartAsync(
                new ProcessSpec
                {
                    Name = "failing",
                    Executable = "/bin/sh",
                    Arguments = ["-c", "exit 7"],
                    Restart = RestartPolicy.Default with { MaxRestartsInWindow = 0 },
                },
                Ct).WaitAsync(Limit, Ct);
            await Eventually.UntilAsync(() => failing.State == SupervisedProcessState.Failed, Limit, ct: Ct);
            failing.LastExitReason.ShouldNotBeNull().ShouldStartWith("exited with code 7");
        }
        finally
        {
            TryDelete(data);
        }
    }

    private static Dictionary<string, JsonNode> BundledSections(int natsPort) => new()
    {
        // Nothing listens on the database port: the host starts NATS (Infrastructure, before the database wait)
        // and then waits for the database, which is all these tests need. No container involved.
        ["Database"] = HostProcess.DatabaseSection(
            $"Host=127.0.0.1;Port={HostProcess.GetFreePort()};Database=wp;Username=wp;Password=wp", startupWait: "PT2M"),
        ["Nats"] = new JsonObject { ["Mode"] = "Bundled", ["Port"] = natsPort },
    };

    /// <summary>Waits until the host's nats-server accepts connections; returns its pid from the pid file.</summary>
    private static async Task<int> WaitForBundledNatsAsync(HostProcess host, int natsPort, int? differentFrom = null)
    {
        var pidFile = Path.Combine(host.DataDirectory, "run", "nats.pid");
        var pid = 0;
        await Eventually.UntilAsync(
            () =>
            {
                host.HasExited.ShouldBeFalse("the host must keep running");
                return TryReadPid(pidFile, out pid) && pid != differentFrom && IsRunning(pid) && CanConnect(natsPort);
            },
            Limit,
            "the bundled nats-server is started by the host",
            TimeSpan.FromMilliseconds(100),
            Ct);
        return pid;
    }

    private static async Task PublishEventuallyAsync(NatsJSContext js, string subject, string data)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                (await js.PublishAsync(subject, data, cancellationToken: Ct)).EnsureSuccess();
                return;
            }
            catch (Exception ex) when (ex is NatsException && started.Elapsed < Limit)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            }
        }
    }

    private static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(pidFile) && int.TryParse(File.ReadLines(pidFile).FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out pid);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Whether a process exists and is not a zombie waiting to be reaped.</summary>
    private static bool IsRunning(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)..][0] is not ('Z' or 'X');
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int ProcessGroup(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2], CultureInfo.InvariantCulture);
    }

    /// <summary>The host's JSON log lines (the file is still open in the host).</summary>
    private static List<JsonNode> ReadHostLog(string data)
    {
        var lines = new List<JsonNode>();
        var folder = Path.Combine(data, "logs");
        if (!Directory.Exists(folder))
        {
            return lines;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "watchpoint-*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    if (JsonNode.Parse(line) is { } node)
                    {
                        lines.Add(node);
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // A line still being written.
                }
            }
        }

        return lines;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Files may still be held for a moment; the temp folder is cleaned by the OS.
        }
    }
}
