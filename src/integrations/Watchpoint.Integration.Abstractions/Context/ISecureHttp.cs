namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>
/// Creates HTTP clients with the connection's TLS policy (INT §7.3): system trust, a custom CA (PEM) or a SHA-256
/// public-key pin. There is deliberately no way to ignore certificate errors.
/// </summary>
public interface ISecureHttp
{
    /// <summary>Creates a client.</summary>
    /// <param name="options">The client options.</param>
    /// <returns>The client; dispose it with the connector.</returns>
    HttpClient CreateClient(HttpClientOptions options);
}

/// <summary>Options of a connector HTTP client.</summary>
/// <param name="BaseAddress">The base address.</param>
/// <param name="Timeout">The request timeout.</param>
/// <param name="PinnedPublicKeySha256">SHA-256 of the server's public key (hex or base64), to pin it.</param>
/// <param name="CustomCaPem">A CA certificate (PEM) the server certificate must chain to.</param>
/// <param name="AllowHttp">Allows plain HTTP; only when the settings explicitly allow it.</param>
public sealed record HttpClientOptions(Uri BaseAddress, TimeSpan Timeout, string? PinnedPublicKeySha256 = null,
    string? CustomCaPem = null, bool AllowHttp = false);
