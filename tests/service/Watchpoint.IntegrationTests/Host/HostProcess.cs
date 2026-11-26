using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Npgsql;
using Watchpoint.Testing.Containers;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>A <c>Watchpoint.Host</c> process started with its own settings file, data folder and free ports.</summary>
internal sealed class HostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _tempDir;
    private readonly bool _keepData;
    private NatsContainerFixture? _nats;
    private System.Collections.Concurrent.ConcurrentQueue<string> _output = new();

    private HostProcess(Process process, string tempDir, bool keepData, int mainPort, int managementPort)
    {
        _process = process;
        _tempDir = tempDir;
        _keepData = keepData;
        MainPort = mainPort;
        ManagementPort = managementPort;
    }

    public int MainPort { get; }

    public int ManagementPort { get; }

    /// <summary>The data folder (<c>Paths:Data</c>); binaries are in its <c>bin</c> subfolder.</summary>
    public string DataDirectory => _tempDir;

    /// <summary>The URL of the host's own NATS server (<see cref="StartWithNatsAsync"/>).</summary>
    public string NatsUrl => _nats?.Url ?? throw new InvalidOperationException("The host was not started with its own NATS server.");

    public bool HasExited => _process.HasExited;

    /// <summary>Gets the last lines (at most 200) the host wrote to its standard output and error, for diagnostics.</summary>
    public string Output => string.Join(Environment.NewLine, _output);

    public int ExitCode => _process.ExitCode;

    public static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The <c>Database</c> section in the <c>StorageSettings.Bind</c> format for one PostgreSQL database.</summary>
    public static JsonObject DatabaseSection(string connectionString, string startupWait = "PT30S")
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        return new JsonObject
        {
            ["StartupWait"] = startupWait,
            ["AllowInsecureRemote"] = true,
            ["Databases"] = new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["Host"] = cs.Host,
                    ["Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
                    ["Name"] = cs.Database,
                    ["Username"] = cs.Username,
                    ["PasswordDev"] = cs.Password,
                    ["Tls"] = "Disable",
                },
            },
        };
    }

    /// <summary>
    /// Starts a host with a NATS server of its own (a container that lives as long as the host). The host's event log
    /// reserves gigabytes of JetStream storage (PIP §5.1), which would starve the tests sharing the assembly's NATS
    /// server; see <see cref="NatsUrl"/>.
    /// </summary>
    public static async Task<HostProcess> StartWithNatsAsync(
        string name,
        Dictionary<string, JsonNode> sections,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null,
        string? dataDirectory = null)
    {
        var nats = NatsContainerFixture.WithOptions(new ContainerFixtureOptions());
        try
        {
            await nats.StartAsync(ct);
            sections["Nats"] = new JsonObject { ["Mode"] = "External", ["Url"] = nats.Url };

            // The test server has no TLS, and its address is not always a loopback one (Docker in Docker).
            sections["Log"] = new JsonObject { ["MaxBytes"] = "1GB", ["AllowInsecureRemote"] = "true" };
            var host = Start(name, sections, environment, dataDirectory);
            host._nats = nats;
            return host;
        }
        catch
        {
            await nats.DisposeAsync();
            throw;
        }
    }

    /// <summary>A new, empty data folder for <see cref="Start"/> (to prepare files before the host starts, or to start a second host on it).</summary>
    public static string NewDataDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wp_{name}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Starts the host in Development with <paramref name="sections"/> added to the generated settings. NATS is
    /// External unless a <c>Nats</c> section says otherwise (Bundled needs the nats-server binary in <c>bin</c>); the
    /// host needs a reachable NATS server for its event log (see <see cref="StartWithNatsAsync"/>).
    /// With <paramref name="dataDirectory"/> the folder is used as is and kept when the host is disposed.
    /// </summary>
    public static HostProcess Start(
        string name,
        IReadOnlyDictionary<string, JsonNode> sections,
        IReadOnlyDictionary<string, string>? environment = null,
        string? dataDirectory = null)
    {
        var mainPort = GetFreePort();
        var managementPort = GetFreePort();
        var tempDir = dataDirectory ?? NewDataDirectory(name);
        var config = new JsonObject
        {
            ["Node"] = new JsonObject { ["Environment"] = "Development", ["Name"] = name },
            ["Http"] = new JsonObject { ["Urls"] = new JsonArray($"http://127.0.0.1:{mainPort}") },
            ["Management"] = new JsonObject { ["Urls"] = new JsonArray($"http://127.0.0.1:{managementPort}") },
            ["Nats"] = new JsonObject { ["Mode"] = "External" },
            ["Paths"] = new JsonObject
            {
                ["Data"] = tempDir,
                ["Logs"] = Path.Combine(tempDir, "logs"),
                ["Packages"] = Path.Combine(tempDir, "packages"),
                ["Binaries"] = Path.Combine(tempDir, "bin"),
            },
        };
        foreach (var (key, value) in sections)
        {
            config[key] = value.DeepClone();
        }

        var configPath = Path.Combine(tempDir, "watchpoint.json");
        File.WriteAllText(configPath, config.ToJsonString());

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { HostDll(), $"--config={configPath}" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        // A CI runner under systemd passes INVOCATION_ID / NOTIFY_SOCKET down: the Development host would then refuse to
        // start as a "service" (HST §12, HST-U-048). The test host is a console run.
        process.StartInfo.EnvironmentVariables.Remove("INVOCATION_ID");
        process.StartInfo.EnvironmentVariables.Remove("NOTIFY_SOCKET");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.EnvironmentVariables[key] = value;
        }

        // Drain the redirected streams so the child never blocks on a full pipe; the last lines are kept for diagnostics.
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Keep(string? line)
        {
            if (line is null)
            {
                return;
            }

            output.Enqueue(line);
            while (output.Count > 200 && output.TryDequeue(out _))
            {
            }
        }

        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new HostProcess(process, tempDir, dataDirectory is not null, mainPort, managementPort) { _output = output };
    }

    /// <summary>Kills the host process only (not its children), as a crash or <c>kill -9</c> would.</summary>
    public async Task KillHostOnlyAsync(CancellationToken ct)
    {
        _process.Kill(entireProcessTree: false);
        await _process.WaitForExitAsync(ct);
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }

        _process.Dispose();
        if (_nats is not null)
        {
            await _nats.DisposeAsync();
        }

        if (_keepData)
        {
            return;
        }

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Log files may still be held for a moment; the temp folder is cleaned by the OS.
        }
    }

    private static string HostDll()
    {
        var hostDll = Path.Combine(AppContext.BaseDirectory, "Watchpoint.Host.dll");
        if (!File.Exists(hostDll))
        {
            throw new FileNotFoundException("Watchpoint.Host.dll is not next to the integration tests.", hostDll);
        }

        return hostDll;
    }
}
