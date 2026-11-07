using Microsoft.Extensions.Logging;

namespace Watchpoint.Testing.Logging;

/// <summary>A log entry captured by <see cref="LogCollector"/>.</summary>
/// <param name="Category">The logger category.</param>
/// <param name="Level">The level.</param>
/// <param name="EventId">The event id.</param>
/// <param name="MessageTemplate">The message template (<c>{OriginalFormat}</c>), or the rendered message when absent.</param>
/// <param name="Properties">The structured properties, without <c>{OriginalFormat}</c>.</param>
/// <param name="RenderedMessage">The formatted message.</param>
/// <param name="Exception">The exception, if any.</param>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    int EventId,
    string MessageTemplate,
    IReadOnlyDictionary<string, object?> Properties,
    string RenderedMessage,
    Exception? Exception);
