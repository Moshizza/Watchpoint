using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// Keeps children from outliving the host (spec HST §7.3): pid files in <c>&lt;Data&gt;/run</c>, cleanup of the
/// processes left by a host that died, and on Windows the Job Object that kills every child when the host goes.
/// </summary>
public sealed class ChildProcessJanitor : IDisposable
{
    private const string PidFileExtension = ".pid";

    private readonly string _runDirectory;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IProcessInspector _inspector;
    private readonly TimeSpan _killTimeout;
    private readonly Lock _gate = new();
    private WindowsJobObject? _job;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ChildProcessJanitor"/> class.</summary>
    /// <param name="runDirectory">The folder of the pid files (<c>&lt;Data&gt;/run</c>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The time provider; the system one when <see langword="null"/>.</param>
    public ChildProcessJanitor(string runDirectory, ILogger<ChildProcessJanitor> logger, TimeProvider? timeProvider = null)
        : this(runDirectory, logger, timeProvider ?? TimeProvider.System, new SystemProcessInspector(), TimeSpan.FromSeconds(5))
    {
    }

    internal ChildProcessJanitor(string runDirectory, ILogger logger, TimeProvider timeProvider, IProcessInspector inspector, TimeSpan killTimeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(runDirectory);
        _runDirectory = runDirectory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider;
        _inspector = inspector;
        _killTimeout = killTimeout;
    }

    /// <summary>Returns the pid file path of a child.</summary>
    /// <param name="name">The child name.</param>
    /// <returns><c>&lt;run&gt;/&lt;name&gt;.pid</c>.</returns>
    public string PidFilePath(string name) => Path.Combine(_runDirectory, name + PidFileExtension);

    /// <summary>Kills the processes left by a previous host for every pid file in the run folder, and removes the files.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of processes killed.</returns>
    public async Task<int> CleanupAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_runDirectory))
        {
            return 0;
        }

        var killed = 0;
        foreach (var file in Directory.EnumerateFiles(_runDirectory, "*" + PidFileExtension))
        {
            if (await CleanupFileAsync(Path.GetFileNameWithoutExtension(file), file, ct).ConfigureAwait(false))
            {
                killed++;
            }
        }

        return killed;
    }

    /// <summary>Kills the process left by a previous host for the child <paramref name="name"/>, if any, and removes its pid file.</summary>
    /// <param name="name">The child name.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when a leftover process was killed.</returns>
    public Task<bool> CleanupAsync(string name, CancellationToken ct) => CleanupFileAsync(name, PidFilePath(name), ct);

    /// <summary>Writes the pid file of a started child: the pid on the first line, the executable path on the second.</summary>
    internal void WritePidFile(string name, int pid, string executable)
    {
        try
        {
            EnsureRunDirectory();
            var path = PidFilePath(name);
            var temp = path + ".tmp";
            File.WriteAllText(temp, string.Create(CultureInfo.InvariantCulture, $"{pid}\n{executable}\n"));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file a leftover cannot be found after a host crash; the child itself is fine.
            HostingLog.LogPidFileFailed(_logger, name, ex);
        }
    }

    internal void RemovePidFile(string name)
    {
        try
        {
            File.Delete(PidFilePath(name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostingLog.LogPidFileFailed(_logger, name, ex);
        }
    }

    /// <summary>Windows: puts the process in the host's kill-on-close Job Object. No effect elsewhere.</summary>
    internal void AssignToJob(nint processHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _job ??= WindowsJobObject.CreateKillOnClose();
            _job.Assign(processHandle);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Closing the last handle of the job kills whatever is still in it.
            if (OperatingSystem.IsWindows())
            {
                _job?.Dispose();
            }

            _job = null;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveLinks(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private void EnsureRunDirectory()
    {
        if (Directory.Exists(_runDirectory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_runDirectory);
        }
        else
        {
            Directory.CreateDirectory(_runDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<bool> CleanupFileAsync(string name, string file, CancellationToken ct)
    {
        string[] lines;
        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HostingLog.LogPidFileFailed(_logger, name, ex);
            return false;
        }

        var killed = false;
        if (lines.Length >= 2
            && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            && pid > 0
            && pid != Environment.ProcessId
            && _inspector.GetExecutablePath(pid) is { } running
            && (SamePath(running, lines[1]) || SamePath(ResolveLinks(running), ResolveLinks(lines[1]))))
        {
            // The pid may have been reused by another program: only a process running the recorded executable is killed.
            _inspector.Kill(pid);
            killed = await WaitGoneAsync(pid, ct).ConfigureAwait(false);
            HostingLog.LogChildKilledLeftover(_logger, pid, name);
        }

        RemovePidFile(name);
        return killed;
    }

    private async Task<bool> WaitGoneAsync(int pid, CancellationToken ct)
    {
        var started = _timeProvider.GetTimestamp();
        while (_inspector.IsAlive(pid))
        {
            if (_timeProvider.GetElapsedTime(started) >= _killTimeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, ct).ConfigureAwait(false);
        }

        return true;
    }
}
