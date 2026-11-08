using System.Runtime.InteropServices;

namespace Watchpoint.Hosting;

/// <summary>
/// Resolved filesystem paths for data, logs, packages, and binaries.
/// </summary>
public sealed class NodePaths
{
    /// <summary>Gets the data folder path.</summary>
    public string Data { get; }

    /// <summary>Gets the logs folder path.</summary>
    public string Logs { get; }

    /// <summary>Gets the integrations/packages folder path.</summary>
    public string Packages { get; }

    /// <summary>Gets the binaries folder path.</summary>
    public string Binaries { get; }

    /// <summary>Initializes a new instance of the <see cref="NodePaths"/> class.</summary>
    /// <param name="data">Data path.</param>
    /// <param name="logs">Logs path.</param>
    /// <param name="packages">Packages path.</param>
    /// <param name="binaries">Binaries path.</param>
    public NodePaths(string data, string logs, string packages, string binaries)
    {
        Data = data;
        Logs = logs;
        Packages = packages;
        Binaries = binaries;
    }

    /// <summary>Resolves default paths based on the operating system.</summary>
    /// <param name="pathsConfig">Optional paths configuration from settings.</param>
    /// <param name="isWindows">Optional OS check override for testing.</param>
    /// <param name="baseDir">Optional base directory for binaries.</param>
    /// <returns>A new <see cref="NodePaths"/> instance.</returns>
    public static NodePaths Resolve(PathsSection? pathsConfig = null, bool? isWindows = null, string? baseDir = null)
    {
        var windows = isWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        baseDir ??= AppContext.BaseDirectory;

        string defaultData = windows ? @"C:\ProgramData\Watchpoint" : "/var/lib/watchpoint";
        string defaultLogs = windows ? @"C:\ProgramData\Watchpoint\logs" : "/var/log/watchpoint";

        var data = !string.IsNullOrWhiteSpace(pathsConfig?.Data) ? pathsConfig.Data : defaultData;
        var logs = !string.IsNullOrWhiteSpace(pathsConfig?.Logs)
            ? pathsConfig.Logs
            : (windows ? (data == defaultData ? defaultLogs : Path.Combine(data, "logs")) : "/var/log/watchpoint");
        var packages = !string.IsNullOrWhiteSpace(pathsConfig?.Packages)
            ? pathsConfig.Packages
            : (windows ? Path.Combine(data, "integrations").Replace('/', '\\') : Path.Combine(data, "integrations").Replace('\\', '/'));
        var binaries = !string.IsNullOrWhiteSpace(pathsConfig?.Binaries)
            ? pathsConfig.Binaries
            : (windows ? Path.Combine(baseDir, "bin").Replace('/', '\\') : Path.Combine(baseDir, "bin").Replace('\\', '/'));

        return new NodePaths(data, logs, packages, binaries);
    }

    /// <summary>Ensures required folders exist with restricted permissions.</summary>
    public void EnsureDirectories()
    {
        EnsureDirectory(Data);
        EnsureDirectory(Logs);
        EnsureDirectory(Packages);
    }

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(path);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // Linux mode 0750 (owner rwx, group rx, other none)
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            catch
            {
                // Ignore if filesystem does not support unix permissions
            }
        }
    }
}
