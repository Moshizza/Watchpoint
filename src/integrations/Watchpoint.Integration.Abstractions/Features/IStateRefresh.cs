using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Reads current states and reports them through the sink (after (re)connection, on operator refresh) (INT §6).</summary>
public interface IStateRefresh
{
    /// <summary>Reads current states within the scope and reports them through the sink.</summary>
    /// <param name="scope">What to refresh.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completing when the states were reported.</returns>
    ValueTask RefreshAsync(RefreshScope scope, CancellationToken ct);
}
