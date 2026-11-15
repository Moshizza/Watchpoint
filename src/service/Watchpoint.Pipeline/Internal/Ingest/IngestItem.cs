using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>A validated, normalised observation on its way to a partition worker (spec PIP §7).</summary>
internal sealed class IngestItem
{
    public required Guid Id { get; init; }

    public required ObservationKind Kind { get; init; }

    public DeviceId? Device { get; init; }

    public ServerId? Server { get; init; }

    /// <summary>The id of the device or server.</summary>
    public required Guid Target { get; init; }

    public required int Partition { get; init; }

    public PropertyPath Property { get; init; }

    public WpValue Value { get; init; }

    public Quality Quality { get; init; }

    public EventPath Event { get; init; }

    public IReadOnlyDictionary<string, WpValue>? Payload { get; init; }

    public Severity Severity { get; init; }

    public HealthRecordBody? Health { get; init; }

    public required DateTimeOffset ReceivedTime { get; init; }

    /// <summary>The source time, already replaced by the received time when it was too far in the future.</summary>
    public DateTimeOffset? SourceTime { get; init; }

    /// <summary>The source time that was replaced, if any (clock drift hint).</summary>
    public DateTimeOffset? DriftedSourceTime { get; init; }

    public required ConnectionId Connection { get; init; }

    public CommandId? Command { get; init; }

    /// <summary>The ingest call waiting for this observation.</summary>
    public required IngestCall Call { get; init; }

    /// <summary>Whether the outcome was reported to <see cref="Call"/>; an observation reports exactly once.</summary>
    public bool Settled { get; private set; }

    public void Settle(IngestOutcome outcome)
    {
        if (!Settled)
        {
            Settled = true;
            Call.Complete(outcome);
        }
    }

    public void Reject(Error error)
    {
        if (!Settled)
        {
            Settled = true;
            Call.Reject(Id, error);
        }
    }
}

/// <summary>What happened to an observation.</summary>
internal enum IngestOutcome
{
    Accepted,
    Unchanged,
    Coalesced,
    Rejected,
    Disabled,
}

/// <summary>
/// One <c>IngestAsync</c> call: counts the outcome of its observations and completes when the last one is known. Every
/// observation reports exactly once, from any thread.
/// </summary>
internal sealed class IngestCall
{
    private readonly TaskCompletionSource<IngestResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PipelineMetrics _metrics;
    private readonly string _connection;
    private List<RejectedObservation>? _rejected;
    private int _pending;
    private int _accepted;
    private int _unchanged;
    private int _coalesced;

    public IngestCall(int observations, long startedTimestamp, PipelineMetrics metrics, ConnectionId connection)
    {
        // One more than the observations: released by Seal, so that the call cannot complete while it is still routed.
        _pending = observations + 1;
        StartedTimestamp = startedTimestamp;
        _metrics = metrics;
        _connection = connection.ToString();
    }

    public long StartedTimestamp { get; }

    public Task<IngestResult> Completion => _done.Task;

    public void Complete(IngestOutcome outcome)
    {
        switch (outcome)
        {
            case IngestOutcome.Accepted:
                Interlocked.Increment(ref _accepted);
                break;
            case IngestOutcome.Unchanged:
                Interlocked.Increment(ref _unchanged);
                break;
            case IngestOutcome.Coalesced:
                Interlocked.Increment(ref _coalesced);
                break;
        }

        _metrics.Observation(_connection, outcome);
        Release();
    }

    public void Reject(Guid observationId, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_done)
        {
            (_rejected ??= []).Add(new RejectedObservation(observationId, error));
        }

        _metrics.Observation(_connection, IngestOutcome.Rejected, error.Code.Value);
        Release();
    }

    public void Seal() => Release();

    private void Release()
    {
        if (Interlocked.Decrement(ref _pending) != 0)
        {
            return;
        }

        IReadOnlyList<RejectedObservation> rejected;
        lock (_done)
        {
            rejected = _rejected is null ? [] : _rejected;
        }

        _done.TrySetResult(new IngestResult(_accepted, _unchanged, _coalesced, rejected));
    }
}
