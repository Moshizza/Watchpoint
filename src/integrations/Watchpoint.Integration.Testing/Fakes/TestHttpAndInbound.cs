using System.Net;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Fake implementation of <see cref="ISecureHttp"/>.
/// </summary>
public sealed class TestSecureHttp : ISecureHttp
{
    /// <inheritdoc />
    public HttpClient CreateClient(HttpClientOptions options)
    {
        return new HttpClient();
    }
}

/// <summary>
/// Fake implementation of <see cref="IInboundEndpoints"/>.
/// </summary>
public sealed class TestInboundEndpoints : IInboundEndpoints
{
    /// <inheritdoc />
    public ValueTask<InboundRegistration> RegisterHttpAsync(string path, InboundAuth auth, Func<InboundRequest, CancellationToken, ValueTask<InboundResponse>> handler, CancellationToken ct)
    {
        return ValueTask.FromResult(new InboundRegistration($"http://localhost/inbound/{path}", () => ValueTask.CompletedTask));
    }

    /// <inheritdoc />
    public ValueTask<InboundRegistration> RegisterTcpAsync(int port, IPAddress bindAddress, Func<Stream, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        return ValueTask.FromResult(new InboundRegistration($"{bindAddress}:{port}", () => ValueTask.CompletedTask));
    }

    /// <inheritdoc />
    public ValueTask<InboundRegistration> RegisterUdpAsync(int port, IPAddress bindAddress, string? shareKey, Func<UdpDatagram, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        return ValueTask.FromResult(new InboundRegistration($"{bindAddress}:{port}", () => ValueTask.CompletedTask));
    }
}
