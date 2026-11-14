namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// Where the NATS server of the event log is and how to authenticate (spec PIP §13). Registered by the host from its
/// NATS settings (HST §7.4: the bundled server with its generated token, or an external one).
/// </summary>
/// <param name="Url">The server URL (<c>nats://host:port</c>).</param>
/// <param name="Token">The authentication token, or <see langword="null"/> when the server needs none.</param>
public sealed record EventLogEndpoint(string Url, string? Token)
{
    /// <summary>Returns the URL only: the token must never reach a log.</summary>
    /// <returns>The URL.</returns>
    public override string ToString() => Url;
}
