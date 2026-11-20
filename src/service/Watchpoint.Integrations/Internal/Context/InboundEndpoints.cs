using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>The HTTP routes connectors registered, served under <c>/integrations/{connectionId}/{**path}</c> on the main endpoint.</summary>
internal sealed class InboundRouter(ILogger<InboundRouter> logger)
{
    public const string RoutePrefix = "/integrations";
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<(Guid Connection, string Path), Route> _routes = new();

    public int Count => _routes.Count;

    public bool TryAdd(Guid connection, string path, Route route) => _routes.TryAdd((connection, path), route);

    public void Remove(Guid connection, string path, Route route) =>
        _routes.TryRemove(new KeyValuePair<(Guid, string), Route>((connection, path), route));

    /// <summary>Authenticates the request as the registration declares, then calls the connector's handler.</summary>
    public async Task HandleAsync(HttpContext http, Guid connection, string? path)
    {
        ArgumentNullException.ThrowIfNull(http);
        var key = Normalize(path ?? string.Empty);
        if (!_routes.TryGetValue((connection, key), out var route))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = http.RequestAborted;
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var body = await ReadBodyAsync(http.Request.Body, ct).ConfigureAwait(false);
        if (body is null)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var remote = http.Connection.RemoteIpAddress ?? IPAddress.None;
        var refused = await AuthenticateAsync(route, http, body, remote, ct).ConfigureAwait(false);
        if (refused is not null)
        {
            IntegrationLog.InboundRefused(logger, connection, key, refused);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var headers = http.Request.Headers.ToDictionary(h => h.Key, h => (IReadOnlyList<string>)[.. h.Value.Select(v => v ?? string.Empty)], StringComparer.OrdinalIgnoreCase);
        InboundResponse response;
        try
        {
            response = await route.Handler(new InboundRequest(http.Request.Method, key, http.Request.QueryString.Value ?? string.Empty, headers, body, remote), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            IntegrationLog.InboundHandlerFailed(logger, ex, connection, key);
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        http.Response.StatusCode = response.StatusCode;
        if (response.ContentType is not null)
        {
            http.Response.ContentType = response.ContentType;
        }

        if (!response.Body.IsEmpty)
        {
            await http.Response.Body.WriteAsync(response.Body, ct).ConfigureAwait(false);
        }
    }

    public static string Normalize(string path) => path.Trim('/');

    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<string?> AuthenticateAsync(Route route, HttpContext http, byte[] body, IPAddress remote, CancellationToken ct)
    {
        switch (route.Auth)
        {
            case InboundAuth.SharedSecretHeader shared:
                {
                    if (!http.Request.Headers.TryGetValue(shared.HeaderName, out var given) || given.Count != 1)
                    {
                        return "missing header";
                    }

                    using var secret = await route.Secrets.GetAsync(shared.SecretFieldId, ct).ConfigureAwait(false);
                    if (secret is null)
                    {
                        return "secret not set";
                    }

                    var presented = Encoding.UTF8.GetBytes(given[0] ?? string.Empty);
                    return secret.UseBytes(expected => CryptographicOperations.FixedTimeEquals(expected, presented)) ? null : "invalid header";
                }

            case InboundAuth.HmacSignature hmac:
                {
                    if (!string.Equals(hmac.Algorithm, "HMACSHA256", StringComparison.OrdinalIgnoreCase))
                    {
                        return "unsupported algorithm";
                    }

                    if (!http.Request.Headers.TryGetValue(hmac.HeaderName, out var given) || given.Count != 1 || ParseSignature(given[0]) is not { } presented)
                    {
                        return "missing signature";
                    }

                    using var secret = await route.Secrets.GetAsync(hmac.SecretFieldId, ct).ConfigureAwait(false);
                    if (secret is null)
                    {
                        return "secret not set";
                    }

                    var expected = secret.UseBytes(key => HMACSHA256.HashData(key, body));
                    return CryptographicOperations.FixedTimeEquals(expected, presented) ? null : "invalid signature";
                }

            case InboundAuth.ClientCertificate certificate:
                {
                    var client = http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct).ConfigureAwait(false);
                    if (client is null)
                    {
                        return "no client certificate";
                    }

                    var pin = Convert.FromHexString(certificate.PinnedSha256.Replace(":", string.Empty, StringComparison.Ordinal));
                    return CryptographicOperations.FixedTimeEquals(SHA256.HashData(client.RawData), pin) ? null : "certificate not pinned";
                }

            case InboundAuth.SourceAddress source:
                {
                    var address = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
                    return source.AllowedCidrs.Any(c => IPNetwork.TryParse(c, out var network) && network.Contains(address)) ? null : "address not allowed";
                }

            default:
                return "unsupported authentication";
        }
    }

    private static byte[]? ParseSignature(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var value = text.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? text["sha256=".Length..] : text;
        try
        {
            return value.Length == 64 ? Convert.FromHexString(value) : Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal sealed record Route(InboundAuth Auth, Func<InboundRequest, CancellationToken, ValueTask<InboundResponse>> Handler, ISecretAccessor Secrets);
}

/// <summary>
/// The inbound endpoints of one connector instance (spec IRT §8): HTTP routes on the main endpoint, authenticated before
/// the handler; TCP listeners only with the manifest permission <c>inboundTcp</c> and the settings field
/// <c>inboundTcp: true</c>. Everything registered is released when the instance stops.
/// </summary>
internal sealed class InboundEndpoints(ConnectionId connection, InboundRouter router, ISecretAccessor secrets, IReadOnlySet<string> secretFields,
    bool tcpPermitted, bool tcpEnabled, ILogger logger) : IInboundEndpoints, IAsyncDisposable
{
    public const string InboundTcpPermission = "inboundTcp";

    private readonly Lock _lock = new();
    private readonly List<InboundRegistration> _registrations = [];
    private bool _disposed;

    public ValueTask<InboundRegistration> RegisterHttpAsync(string path, InboundAuth auth,
        Func<InboundRequest, CancellationToken, ValueTask<InboundResponse>> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(handler);
        var field = auth switch
        {
            InboundAuth.SharedSecretHeader s => s.SecretFieldId,
            InboundAuth.HmacSignature h => h.SecretFieldId,
            _ => null,
        };
        if (field is not null && !secretFields.Contains(field))
        {
            throw new ArgumentException($"Secret field '{field}' is not declared by the connector.", nameof(auth));
        }

        var key = InboundRouter.Normalize(path);
        var route = new InboundRouter.Route(auth, handler, secrets);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!router.TryAdd(connection.Value, key, route))
            {
                throw new InvalidOperationException($"Inbound path '{key}' is already registered.");
            }

            var registration = new InboundRegistration($"{InboundRouter.RoutePrefix}/{connection.Value:D}/{key}", () =>
            {
                router.Remove(connection.Value, key, route);
                return ValueTask.CompletedTask;
            });
            _registrations.Add(registration);
            return ValueTask.FromResult(registration);
        }
    }

    public ValueTask<InboundRegistration> RegisterTcpAsync(int port, IPAddress bindAddress, Func<Stream, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        ArgumentNullException.ThrowIfNull(handler);
        if (!tcpPermitted)
        {
            throw new UnauthorizedAccessException($"The connector's manifest does not declare the permission '{InboundTcpPermission}'.");
        }

        if (!tcpEnabled)
        {
            throw new UnauthorizedAccessException("Inbound TCP is not enabled in the connection settings (inboundTcp).");
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var listener = new TcpListener(bindAddress, port);
        listener.Start();
        var cts = new CancellationTokenSource();
        var loop = Task.Run(() => AcceptAsync(listener, handler, cts.Token), CancellationToken.None);
        var registration = new InboundRegistration(listener.LocalEndpoint.ToString()!, async () =>
        {
            await cts.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            await loop.ConfigureAwait(false);
            cts.Dispose();
        });
        lock (_lock)
        {
            _registrations.Add(registration);
        }

        return ValueTask.FromResult(registration);
    }

    public ValueTask<InboundRegistration> RegisterUdpAsync(int port, IPAddress bindAddress, string? shareKey,
        Func<UdpDatagram, CancellationToken, ValueTask> handler, CancellationToken ct) =>
        throw new NotSupportedException("Inbound UDP arrives with M4 (C-SNMP).");

    public async ValueTask DisposeAsync()
    {
        List<InboundRegistration> registrations;
        lock (_lock)
        {
            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
        }

        foreach (var registration in registrations)
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AcceptAsync(TcpListener listener, Func<Stream, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        var clients = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            clients.RemoveAll(c => c.IsCompleted);
            clients.Add(ServeAsync(client, handler, ct));
        }

        await Task.WhenAll(clients).ConfigureAwait(false);
    }

    private async Task ServeAsync(TcpClient client, Func<Stream, CancellationToken, ValueTask> handler, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await handler(client.GetStream(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                IntegrationLog.InboundHandlerFailed(logger, ex, connection.Value, "tcp");
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }
    }
}
