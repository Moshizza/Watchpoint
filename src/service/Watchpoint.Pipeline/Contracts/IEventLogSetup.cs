namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// Prepares the event log at start-up (spec PIP §5.1). The host calls <see cref="EnsureStreamsAsync"/> in start-up
/// phase <c>Infrastructure</c>, order 20, after NATS is reachable and before any module appends or reads.
/// </summary>
public interface IEventLogSetup
{
    /// <summary>Gets a value indicating whether the streams were found or created since the node started.</summary>
    bool Ready { get; }

    /// <summary>Creates the streams or updates them to the configured settings; idempotent.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the three streams exist with the configured settings.</returns>
    /// <exception cref="PipelineException">
    /// <c>pipeline.stream.incompatible</c>: an existing stream cannot be brought to the configuration.
    /// <c>pipeline.log.unavailable</c>: NATS was not reachable for <c>Log:AppendTimeout</c>.
    /// </exception>
    Task EnsureStreamsAsync(CancellationToken ct);
}
