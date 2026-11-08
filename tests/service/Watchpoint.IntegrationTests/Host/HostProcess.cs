using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Npgsql;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>A <c>Watchpoint.Host</c> process started with its own settings file, data folder and free ports.</summary>
internal sealed class HostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _tempDir;

    private HostProcess(Process process, string tempDir, int mainPort, int managementPort)
    {
        _process = process;
        _tempDir = tempDir;
        MainPort = mainPort;
        ManagementPort = managementPort;
    }

    public int MainPort { get; }

    public int ManagementPort { get; }

    public bool HasExited => _process.HasExited;

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

    /// <summary>Starts the host in Development with <paramref name="sections"/> added to the generated settings.</summary>
    public static HostProcess Start(string name, IReadOnlyDictionary<string, JsonNode> sections, IReadOnlyDictionary<string, string>? environment = null)
    {
        var mainPort = GetFreePort();
        var managementPort = GetFreePort();
        var tempDir = Path.Combine(Path.GetTempPath(), $"wp_{name}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var config = new JsonObject
        {
            ["Node"] = new JsonObject { ["Environment"] = "Development", ["Name"] = name },
            ["Http"] = new JsonObject { ["Urls"] = new JsonArray($"http://127.0.0.1:{mainPort}") },
            ["Management"] = new JsonObject { ["Urls"] = new JsonArray($"http://127.0.0.1:{managementPort}") },
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
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.EnvironmentVariables[key] = value;
        }

        // Drain the redirected streams so the child never blocks on a full pipe.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new HostProcess(process, tempDir, mainPort, managementPort);
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
