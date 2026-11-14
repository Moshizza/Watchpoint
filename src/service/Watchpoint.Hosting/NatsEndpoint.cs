namespace Watchpoint.Hosting;

/// <summary>
/// Where the node's NATS server is and how to authenticate (spec HST §7.4): the bundled server on loopback with
/// the generated token, or <c>Nats:Url</c> / <c>Nats:Token</c> in External mode. Registered by the host; used by PIP.
/// </summary>
/// <param name="Url">The server URL (<c>nats://host:port</c>).</param>
/// <param name="Token">The authentication token, or <see langword="null"/> when the server needs none.</param>
public sealed record NatsEndpoint(string Url, string? Token)
{
    /// <summary>Returns the URL only: the token must never reach a log.</summary>
    /// <returns>The URL.</returns>
    public override string ToString() => Url;
}
