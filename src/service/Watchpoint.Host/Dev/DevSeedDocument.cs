using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Host.Dev;

/// <summary>The development seed (spec HST §9, <c>schemas/dev-seed.schema.json</c>), read by <see cref="DevSeedReader"/>.</summary>
/// <param name="Site">The site.</param>
/// <param name="Locations">The locations, parents before children.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Users">The users.</param>
/// <param name="Connections">The integration connections.</param>
public sealed record DevSeedDocument(
    DevSeedSite Site,
    IReadOnlyList<DevSeedLocation> Locations,
    IReadOnlyList<DevSeedTag> Tags,
    IReadOnlyList<DevSeedUser> Users,
    IReadOnlyList<DevSeedConnection> Connections)
{
    /// <summary>The sections in the order they are applied.</summary>
    public static readonly IReadOnlyList<string> Sections = ["site", "locations", "tags", "users", "connections"];

    /// <summary>Returns whether the document has something for <paramref name="section"/>.</summary>
    /// <param name="section">A name from <see cref="Sections"/>.</param>
    /// <returns><see langword="true"/> when the section is not empty.</returns>
    public bool Has(string section) => section switch
    {
        "site" => true,
        "locations" => Locations.Count > 0,
        "tags" => Tags.Count > 0,
        "users" => Users.Count > 0,
        "connections" => Connections.Count > 0,
        _ => false,
    };
}

/// <summary>The site of the seed.</summary>
/// <param name="Name">The site name.</param>
/// <param name="TimeZone">The IANA time zone.</param>
public sealed record DevSeedSite(string Name, string TimeZone);

/// <summary>A location of the seed.</summary>
/// <param name="Key">The key other entries refer to.</param>
/// <param name="Parent">The key of the parent location, or <see langword="null"/> for the root.</param>
/// <param name="Name">The name.</param>
/// <param name="Kind">The kind: <c>site</c>, <c>building</c>, <c>floor</c>, <c>zone</c>, <c>room</c> or <c>outdoor</c>.</param>
public sealed record DevSeedLocation(string Key, string? Parent, string Name, string Kind);

/// <summary>A tag of the seed.</summary>
/// <param name="Family">The key of the tag family.</param>
/// <param name="Key">The key of the tag.</param>
/// <param name="Name">The name.</param>
public sealed record DevSeedTag(string Family, string Key, string Name);

/// <summary>A user of the seed.</summary>
/// <param name="Login">The login.</param>
/// <param name="Password">The initial password (development only).</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Administrator">Whether the user is an administrator.</param>
public sealed record DevSeedUser(string Login, string Password, string DisplayName, bool Administrator)
{
    /// <summary>Returns the login only: the password must never reach a log.</summary>
    /// <returns>The login.</returns>
    public override string ToString() => Login;
}

/// <summary>An integration connection of the seed.</summary>
/// <param name="Package">The integration package id.</param>
/// <param name="ServerType">The server type of the package.</param>
/// <param name="Name">The connection name.</param>
/// <param name="Location">The key of the location, or <see langword="null"/>.</param>
/// <param name="Settings">The connection settings.</param>
/// <param name="Secrets">The connection secrets (development only).</param>
/// <param name="AutoImport">Whether discovery runs and every device is imported.</param>
public sealed record DevSeedConnection(
    string Package,
    string ServerType,
    string Name,
    string? Location,
    JsonObject Settings,
    IReadOnlyDictionary<string, string> Secrets,
    bool AutoImport)
{
    /// <summary>Returns the name only: secrets must never reach a log.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => Name;

    internal static DevSeedConnection From(JsonElement element) => new(
        element.GetProperty("package").GetString()!,
        element.GetProperty("serverType").GetString()!,
        element.GetProperty("name").GetString()!,
        element.TryGetProperty("location", out var location) ? location.GetString() : null,
        // Parsed from the text: a node created from the element would read the document after it is disposed.
        element.TryGetProperty("settings", out var settings) ? JsonNode.Parse(settings.GetRawText())!.AsObject() : [],
        element.TryGetProperty("secrets", out var secrets)
            ? secrets.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(),
        element.TryGetProperty("autoImport", out var autoImport) && autoImport.GetBoolean());
}
