using System.Text.Json;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>
/// Finds systems on the network for the connection wizard (e.g. ONVIF WS-Discovery, BACnet Who-Is) (INT §6).
/// Implemented by a separate class declared in the manifest and instantiated without settings.
/// </summary>
public interface IServerLocator
{
    /// <summary>Searches for systems until the request's timeout or cancellation.</summary>
    /// <param name="request">What to search.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The systems found, as they are found.</returns>
    IAsyncEnumerable<LocatedServer> LocateAsync(LocateRequest request, CancellationToken ct);
}

/// <summary>A network search request.</summary>
/// <param name="Timeout">How long to search.</param>
/// <param name="Networks">Networks to search (CIDR notation); <see langword="null"/> = the node's local networks.</param>
public sealed record LocateRequest(TimeSpan Timeout, IReadOnlyList<string>? Networks = null);

/// <summary>A system found on the network.</summary>
/// <param name="Address">How to reach it (host, host:port or URL), as the settings expect it.</param>
/// <param name="Name">The name the system announces, if any.</param>
/// <param name="Identity">Model, version and serial, when announced.</param>
/// <param name="SuggestedSettings">A settings fragment to pre-fill the wizard, if any.</param>
public sealed record LocatedServer(string Address, string? Name, ServerIdentity? Identity, JsonElement? SuggestedSettings);
