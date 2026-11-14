using Watchpoint.Kernel.Results;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>The entry point of observations (spec PIP §6), used by the integration runtime.</summary>
public interface IObservationIngest
{
    /// <summary>Ingests a batch. Completes when every accepted observation is durable in the log (or coalesced/unchanged).</summary>
    /// <param name="batch">At most <see cref="IngestLimits.MaxBatchSize"/> observations.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The outcome per category.</returns>
    ValueTask<IngestResult> IngestAsync(IReadOnlyList<Observation> batch, CancellationToken ct);
}

/// <summary>The outcome of an ingest call.</summary>
/// <param name="Accepted">Observations appended to the log.</param>
/// <param name="Unchanged">Observations dropped because nothing changed.</param>
/// <param name="Coalesced">Observations replaced by a newer value while the connection was over quota.</param>
/// <param name="Rejected">Observations rejected, with the reason.</param>
public sealed record IngestResult(int Accepted, int Unchanged, int Coalesced, IReadOnlyList<RejectedObservation> Rejected);

/// <summary>A rejected observation.</summary>
/// <param name="ObservationId">The observation id.</param>
/// <param name="Error">Why it was rejected.</param>
public sealed record RejectedObservation(Guid ObservationId, Error Error);

/// <summary>Limits of the ingest contract (spec PIP §6).</summary>
public static class IngestLimits
{
    /// <summary>The largest batch accepted by <see cref="IObservationIngest.IngestAsync"/>.</summary>
    public const int MaxBatchSize = 5000;

    /// <summary>Checks the size of a batch.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>Success, or <c>pipeline.batch.too_large</c>.</returns>
    public static Result CheckBatchSize(IReadOnlyList<Observation> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Count > MaxBatchSize ? PipelineErrors.BatchTooLarge(batch.Count, MaxBatchSize) : Result.Success();
    }
}
