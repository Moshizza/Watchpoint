using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Watchpoint.Spikes.NatsJetStream;

/// <summary>
/// Starts local <c>nats-server</c> processes: one standalone JetStream node, or a 3-node JetStream cluster
/// (the CLU topology "two nodes + witness" has the same Raft behaviour as three nodes).
/// </summary>
internal sealed class NatsCluster : IAsyncDisposable
{
    private readonly string _serverPath;
    private readonly string _workDir;
    private readonly Process?[] _processes;
    private readonly int _basePort;

    private readonly bool _syncAlways;

    private NatsCluster(string serverPath, string workDir, int size, int basePort, bool syncAlways)
    {
        _syncAlways = syncAlways;
        _serverPath = serverPath;
        _workDir = workDir;
        _processes = new Process?[size];
        _basePort = basePort;
    }

    public int Size => _processes.Length;

    /// <summary>Client URL list of every node (used for reconnect on node loss).</summary>
    public string Urls => string.Join(",", Enumerable.Range(0, Size).Select(ClientUrl));

    public string ClientUrl(int node) => string.Create(CultureInfo.InvariantCulture, $"nats://127.0.0.1:{_basePort + node}");

    public static string NodeName(int node) => string.Create(CultureInfo.InvariantCulture, $"n{node}");

    public static async Task<NatsCluster> StartAsync(string serverPath, string workDir, int size, int basePort,
        bool syncAlways = false, bool keepData = false)
    {
        if (!keepData && Directory.Exists(workDir))
        {
            Directory.Delete(workDir, recursive: true);
        }

        Directory.CreateDirectory(workDir);
        var cluster = new NatsCluster(serverPath, workDir, size, basePort, syncAlways);
        for (var i = 0; i < size; i++)
        {
            await File.WriteAllTextAsync(cluster.ConfigPath(i), cluster.Config(i)).ConfigureAwait(false);
        }

        for (var i = 0; i < size; i++)
        {
            cluster.Start(i);
        }

        await cluster.WaitReadyAsync().ConfigureAwait(false);
        return cluster;
    }

    /// <summary>SIGKILL a node (no graceful shutdown: models power loss / crash).</summary>
    public void Kill(int node)
    {
        var p = _processes[node];
        if (p is { HasExited: false })
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
        }

        p?.Dispose();
        _processes[node] = null;
    }

    public void Start(int node)
    {
        var psi = new ProcessStartInfo(_serverPath, ["-c", ConfigPath(node)])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("nats-server did not start");
        var log = Path.Combine(_workDir, NodeName(node) + ".log");
        p.OutputDataReceived += (_, e) => Append(log, e.Data);
        p.ErrorDataReceived += (_, e) => Append(log, e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _processes[node] = p;
    }

    public async Task WaitReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        for (var i = 0; i < Size; i++)
        {
            if (_processes[i] is null)
            {
                continue;
            }

            var url = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{MonitorPort(i)}/healthz"));
            while (true)
            {
                try
                {
                    using var r = await http.GetAsync(url).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException)
                {
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("nats-server node " + NodeName(i) + " not ready");
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        for (var i = 0; i < Size; i++)
        {
            Kill(i);
        }

        return ValueTask.CompletedTask;
    }

    private static readonly Lock _logLock = new();

    private static void Append(string path, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private int MonitorPort(int node) => _basePort + 100 + node;

    private int ClusterPort(int node) => _basePort + 200 + node;

    private string ConfigPath(int node) => Path.Combine(_workDir, NodeName(node) + ".conf");

    private string Config(int node)
    {
        var store = Path.Combine(_workDir, NodeName(node) + "-js").Replace('\\', '/');
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"server_name: {NodeName(node)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"listen: 127.0.0.1:{_basePort + node}\n");
        sb.Append(CultureInfo.InvariantCulture, $"http: 127.0.0.1:{MonitorPort(node)}\n");
        sb.Append("max_payload: 1MB\n");
        sb.Append(CultureInfo.InvariantCulture, $"jetstream {{ store_dir: \"{store}\"{(_syncAlways ? ", sync_interval: always" : "")} }}\n");
        if (Size > 1)
        {
            var routes = string.Join(", ", Enumerable.Range(0, Size)
                .Where(i => i != node)
                .Select(i => string.Create(CultureInfo.InvariantCulture, $"nats-route://127.0.0.1:{ClusterPort(i)}")));
            sb.Append(CultureInfo.InvariantCulture, $"cluster {{ name: spike, listen: 127.0.0.1:{ClusterPort(node)}, routes: [{routes}] }}\n");
        }

        return sb.ToString();
    }
}
