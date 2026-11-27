using Watchpoint.Hosting;
using Watchpoint.State;
using Watchpoint.State.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>
/// State module (spec STA §3, HST §5.2): the live state store, overall state computation, aggregates, queries and
/// change feed. Depends on the registry (entity membership) and the pipeline (partition subscriber, event log
/// records); registers no settings validation of its own beyond the <c>State</c> section's own range checks.
/// Start-up: the snapshot is restored in <c>Recovery</c> (order 50, before the pipeline recovery at order 100), the
/// periodic snapshots start in <c>Runtime</c> after the partition workers; the final snapshot is written at shutdown
/// in phase <c>Recovery</c>, i.e. after the workers drained in phase <c>Runtime</c> (STA §8).
/// </summary>
public sealed class StateModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "State";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>State</c> setting is out of range (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            services.AddWatchpointState(context.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }

        services.AddStartupTask<StateRestoreStartupTask>();
        services.AddStartupTask<StateSnapshotsStartupTask>();
        services.AddShutdownTask<StateSnapshotShutdownTask>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapStateEndpoints();
}

/// <summary>Phase <c>Recovery</c>, order 50: restores the state snapshot before the pipeline replays the log (STA §8.2).</summary>
/// <param name="state">The state lifecycle.</param>
public sealed class StateRestoreStartupTask(IStateLifecycle state) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Recovery;

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public string Name => "StateRestore";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => state.RestoreAsync(ct);
}

/// <summary>Phase <c>Runtime</c>, order 10: starts the periodic snapshots once the partition workers run (STA §8.1).</summary>
/// <param name="state">The state lifecycle.</param>
public sealed class StateSnapshotsStartupTask(IStateLifecycle state) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Runtime;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public string Name => "StateSnapshots";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct)
    {
        state.StartSnapshots();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Shutdown in phase <c>Recovery</c> (after the workers drained in phase <c>Runtime</c>): stops the periodic
/// snapshots and writes the final one (STA §8.1).
/// </summary>
/// <param name="state">The state lifecycle.</param>
public sealed class StateSnapshotShutdownTask(IStateLifecycle state) : IShutdownTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Recovery;

    /// <inheritdoc />
    public string Name => "StateSnapshot";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => state.StopAsync(ct);
}
