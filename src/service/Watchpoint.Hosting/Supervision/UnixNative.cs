using System.Runtime.InteropServices;

namespace Watchpoint.Hosting.Supervision;

/// <summary>libc calls used to start children in their own process group (Linux, macOS) and to signal them.</summary>
internal static class UnixNative
{
#pragma warning disable SA1310, CA1707 // POSIX constant names
    public const int SIGINT = 2;
    public const int SIGKILL = 9;
    public const int SIGPIPE = 13;
    public const int SIGTERM = 15;
    public const int EINTR = 4;
    public const int ECHILD = 10;
    public const short POSIX_SPAWN_SETPGROUP = 0x02;
    public const short POSIX_SPAWN_SETSIGDEF = 0x04;
    public const short POSIX_SPAWN_SETSIGMASK = 0x08;
#pragma warning restore SA1310, CA1707

    private const string Libc = "libc";

    [DllImport(Libc, EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int Kill(int pid, int signal);

    [DllImport(Libc, EntryPoint = "waitpid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int WaitPid(int pid, out int status, int options);

    [DllImport(Libc, EntryPoint = "posix_spawn")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int PosixSpawn(out int pid, nint path, nint fileActions, nint attributes, nint argv, nint envp);

    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int FileActionsInit(nint fileActions);

    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int FileActionsDestroy(nint fileActions);

    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_adddup2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int FileActionsAddDup2(nint fileActions, int fd, int newFd);

    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_addopen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int FileActionsAddOpen(nint fileActions, int fd, nint path, int flags, uint mode);

    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int FileActionsAddChdir(nint fileActions, nint path);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrInit(nint attributes);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrDestroy(nint attributes);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_setflags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrSetFlags(nint attributes, short flags);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_setpgroup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrSetProcessGroup(nint attributes, int processGroup);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_setsigdefault")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrSetSigDefault(nint attributes, nint signalSet);

    [DllImport(Libc, EntryPoint = "posix_spawnattr_setsigmask")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int AttrSetSigMask(nint attributes, nint signalSet);

    [DllImport(Libc, EntryPoint = "sigemptyset")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int SigEmptySet(nint signalSet);

    [DllImport(Libc, EntryPoint = "sigaddset")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static extern int SigAddSet(nint signalSet, int signal);
}
