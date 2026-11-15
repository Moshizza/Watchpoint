using System.Net;
using System.Net.Sockets;
using Testcontainers.Nats;

namespace Watchpoint.Testing.Containers;

/// <summary>
/// A NATS server with JetStream of its own for tests that stop and start the server (the shared
/// <see cref="NatsContainerFixture"/> must stay up for the other tests). The host port is fixed, so clients reconnect
/// to the same URL after a restart; JetStream data stays in the container between stop and start.
/// </summary>
public sealed class RestartableNatsContainer : IAsyncDisposable
{
    private readonly NatsContainer _container;

    private RestartableNatsContainer(NatsContainer container, string url)
    {
        _container = container;
        Url = url;
    }

    /// <summary>Gets the server URL (<c>nats://host:port</c>), the same before and after a restart.</summary>
    public string Url { get; }

    /// <summary>Starts a dedicated server. Skips or fails the test when Docker is missing.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The running server; the caller disposes it.</returns>
    public static async Task<RestartableNatsContainer> StartNewAsync(CancellationToken ct)
    {
        DockerAvailability.Ensure(new ContainerFixtureOptions());
        var port = FreePort();
        var container = new NatsBuilder(ContainerImages.Nats).WithCommand("-js").WithPortBinding(port, 4222).Build();
        try
        {
            await container.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new RestartableNatsContainer(container, $"nats://{container.Hostname}:{port}");
    }

    /// <summary>Stops the server (graceful stop of the container).</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the container stopped.</returns>
    public Task StopAsync(CancellationToken ct) => _container.StopAsync(ct);

    /// <summary>Starts the stopped server again with its data.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the container started.</returns>
    public Task StartAsync(CancellationToken ct) => _container.StartAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
