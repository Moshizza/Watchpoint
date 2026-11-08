using System.Text.Json;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Connection diagnostics shown in the connection's Diagnostics tab (INT §6).</summary>
public interface IDiagnostics
{
    /// <summary>Returns the current diagnostic items.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The items.</returns>
    ValueTask<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(CancellationToken ct);

    /// <summary>Runs a diagnostic action declared in the manifest.</summary>
    /// <param name="actionId">The action id.</param>
    /// <param name="args">The action arguments.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A message describing the outcome, or a failure.</returns>
    ValueTask<Result<ConnectorMessage>> RunActionAsync(string actionId, JsonElement args, CancellationToken ct);
}

/// <summary>Level of a diagnostic item.</summary>
public enum DiagnosticLevel
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Something an administrator should look at.</summary>
    Warning,

    /// <summary>A problem.</summary>
    Error,
}

/// <summary>One diagnostic fact.</summary>
/// <param name="Id">A stable id, e.g. <c>firmwareVersion</c>.</param>
/// <param name="Label">The label.</param>
/// <param name="Value">The value.</param>
/// <param name="Level">The level.</param>
public sealed record DiagnosticItem(string Id, ConnectorMessage Label, WpValue Value, DiagnosticLevel Level = DiagnosticLevel.Info);
