using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Watchpoint.Hosting;
using Watchpoint.Hosting.Supervision;

namespace Watchpoint.Host.Nats;

/// <summary>
/// Bundled NATS (spec HST §7.4): locates and checks the <c>nats-server</c> binary, keeps the authentication token,
/// writes <c>nats.conf</c> at every start and builds the <see cref="ProcessSpec"/> for the supervisor.
/// </summary>
public sealed partial class NatsProcessSpecFactory
{
    /// <summary>The name of the supervised process.</summary>
    public const string ProcessName = "nats";

    /// <summary>The logger category of the server's output.</summary>
    public const string LogCategory = "Watchpoint.Child.Nats";

    private const long MinFileStoreBytes = 10L * 1024 * 1024 * 1024;
    private const int TokenBytes = 32;

    private static readonly TimeSpan _versionTimeout = TimeSpan.FromSeconds(10);

    private readonly NodeSettings _settings;
    private readonly NodePaths _paths;
    private readonly Func<string, long> _freeBytes;

    /// <summary>Initializes a new instance of the <see cref="NatsProcessSpecFactory"/> class.</summary>
    /// <param name="settings">The node settings.</param>
    /// <param name="paths">The node paths.</param>
    /// <param name="freeBytes">Returns the free bytes of the disk holding a folder; the real disk when <see langword="null"/>.</param>
    public NatsProcessSpecFactory(NodeSettings settings, NodePaths paths, Func<string, long>? freeBytes = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _freeBytes = freeBytes ?? (folder => new DriveInfo(folder).AvailableFreeSpace);
    }

    /// <summary>Gets the oldest supported server version (ADR-001: atomic batches, expected-sequence subject header).</summary>
    public static Version MinimumVersion { get; } = new(2, 12, 0);

    /// <summary>Gets the path of the server binary: <c>&lt;Paths:Binaries&gt;/nats-server(.exe)</c>.</summary>
    public string ExecutablePath => Path.Combine(_paths.Binaries, OperatingSystem.IsWindows() ? "nats-server.exe" : "nats-server");

    /// <summary>Gets the folder of the server's files: <c>Nats:StorePath</c>, default <c>&lt;Data&gt;/nats</c>.</summary>
    public string StorePath => string.IsNullOrWhiteSpace(_settings.Nats.StorePath) ? Path.Combine(_paths.Data, "nats") : _settings.Nats.StorePath;

    /// <summary>Gets the path of the generated configuration file.</summary>
    public string ConfigPath => Path.Combine(StorePath, "nats.conf");

    /// <summary>Gets the path of the token file.</summary>
    public string TokenPath => Path.Combine(StorePath, "token");

