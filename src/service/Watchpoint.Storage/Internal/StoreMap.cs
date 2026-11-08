using System.Net;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Validated settings: store → database definition → provider (spec STO §4, §8, §11).</summary>
internal sealed class StoreMap
{
    private readonly Dictionary<StoreKind, StoreBinding> _bindings;
    private readonly Dictionary<string, DatabaseDefinition> _databases;
    private readonly Dictionary<string, IStorageProvider> _providers;

    private StoreMap(Dictionary<StoreKind, StoreBinding> bindings, Dictionary<string, DatabaseDefinition> databases,
        Dictionary<string, IStorageProvider> providers, TimeSpan startupWait, IReadOnlyList<string> warnings)
    {
        _bindings = bindings;
        _databases = databases;
        _providers = providers;
        StartupWait = startupWait;
        Warnings = warnings;
    }

    public TimeSpan StartupWait { get; }

    /// <summary>Accepted but risky settings (logged at start-up), e.g. insecure remote connections allowed explicitly.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyCollection<StoreBinding> Bindings => _bindings.Values;

    /// <summary>Databases used by at least one store.</summary>
    public IEnumerable<DatabaseDefinition> UsedDatabases =>
        _bindings.Values.Select(b => b.DatabaseKey).Distinct(StringComparer.Ordinal).Select(k => _databases[k]);

    public StoreBinding Binding(StoreKind store) => _bindings[store];

    public DatabaseDefinition Database(StoreKind store) => _databases[_bindings[store].DatabaseKey];

    public IStorageProvider Provider(StoreKind store) => _providers[_bindings[store].ProviderId];

    public IStorageProvider ProviderOf(DatabaseDefinition db) => _providers[db.ProviderId];

    public static Result<StoreMap> Build(StorageSettings settings, IEnumerable<IStorageProvider> installed, bool isDevelopment)
    {
        var providers = installed.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
        var errors = new List<FieldError>();
        var warnings = new List<string>();
        var databases = new Dictionary<string, DatabaseDefinition>(StringComparer.Ordinal);

        foreach (var (key, s) in settings.Databases)
        {
            var path = $"{StorageSettings.Section}:Databases:{key}";
            if (!providers.ContainsKey(s.Provider))
            {
                return StorageErrors.ProviderUnknown(s.Provider);
            }

            if (s.PasswordDev is not null && !isDevelopment)
            {
                errors.Add(FieldError.Create($"{path}:PasswordDev", CommonErrorCodes.ValueNotAllowed, ("allowed", (IReadOnlyList<string>)["Development"])));
            }

            if (s.Port is < 1 or > 65535)
            {
                errors.Add(CommonErrors.OutOfRangeAt($"{path}:Port", 1, 65535));
            }

            if (s.MaxPoolSize is < 5 or > 500)
            {
                errors.Add(CommonErrors.OutOfRangeAt($"{path}:MaxPoolSize", 5, 500));
            }

            if (s.Tls == TlsMode.Disable && !(IsLocal(s.Host) && s.AdditionalHosts.All(h => IsLocal(HostPart(h)))))
            {
                if (settings.AllowInsecureRemote)
                {
                    warnings.Add($"Database {key}: TLS disabled for a remote host (Database:AllowInsecureRemote=true).");
                }
                else
                {
                    errors.Add(FieldError.Create($"{path}:Tls", CommonErrorCodes.ValueNotAllowed,
                        ("allowed", (IReadOnlyList<string>)[nameof(TlsMode.Prefer), nameof(TlsMode.Require), nameof(TlsMode.VerifyCa), nameof(TlsMode.VerifyFull)])));
                }
            }

            databases[key] = new DatabaseDefinition(key, s.Provider, s.Host, s.Port, s.Name, s.Username, s.PasswordRef, s.Tls,
                s.ExtraOptions, [.. s.AdditionalHosts], s.MaxPoolSize)
            { PasswordDev = s.PasswordDev };
        }

        var bindings = new Dictionary<StoreKind, StoreBinding>();
        foreach (var store in Enum.GetValues<StoreKind>())
        {
            var key = settings.Stores.KeyOf(store);
            if (databases.TryGetValue(key, out var db))
            {
                bindings[store] = new StoreBinding(store, db.ProviderId, key);
            }
            else
            {
                errors.Add(FieldError.Create($"{StorageSettings.Section}:Stores:{store}", CommonErrorCodes.ValueNotAllowed,
                    ("allowed", (IReadOnlyList<string>)[.. databases.Keys.Order(StringComparer.Ordinal)])));
            }
        }

        if (!StorageSettings.TryParseDuration(settings.StartupWait, out var startupWait))
        {
            errors.Add(CommonErrors.InvalidFormatAt($"{StorageSettings.Section}:StartupWait", "ISO 8601 duration, e.g. PT5M"));
        }

        return errors.Count > 0
            ? StorageErrors.SettingsInvalid(errors)
            : new StoreMap(bindings, databases, providers, startupWait, warnings);
    }

    /// <summary>Local host: loopback name or address, or a Unix socket directory.</summary>
    internal static bool IsLocal(string host) =>
        host.StartsWith('/')
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static string HostPart(string hostAndPort)
    {
        if (hostAndPort.StartsWith('['))
        {
            return hostAndPort[..(hostAndPort.IndexOf(']', StringComparison.Ordinal) + 1)];
        }

        var colon = hostAndPort.LastIndexOf(':');
        return colon > 0 && hostAndPort.IndexOf(':', StringComparison.Ordinal) == colon ? hostAndPort[..colon] : hostAndPort;
    }
}
