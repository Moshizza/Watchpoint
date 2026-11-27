namespace Watchpoint.Api.Contracts;

/// <summary>Node facts for <c>GET /api/v1/system/info</c> (spec API §6), registered by the host.</summary>
/// <param name="NodeId">The node id.</param>
/// <param name="NodeName">The node name.</param>
/// <param name="Environment">The environment, lower case.</param>
/// <param name="Version">The product version.</param>
/// <param name="Features">The enabled modules.</param>
/// <param name="Urls">The node's public URLs (client failover).</param>
public sealed record SystemInfo(Guid NodeId, string NodeName, string Environment, string Version, IReadOnlyList<string> Features, IReadOnlyList<string> Urls);

/// <summary>Supplies the site name of <c>/system/info</c> (registered by the registry module; absent → <see langword="null"/>).</summary>
public interface ISiteNameProvider
{
    /// <summary>The site name in the default culture, or <see langword="null"/> before the site exists.</summary>
    /// <returns>The name.</returns>
    string? GetSiteName();
}