    /// <summary>Parses the output of <c>nats-server --version</c> (<c>nats-server: v2.15.2</c>).</summary>
    /// <param name="output">The output.</param>
    /// <returns>The version, or <see langword="null"/> when the output has none.</returns>
    public static Version? ParseVersion(string? output)
    {
        var match = VersionPattern().Match(output ?? string.Empty);
        return match.Success
            ? new Version(
                int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Checks a <c>--version</c> output against <see cref="MinimumVersion"/>.</summary>
    /// <param name="output">The output of <c>nats-server --version</c>.</param>
    /// <returns>The version.</returns>
    /// <exception cref="SupervisorException">The version is missing or too old (<c>host.nats.version_unsupported</c>).</exception>
    public static Version EnsureSupported(string? output)
    {
        var version = ParseVersion(output);
        if (version is null || version < MinimumVersion)
        {
            var found = version?.ToString() ?? "unknown";
            throw new SupervisorException(
                HostErrors.NatsVersionUnsupported(found, MinimumVersion.ToString()),
                $"nats-server {found} is not supported: version {MinimumVersion} or later is required.");
        }

        return version;
    }

    /// <summary>Runs <c>nats-server --version</c> and checks the version.</summary>
    /// <param name="timeProvider">The time provider of the 10 s limit.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The version of the binary.</returns>
    /// <exception cref="SupervisorException">The binary is missing, or its version is unknown or too old.</exception>
    public async Task<Version> ReadVersionAsync(TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!File.Exists(ExecutablePath))
        {
            throw new SupervisorException(
                HostErrors.ExecutableMissing(ProcessName, ExecutablePath), $"Bundled NATS server not found: {ExecutablePath}");
        }

        using var timeout = new CancellationTokenSource(_versionTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        string output;
        try
        {
            process.Start();

            // Both streams are read to the end, so the process cannot block on a full pipe.
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            TryKill(process);
            output = string.Empty;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return EnsureSupported(output);
    }

    /// <summary>Returns the token of the bundled server, creating the owner-only token file at the first start.</summary>
    /// <returns>The token: 32 random bytes, base64url.</returns>
    public string EnsureToken()
    {
        EnsureStoreDirectory();
        if (File.Exists(TokenPath))
        {
            var existing = File.ReadAllText(TokenPath).Trim();
            if (TokenPattern().IsMatch(existing))
            {
                return existing;
            }
        }

        // 43 characters of [A-Za-z0-9_-]: safe inside a quoted configuration string.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        WriteOwnerOnly(TokenPath, token);
        return token;
    }

    /// <summary>Builds the content of <c>nats.conf</c>.</summary>
    /// <param name="token">The authentication token.</param>
    /// <returns>The configuration text.</returns>
    public string BuildConfig(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var store = Path.Combine(StorePath, "store");
        var free = Directory.Exists(StorePath) ? _freeBytes(StorePath) : 0;
        var maxFileStore = Math.Max(free / 5, MinFileStoreBytes);
        var config = new StringBuilder();
        config.Append("# Generated by Watchpoint at every start; changes are overwritten.\n");
        config.Append(CultureInfo.InvariantCulture, $"listen: 127.0.0.1:{_settings.Nats.Port}\n");
        config.Append(CultureInfo.InvariantCulture, $"server_name: \"{_settings.Node.Id}\"\n");
        config.Append("jetstream {\n");
        config.Append(CultureInfo.InvariantCulture, $"  store_dir: \"{Quote(store)}\"\n");
        config.Append("  max_memory_store: 256MB\n");
        config.Append(CultureInfo.InvariantCulture, $"  max_file_store: {maxFileStore}\n");
        config.Append("}\n");
        config.Append("authorization {\n");
        config.Append(CultureInfo.InvariantCulture, $"  token: \"{Quote(token)}\"\n");
        config.Append("}\n");
        config.Append("max_payload: 4MB\n");
        return config.ToString();
    }

    /// <summary>Writes <c>nats.conf</c> (owner-only: it holds the token) and returns the process to supervise.</summary>
    /// <returns>The process specification of the bundled server.</returns>
    public ProcessSpec Create()
    {
        var token = EnsureToken();
        WriteOwnerOnly(ConfigPath, BuildConfig(token));
        return new ProcessSpec
        {
            Name = ProcessName,
            Executable = Path.GetFullPath(ExecutablePath),
            Arguments = ["-c", ConfigPath],
            WorkingDirectory = StorePath,
            Readiness = new ReadinessProbe.Tcp("127.0.0.1", _settings.Nats.Port, TimeSpan.FromSeconds(30)),
            LogCategory = LogCategory,

            // nats-server writes its whole log to stderr.
            StandardErrorLevel = LogLevel.Information,
        };
    }

    /// <summary>Returns where clients connect: the bundled server, or the External server from the settings.</summary>
    /// <returns>The endpoint.</returns>
    public NatsEndpoint Endpoint() => _settings.Nats.Mode == NatsMode.Bundled
        ? new NatsEndpoint(string.Create(CultureInfo.InvariantCulture, $"nats://127.0.0.1:{_settings.Nats.Port}"), EnsureToken())
        : new NatsEndpoint(_settings.Nats.Url, string.IsNullOrEmpty(_settings.Nats.Token) ? null : _settings.Nats.Token);

    [GeneratedRegex(@"v(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Forward slashes (accepted by nats-server on Windows) and escaped quotes: a backslash starts an escape in a quoted string.</summary>
    private static string Quote(string value) => value.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Not started or already gone.
        }
    }

    /// <summary>Writes a file that only the service account can read; replaced atomically. Windows: the ACL of the data folder applies.</summary>
    private static void WriteOwnerOnly(string path, string content)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(temp);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private void EnsureStoreDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(StorePath);
        }
        else
        {
            Directory.CreateDirectory(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
