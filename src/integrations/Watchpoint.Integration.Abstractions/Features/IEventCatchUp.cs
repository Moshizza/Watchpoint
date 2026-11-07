namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Reads events missed while disconnected, from the connector's persisted cursor (INT §6).</summary>
public interface IEventCatchUp
{
    /// <summary>Reads and reports missed events; called after reconnection, before live events resume.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completing when caught up.</returns>
    ValueTask CatchUpAsync(CancellationToken ct);
}
