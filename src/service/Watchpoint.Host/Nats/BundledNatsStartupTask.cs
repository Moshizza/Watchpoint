using Watchpoint.Hosting;
using Watchpoint.Hosting.Supervision;

namespace Watchpoint.Host.Nats;

/// <summary>
/// Starts the bundled NATS server (spec HST §7.4, <c>Nats:Mode = Bundled</c>) before any module needs it: version
/// check, generated configuration, supervised start, then a real client connection.
/// </summary>
public sealed partial class BundledNatsStartupTask(
    ISupervisor supervisor,
    NatsProcessSpecFactory factory,
    TimeProvider timeProvider,
    ILogger<BundledNatsStartupTask> logger) : IStartupTask
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Infrastructure;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public string Name => "BundledNats";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var version = await factory.ReadVersionAsync(timeProvider, ct).ConfigureAwait(false);
        var handle = await supervisor.StartAsync(factory.Create(), ct).ConfigureAwait(false);
        if (handle.State != SupervisedProcessState.Running)
        {
            throw new SupervisorException(
                HostErrors.ProcessFailed(handle.Name), $"Bundled NATS server did not start: {handle.LastExitReason ?? handle.State.ToString()}.");
        }

        var endpoint = factory.Endpoint();
        await NatsClientCheck.WaitUntilUsableAsync(endpoint, ClientTimeout, timeProvider, ct).ConfigureAwait(false);
        LogBundledNatsReady(logger, version, endpoint.Url, handle.Pid ?? 0);
    }

    [LoggerMessage(EventId = 1161, Level = LogLevel.Information, Message = "Bundled NATS server {Version} ready at {Url} (pid {Pid})")]
    private static partial void LogBundledNatsReady(ILogger logger, Version version, string url, int pid);
}
