using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// A child on Windows (spec HST §7.3): started with <c>CREATE_NEW_PROCESS_GROUP</c> so that it can receive
/// <c>CTRL_BREAK_EVENT</c>, and assigned to the host's kill-on-close Job Object.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsChildProcess : IChildProcess
{
    private readonly Process _process;

    private WindowsChildProcess(Process process)
    {
        _process = process;
        Pid = process.Id;
        Exited = WaitAsync(process);
    }

    public int Pid { get; }

    public Stream? StandardOutput => _process.StandardOutput.BaseStream;

    public Stream? StandardError => _process.StandardError.BaseStream;

    public Task<ChildExit> Exited { get; }

    public static WindowsChildProcess Start(ProcessSpec spec, ChildProcessJanitor janitor)
    {
        var info = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // The child shares the host's console (when it has one): CTRL_BREAK_EVENT only reaches a shared console.
            CreateNoWindow = false,
            CreateNewProcessGroup = true,
        };
        foreach (var argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in spec.Environment)
        {
            info.Environment[key] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException($"Process '{spec.Name}' could not be started.");
        try
        {
            // From here the child dies with the host. (A child cannot be created inside the job without calling
            // CreateProcess directly; the window between start and assignment is a few instructions.)
            janitor.AssignToJob(process.Handle);
            process.StandardInput.Close();
            return new WindowsChildProcess(process);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    public bool RequestGracefulStop() => !_process.HasExited && WindowsJobObject.SendCtrlBreak(Pid);

    public void Kill() => TryKill(_process);

    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static async Task<ChildExit> WaitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ChildExit(process.ExitCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new ChildExit(null);
        }
    }
}
