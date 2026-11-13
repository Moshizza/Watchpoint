using System.Collections;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// A child started with <c>posix_spawn</c> as the leader of a new process group (spec HST §7.3):
/// <see cref="System.Diagnostics.Process"/> cannot start a process in its own group. stdin is <c>/dev/null</c>,
/// stdout and stderr are pipes; a dedicated thread waits for the exit and reaps the process.
/// </summary>
internal sealed class UnixChildProcess : IChildProcess
{
    // Opaque libc structures (posix_spawnattr_t, posix_spawn_file_actions_t, sigset_t) are at most 336 bytes on
    // glibc, musl and macOS; a generous zeroed block avoids depending on their layout.
    private const int OpaqueSize = 1024;
    private const int ReadOnly = 0;

    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ChildExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AnonymousPipeServerStream _stdout;
    private readonly AnonymousPipeServerStream _stderr;
    private bool _reaped;

    private UnixChildProcess(int pid, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr)
    {
        Pid = pid;
        _stdout = stdout;
        _stderr = stderr;
        var waiter = new Thread(WaitForExit) { IsBackground = true, Name = "wp-child-wait-" + pid };
        waiter.Start();
    }

    public int Pid { get; }

    public Stream? StandardOutput => _stdout;

    public Stream? StandardError => _stderr;

    public Task<ChildExit> Exited => _exited.Task;

    public static UnixChildProcess Start(ProcessSpec spec)
    {
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var allocations = new List<nint>();
        nint fileActions = 0;
        nint attributes = 0;
        try
        {
            var path = AllocString(spec.Executable, allocations);
            var argv = AllocStringArray([spec.Executable, .. spec.Arguments], allocations);
            var envp = AllocStringArray(BuildEnvironment(spec), allocations);
            var signals = AllocZeroed(allocations);
            var noSignals = AllocZeroed(allocations);

            fileActions = AllocZeroed(allocations);
            Check(UnixNative.FileActionsInit(fileActions), "posix_spawn_file_actions_init");
            Check(UnixNative.FileActionsAddOpen(fileActions, 0, AllocString("/dev/null", allocations), ReadOnly, 0), "addopen");
            Check(UnixNative.FileActionsAddDup2(fileActions, (int)stdout.ClientSafePipeHandle.DangerousGetHandle(), 1), "adddup2");
            Check(UnixNative.FileActionsAddDup2(fileActions, (int)stderr.ClientSafePipeHandle.DangerousGetHandle(), 2), "adddup2");
            if (!string.IsNullOrEmpty(spec.WorkingDirectory))
            {
                Check(UnixNative.FileActionsAddChdir(fileActions, AllocString(spec.WorkingDirectory, allocations)), "addchdir");
            }

            // New process group (pgid = child pid); default handling for the signals the .NET runtime ignores or
            // handles, and nothing blocked, whatever thread spawns.
            Check(UnixNative.SigEmptySet(signals), "sigemptyset");
            foreach (var signal in (int[])[UnixNative.SIGINT, UnixNative.SIGPIPE, UnixNative.SIGTERM])
            {
                Check(UnixNative.SigAddSet(signals, signal), "sigaddset");
            }

            Check(UnixNative.SigEmptySet(noSignals), "sigemptyset");
            attributes = AllocZeroed(allocations);
            Check(UnixNative.AttrInit(attributes), "posix_spawnattr_init");
            Check(UnixNative.AttrSetProcessGroup(attributes, 0), "setpgroup");
            Check(UnixNative.AttrSetSigDefault(attributes, signals), "setsigdefault");
            Check(UnixNative.AttrSetSigMask(attributes, noSignals), "setsigmask");
            Check(
                UnixNative.AttrSetFlags(
                    attributes,
                    UnixNative.POSIX_SPAWN_SETPGROUP | UnixNative.POSIX_SPAWN_SETSIGDEF | UnixNative.POSIX_SPAWN_SETSIGMASK),
                "setflags");

            var error = UnixNative.PosixSpawn(out var pid, path, fileActions, attributes, argv, envp);
            if (error != 0)
            {
                throw new Win32Exception(error);
            }

            // The child has its copies of the write ends; without ours the readers see the end when it exits.
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new UnixChildProcess(pid, stdout, stderr);
        }
        catch
        {
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
        finally
        {
            if (attributes != 0)
            {
                _ = UnixNative.AttrDestroy(attributes);
            }

            if (fileActions != 0)
            {
                _ = UnixNative.FileActionsDestroy(fileActions);
            }

            foreach (var allocation in allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }
    }

    public bool RequestGracefulStop() => Signal(UnixNative.SIGTERM);

    public void Kill() => Signal(UnixNative.SIGKILL);

    public ValueTask DisposeAsync()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void Check(int error, string call)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, call + " failed");
        }
    }

    private static nint AllocZeroed(List<nint> allocations)
    {
        var block = Marshal.AllocHGlobal(OpaqueSize);
        allocations.Add(block);
        Marshal.Copy(new byte[OpaqueSize], 0, block, OpaqueSize);
        return block;
    }

    private static nint AllocString(string value, List<nint> allocations)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var block = Marshal.AllocHGlobal(bytes.Length + 1);
        allocations.Add(block);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        Marshal.WriteByte(block, bytes.Length, 0);
        return block;
    }

    private static nint AllocStringArray(List<string> values, List<nint> allocations)
    {
        var block = Marshal.AllocHGlobal((values.Count + 1) * nint.Size);
        allocations.Add(block);
        for (var i = 0; i < values.Count; i++)
        {
            Marshal.WriteIntPtr(block, i * nint.Size, AllocString(values[i], allocations));
        }

        Marshal.WriteIntPtr(block, values.Count * nint.Size, 0);
        return block;
    }

    private static List<string> BuildEnvironment(ProcessSpec spec)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                variables[key] = value;
            }
        }

        foreach (var (key, value) in spec.Environment)
        {
            variables[key] = value;
        }

        return [.. variables.Select(v => v.Key + "=" + v.Value)];
    }

    /// <summary>Signals the whole process group while the leader is not reaped (its pid cannot be reused before).</summary>
    private bool Signal(int signal)
    {
        lock (_gate)
        {
            return !_reaped && UnixNative.Kill(-Pid, signal) == 0;
        }
    }

    private void WaitForExit()
    {
        ChildExit exit;
        while (true)
        {
            var result = UnixNative.WaitPid(Pid, out var status, 0);
            if (result == Pid)
            {
                // WIFEXITED: low 7 bits are 0, the exit code is in bits 8–15; otherwise the low 7 bits are the signal.
                var signal = status & 0x7f;
                exit = signal == 0 ? new ChildExit((status >> 8) & 0xff) : new ChildExit(128 + signal, signal);
                break;
            }

            if (Marshal.GetLastPInvokeError() != UnixNative.EINTR)
            {
                // ECHILD: something else reaped it (SIGCHLD ignored by the host's parent); the exit code is lost.
                exit = new ChildExit(null);
                break;
            }
        }

        lock (_gate)
        {
            // Descendants that stayed in the group would keep the pipes open and survive a restart.
            _ = UnixNative.Kill(-Pid, UnixNative.SIGKILL);
            _reaped = true;
        }

        _exited.TrySetResult(exit);
    }
}
