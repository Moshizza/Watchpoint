using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integration.Abstractions;

/// <summary>
/// A connector: one instance per connection (INT §4). Call order: <see cref="InitializeAsync"/> →
/// (<see cref="TestAsync"/>)* → <see cref="StartAsync"/> → feature calls → <see cref="StopAsync"/> →
/// <see cref="IAsyncDisposable.DisposeAsync"/>. A stopped instance is never restarted (rules L1–L6).
/// Optional features are further interfaces of the same class (namespace <c>Features</c>).
/// </summary>
public interface IConnector : IAsyncDisposable
{
    /// <summary>Called once: validates settings beyond the schema and keeps the context.</summary>
    /// <param name="context">The services the runtime gives this connection.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, or a failure (e.g. <c>common.validation.failed</c> with field errors relative to the settings).</returns>
    ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct);

    /// <summary>Short connectivity test (≤ 30 s); may run before <see cref="StartAsync"/> on a separate instance.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The test result.</returns>
    ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct);

    /// <summary>Begins communication; returns within 5 s, connecting continues in the background (reported via health).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completing once started.</returns>
    ValueTask StartAsync(CancellationToken ct);

    /// <summary>Stops all activity within the token's lifetime (15 s). Idempotent; afterwards the sink is not called.</summary>
    /// <param name="ct">Cancelled when the stop deadline is reached.</param>
    /// <returns>A task completing once stopped.</returns>
    ValueTask StopAsync(CancellationToken ct);

    /// <summary>Returns the complete inventory currently known (servers and devices). May contact the server.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inventory.</returns>
    ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct);
}
