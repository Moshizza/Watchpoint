namespace Watchpoint.Hosting.Supervision;

/// <summary>Starts real processes with the platform behaviour of spec HST §7.3.</summary>
internal sealed class SystemProcessLauncher(ChildProcessJanitor janitor) : IProcessLauncher
{
    public IChildProcess Start(ProcessSpec spec)
    {
        if (!File.Exists(spec.Executable))
        {
            throw new FileNotFoundException("The executable does not exist.", spec.Executable);
        }

        return OperatingSystem.IsWindows() ? WindowsChildProcess.Start(spec, janitor) : UnixChildProcess.Start(spec);
    }
}
