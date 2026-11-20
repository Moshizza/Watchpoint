using Watchpoint.Hosting;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Pipeline module (spec PIP §3, HST §5.2): event log, ingest, partition workers and side-effect queue, on the node's
/// NATS server. Start-up: streams in <c>Infrastructure</c> (order 20, after the bundled NATS server), recovery
/// handshake in <c>Recovery</c>, workers in <c>Runtime</c>; the workers drain at shutdown.
/// </summary>
public sealed class PipelineModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Pipeline";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>Log</c>, <c>Ingest</c> or <c>SideEffects</c> setting is out of range (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        // Modules do not reference Watchpoint.Hosting (ARCH-3): the host hands the NATS endpoint over.
        services.AddSingleton(sp =>
        {
            var nats = sp.GetRequiredService<NatsEndpoint>();
            return new EventLogEndpoint(nats.Url, nats.Token);
        });
        try
        {
            services.AddWatchpointPipeline(context.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }

        services.AddStartupTask<EventLogSetupStartupTask>();
        services.AddStartupTask<PipelineRecoveryStartupTask>();
        services.AddStartupTask<PipelineRuntimeStartupTask>();
        services.AddShutdownTask<PipelineShutdownTask>();
    }
}

/// <summary>Phase <c>Infrastructure</c>, order 20: creates or updates the streams of the event log (PIP §5.1).</summary>
/// <param name="setup">The event log set-up.</param>
public sealed class EventLogSetupStartupTask(IEventLogSetup setup) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Infrastructure;

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public string Name => "EventLogStreams";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => setup.EnsureStreamsAsync(ct);
}

/// <summary>Phase <c>Recovery</c>, order 100: subscribers restore, the log is replayed after their checkpoints (PIP §7.4).</summary>
/// <param name="pipeline">The pipeline lifecycle.</param>
public sealed class PipelineRecoveryStartupTask(IPipelineLifecycle pipeline) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Recovery;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Name => "PipelineRecovery";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => pipeline.RecoverAsync(ct);
}

/// <summary>Phase <c>Runtime</c>, order 0: starts the partition workers and the side-effect workers (PIP §7.3, §9).</summary>
/// <param name="pipeline">The pipeline lifecycle.</param>
public sealed class PipelineRuntimeStartupTask(IPipelineLifecycle pipeline) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Runtime;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public string Name => "PipelineWorkers";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => pipeline.StartAsync(ct);
}

/// <summary>Phase <c>Runtime</c> at shutdown: the workers finish what is queued, within a bounded time (PIP §7.3).</summary>
/// <param name="pipeline">The pipeline lifecycle.</param>
public sealed class PipelineShutdownTask(IPipelineLifecycle pipeline) : IShutdownTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Runtime;

    /// <inheritdoc />
    public string Name => "PipelineDrain";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => pipeline.StopAsync(ct);
}
