using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Watchpoint.Hosting.Supervision;

/// <summary>Looks at and kills processes that are not children of this host (leftover cleanup).</summary>
internal interface IProcessInspector
{
    /// <summary>The executable path of a live process, or <see langword="null"/> when it does not exist or cannot be read.</summary>
    string? GetExecutablePath(int pid);

    bool IsAlive(int pid);

    /// <summary>Kills the process (and on Unix its process group when it leads one).</summary>
    void Kill(int pid);
}

internal sealed class SystemProcessInspector : IProcessInspector
{
    public string? GetExecutablePath(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return IsAlive(pid) ? File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: false)?.FullName : null;
            }

            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsAlive(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // A zombie (state Z) has exited and only waits to be reaped.
                return ReadStat(pid) is { } stat && stat.State != 'Z' && stat.State != 'X';
            }

            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public void Kill(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            return;
        }

        if (OperatingSystem.IsLinux() && ReadStat(pid) is { } stat && stat.ProcessGroup == pid)
        {
            // Children are started as process group leaders: take the descendants too.
            _ = UnixNative.Kill(-pid, UnixNative.SIGKILL);
        }

        _ = UnixNative.Kill(pid, UnixNative.SIGKILL);
    }

    private static (char State, int ProcessGroup)? ReadStat(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // "pid (comm) state ppid pgrp ..."; comm may contain spaces and parentheses, so start after the last ')'.
        var end = stat.LastIndexOf(')');
        var fields = end < 0 ? [] : stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            ? (fields[0][0], group)
            : null;
    }
}
