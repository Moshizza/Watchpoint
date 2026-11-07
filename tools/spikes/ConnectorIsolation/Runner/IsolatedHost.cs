using System.Diagnostics;
using System.Globalization;

namespace Watchpoint.Spikes.ConnectorIsolation.Runner;

/// <summary>A started host process and its session (the supervisor of IHO §6.2, reduced to start/kill).</summary>
internal sealed class IsolatedHost : IAsyncDisposable
{
    private IsolatedHost(Process process, HostSession session, double startToReadyMs)
    {
        Process = process;
        Session = session;
        StartToReadyMs = startToReadyMs;
    }

    public Process Process { get; }

    public HostSession Session { get; }

    /// <summary>Process start → Hello received (includes runtime start-up, gRPC connect and connector load).</summary>
    public double StartToReadyMs { get; }

    public static async Task<IsolatedHost> StartAsync(HostLinkServer server, string batching, bool invariantGlobalization = false)
    {
        var info = new ProcessStartInfo(Layout.HostExecutable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(server.Socket);
        info.ArgumentList.Add("--connector");
        info.ArgumentList.Add(Layout.ConnectorAssembly);
        info.ArgumentList.Add("--batching");
        info.ArgumentList.Add(batching);
        if (invariantGlobalization)
        {
            info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        }

        var start = Stopwatch.GetTimestamp();
        var process = Process.Start(info) ?? throw new InvalidOperationException("host did not start");
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); // IHO §6.2 readiness
        var session = await server.Sessions.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        return new IsolatedHost(process, session, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>Resident set size of the host process, in MB (Linux /proc).</summary>
    public double ResidentMb() => ReadResidentMb(Process.Id);

    public static double ReadResidentMb(int pid)
    {
        var line = File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
        var kb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        return kb / 1024.0;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().ConfigureAwait(false);
        }

        Process.Dispose();
    }
}
