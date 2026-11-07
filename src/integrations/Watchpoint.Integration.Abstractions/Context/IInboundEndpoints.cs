using System.Net;
using System.Text.Json.Serialization;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>Inbound endpoints of a connection: webhooks and listeners (INT §7.3).</summary>
public interface IInboundEndpoints
{
    /// <summary>Registers an HTTP endpoint hosted under <c>https://&lt;node&gt;/integrations/&lt;connectionId&gt;/&lt;path&gt;</c>.</summary>
    /// <param name="path">The path below the connection's base.</param>
    /// <param name="auth">How callers authenticate.</param>
    /// <param name="handler">The request handler.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The registration; dispose it to unregister.</returns>
    ValueTask<InboundRegistration> RegisterHttpAsync(string path, InboundAuth auth,
        Func<InboundRequest, CancellationToken, ValueTask<InboundResponse>> handler, CancellationToken ct);

    /// <summary>Registers a TCP listener (manifest permission <c>inboundTcp</c>).</summary>
    /// <param name="port">The port.</param>
    /// <param name="bindAddress">The address to bind.</param>
    /// <param name="handler">Handles one accepted connection.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The registration; dispose it to unregister.</returns>
    ValueTask<InboundRegistration> RegisterTcpAsync(int port, IPAddress bindAddress, Func<Stream, CancellationToken, ValueTask> handler,
        CancellationToken ct);

    /// <summary>Registers a UDP listener (manifest permission <c>inboundUdp</c>, M4); the same share key shares one socket.</summary>
    /// <param name="port">The port.</param>
    /// <param name="bindAddress">The address to bind.</param>
    /// <param name="shareKey">Connections with the same key share one socket, fanned out to each handler.</param>
    /// <param name="handler">Handles one datagram.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The registration; dispose it to unregister.</returns>
    ValueTask<InboundRegistration> RegisterUdpAsync(int port, IPAddress bindAddress, string? shareKey,
        Func<UdpDatagram, CancellationToken, ValueTask> handler, CancellationToken ct);
}

/// <summary>A registered inbound endpoint; disposing it unregisters the endpoint.</summary>
/// <param name="address">The public URL (HTTP) or the bound endpoint (TCP, UDP).</param>
/// <param name="unregister">Unregisters the endpoint (called once).</param>
public sealed class InboundRegistration(string address, Func<ValueTask> unregister) : IAsyncDisposable
{
    private Func<ValueTask>? _unregister = unregister ?? throw new ArgumentNullException(nameof(unregister));

    /// <summary>Gets the public URL (HTTP) or the bound endpoint (TCP, UDP).</summary>
    public string Address { get; } = address ?? throw new ArgumentNullException(nameof(address));

    /// <summary>Unregisters the endpoint. Idempotent.</summary>
    /// <returns>A task completing when unregistered.</returns>
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _unregister, null)?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>An authenticated inbound HTTP request.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path below the registered path.</param>
/// <param name="Query">The query string without <c>?</c>, possibly empty.</param>
/// <param name="Headers">The request headers (case-insensitive names).</param>
/// <param name="Body">The body (bounded by the runtime).</param>
/// <param name="RemoteAddress">The caller's address.</param>
public sealed record InboundRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
    ReadOnlyMemory<byte> Body, IPAddress RemoteAddress);

/// <summary>A response to an inbound HTTP request.</summary>
/// <param name="StatusCode">The status code.</param>
/// <param name="Body">The body.</param>
/// <param name="ContentType">The body's content type.</param>
public sealed record InboundResponse(int StatusCode, ReadOnlyMemory<byte> Body = default, string? ContentType = null)
{
    /// <summary>200 OK without body.</summary>
    public static InboundResponse Ok { get; } = new(200);

    /// <summary>204 No Content.</summary>
    public static InboundResponse NoContent { get; } = new(204);
}

/// <summary>A received UDP datagram.</summary>
/// <param name="Data">The payload.</param>
/// <param name="Remote">The sender.</param>
public sealed record UdpDatagram(ReadOnlyMemory<byte> Data, IPEndPoint Remote);

/// <summary>
/// How callers of an inbound HTTP endpoint authenticate. Serialised as JSON with a <c>kind</c> discriminator
/// (<c>sharedSecretHeader</c>, <c>hmacSignature</c>, <c>clientCertificate</c>, <c>sourceAddress</c>), see
/// <see cref="IntegrationJson"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SharedSecretHeader), "sharedSecretHeader")]
[JsonDerivedType(typeof(HmacSignature), "hmacSignature")]
[JsonDerivedType(typeof(ClientCertificate), "clientCertificate")]
[JsonDerivedType(typeof(SourceAddress), "sourceAddress")]
public abstract record InboundAuth
{
    private protected InboundAuth()
    {
    }

    /// <summary>A header must carry the secret value.</summary>
    /// <param name="HeaderName">The header name.</param>
    /// <param name="SecretFieldId">The secret field holding the expected value.</param>
    public sealed record SharedSecretHeader(string HeaderName, string SecretFieldId) : InboundAuth;

    /// <summary>A header must carry an HMAC of the body keyed with the secret.</summary>
    /// <param name="HeaderName">The header name.</param>
    /// <param name="SecretFieldId">The secret field holding the key.</param>
    /// <param name="Algorithm">The algorithm, e.g. <c>HMACSHA256</c>.</param>
    public sealed record HmacSignature(string HeaderName, string SecretFieldId, string Algorithm) : InboundAuth;

    /// <summary>The caller must present a client certificate with this SHA-256 thumbprint.</summary>
    /// <param name="PinnedSha256">The SHA-256 thumbprint (hex).</param>
    public sealed record ClientCertificate(string PinnedSha256) : InboundAuth;

    /// <summary>The caller's address must be in one of the ranges; combined with another variant.</summary>
    /// <param name="AllowedCidrs">The allowed ranges (CIDR).</param>
    public sealed record SourceAddress(IReadOnlyList<string> AllowedCidrs) : InboundAuth;
}
