using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>Where the master key lives (spec SEC §6.2).</summary>
internal interface IMasterKeyProvider
{
    string Name { get; }

    bool IsAvailable();

    /// <summary>Loads the key, creating it on first start where allowed; logs 2201/2202.</summary>
    /// <exception cref="SecretsException"><c>secrets.master_key.unavailable</c>.</exception>
    MasterKey LoadOrCreate();
}

/// <summary><c>file</c>: raw 32 bytes in <c>&lt;Data&gt;/secrets/master.key</c>, mode 0400 (default on Linux).</summary>
internal sealed class FileMasterKeyProvider(SecretsOptions options, ILogger logger) : IMasterKeyProvider
{
    public const string ProviderName = "file";
    private const UnixFileMode Allowed = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public string Name => ProviderName;

    public string KeyPath => Path.Join(options.SecretsDirectory, "master.key");

    public bool IsAvailable() => true;

    public MasterKey LoadOrCreate()
    {
        if (File.Exists(KeyPath))
        {
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(KeyPath) & ~Allowed) != 0)
            {
                SecretsLog.MasterKeyPermissionsTooWide(logger, KeyPath);
            }

            var bytes = File.ReadAllBytes(KeyPath);
            try
            {
                if (bytes.Length != MasterKey.Size)
                {
                    throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, $"{KeyPath} does not hold {MasterKey.Size} bytes"));
                }

                var key = new MasterKey(bytes);
                SecretsLog.MasterKeyLoaded(logger, key.KeyId, Name);
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var created = MasterKey.Generate();
        SecureFiles.WriteNew(KeyPath, created.Bytes, readOnly: true);
        SecretsLog.MasterKeyCreated(logger, created.KeyId, Name);
        return created;
    }
}

/// <summary><c>environment</c>: <c>WATCHPOINT_MASTER_KEY</c> (base64) or <c>WATCHPOINT_MASTER_KEY_FILE</c>; never created.</summary>
internal sealed class EnvironmentMasterKeyProvider(Func<string, string?> environment, ILogger logger) : IMasterKeyProvider
{
    public const string ProviderName = "environment";
    public const string KeyVariable = "WATCHPOINT_MASTER_KEY";
    public const string FileVariable = "WATCHPOINT_MASTER_KEY_FILE";

    public string Name => ProviderName;

    public bool IsAvailable() => environment(KeyVariable) is not null || environment(FileVariable) is not null;

    public MasterKey LoadOrCreate()
    {
        byte[] bytes;
        if (environment(KeyVariable) is { } base64)
        {
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, $"{KeyVariable} is not base64"));
            }
        }
        else if (environment(FileVariable) is { } file && File.Exists(file))
        {
            bytes = File.ReadAllBytes(file);
        }
        else
        {
            throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, $"{KeyVariable} or {FileVariable} is not set"));
        }

        try
        {
            if (bytes.Length != MasterKey.Size)
            {
                throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, $"the key has {bytes.Length} bytes, {MasterKey.Size} expected"));
            }

            var key = new MasterKey(bytes);
            SecretsLog.MasterKeyLoaded(logger, key.KeyId, Name);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary><c>dpapi</c>: the key protected by DPAPI (LocalMachine) in <c>&lt;Data&gt;/secrets/master.key.dpapi</c> (default on Windows).</summary>
internal sealed class DpapiMasterKeyProvider(SecretsOptions options, ILogger logger) : IMasterKeyProvider
{
    public const string ProviderName = "dpapi";

    public string Name => ProviderName;

    public string KeyPath => Path.Join(options.SecretsDirectory, "master.key.dpapi");

    public bool IsAvailable() => OperatingSystem.IsWindows();

    public MasterKey LoadOrCreate() =>
        OperatingSystem.IsWindows()
            ? LoadOrCreateWindows()
            : throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, "DPAPI exists on Windows only"));

    [SupportedOSPlatform("windows")]
    private MasterKey LoadOrCreateWindows()
    {
        var entropy = Encoding.UTF8.GetBytes("watchpoint-master|" + options.NodeId);
        if (File.Exists(KeyPath))
        {
            byte[] bytes;
            try
            {
                bytes = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath), entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException ex)
            {
                throw new SecretsException(SecretsErrors.MasterKeyUnavailable(Name, "DPAPI could not unprotect the key"), ex);
            }

            try
            {
                var key = new MasterKey(bytes);
                SecretsLog.MasterKeyLoaded(logger, key.KeyId, Name);
                return key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var created = MasterKey.Generate();
        SecureFiles.WriteNew(KeyPath, ProtectedData.Protect(created.Bytes.ToArray(), entropy, DataProtectionScope.LocalMachine), readOnly: true);
        SecretsLog.MasterKeyCreated(logger, created.KeyId, Name);
        return created;
    }
}

/// <summary>Files readable by the service account only.</summary>
internal static class SecureFiles
{
    public static void EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path); // ACL set by the installer (DEP)
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void WriteNew(string path, ReadOnlySpan<byte> content, bool readOnly)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = readOnly ? UnixFileMode.UserRead : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }
}
