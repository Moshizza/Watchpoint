using System.Security.Cryptography;
using System.Text;
using Testcontainers.Nats;
using Xunit;

namespace Watchpoint.Testing.Containers;

/// <summary>
/// NATS server with JetStream shared by a test assembly (register with
/// <c>[assembly: AssemblyFixture(typeof(NatsContainerFixture))]</c>). Started lazily by <see cref="StartAsync"/>;
/// test classes isolate themselves with a <see cref="NatsScope"/>.
/// </summary>
public sealed class NatsContainerFixture : IAsyncLifetime
{
    private readonly ContainerFixtureOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private NatsContainer? _container;
    private string? _url;

    /// <summary>Initializes a new instance of the <see cref="NatsContainerFixture"/> class with default options.</summary>
    public NatsContainerFixture()
        : this(new ContainerFixtureOptions())
    {
    }

    private NatsContainerFixture(ContainerFixtureOptions options)
    {
        _options = options;
    }

    /// <summary>Creates a fixture with explicit options (the assembly fixture uses the defaults).</summary>
    /// <param name="options">The options.</param>
    /// <returns>The fixture; the caller disposes it.</returns>
    public static NatsContainerFixture WithOptions(ContainerFixtureOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>Gets the server URL (<c>nats://host:port</c>).</summary>
    /// <exception cref="InvalidOperationException">The container is not started.</exception>
    public string Url => _url ?? throw new InvalidOperationException("Container not started: call StartAsync first.");

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Starts the container once. Skips or fails the test when Docker is missing.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the server is ready.</returns>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_url is not null)
        {
            return;
        }

        DockerAvailability.Ensure(_options);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_url is not null)
            {
                return;
            }

            var container = new NatsBuilder(ContainerImages.Nats).WithCommand("-js").Build();
            _container = container;
            await container.StartAsync(ct).ConfigureAwait(false);
            _url = container.GetConnectionString();
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Creates a unique subject and stream prefix for a test class.</summary>
    /// <param name="testClassName">The test class name, used in the prefix hash.</param>
    /// <returns>The scope.</returns>
    /// <exception cref="InvalidOperationException">The container is not started.</exception>
    public NatsScope CreateScope(string testClassName)
    {
        ArgumentException.ThrowIfNullOrEmpty(testClassName);
        _ = Url;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(testClassName + "/" + Guid.NewGuid().ToString("N")));
        var token = "t" + Convert.ToHexStringLower(hash)[..8];
        return new NatsScope(token + ".wp", token.ToUpperInvariant() + "_");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        _startGate.Dispose();
    }
}
