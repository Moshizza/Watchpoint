using System.Xml;
using Microsoft.Extensions.Configuration;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Settings section <c>Database</c> (spec STO §8), bound from configuration.</summary>
internal sealed class StorageSettings
{
    public const string Section = "Database";
    public const string DefaultKey = "default";

    public Dictionary<string, DatabaseSettings> Databases { get; } = new(StringComparer.Ordinal);

    public StoreSettings Stores { get; set; } = new();

    /// <summary>ISO 8601 duration, default <c>PT5M</c>.</summary>
    public string StartupWait { get; set; } = "PT5M";

    public bool AllowInsecureRemote { get; set; }

    public static StorageSettings Bind(IConfiguration configuration)
    {
        var settings = new StorageSettings();
        configuration.GetSection(Section).Bind(settings);
        if (settings.Databases.Count == 0)
        {
            settings.Databases[DefaultKey] = new DatabaseSettings();
        }

        return settings;
    }

    public static bool TryParseDuration(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('P'))
        {
            return false;
        }

        try
        {
            value = XmlConvert.ToTimeSpan(text);
            return value > TimeSpan.Zero;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class DatabaseSettings
{
    public string Provider { get; set; } = "postgres";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string Name { get; set; } = "watchpoint";

    public string Username { get; set; } = "watchpoint";

    public string? PasswordRef { get; set; } = "bootstrap:database.default";

    public string? PasswordDev { get; set; }

    public TlsMode Tls { get; set; } = TlsMode.Prefer;

    public string? ExtraOptions { get; set; }

    public List<string> AdditionalHosts { get; } = [];

    public int MaxPoolSize { get; set; } = 50;
}

internal sealed class StoreSettings
{
    public string Configuration { get; set; } = StorageSettings.DefaultKey;

    public string State { get; set; } = StorageSettings.DefaultKey;

    public string History { get; set; } = StorageSettings.DefaultKey;

    public string Audit { get; set; } = StorageSettings.DefaultKey;

    public string KeyOf(StoreKind store) => store switch
    {
        StoreKind.Configuration => Configuration,
        StoreKind.State => State,
        StoreKind.History => History,
        StoreKind.Audit => Audit,
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };
}
