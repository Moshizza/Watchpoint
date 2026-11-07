using Microsoft.Extensions.Logging;

namespace Watchpoint.Kernel;

/// <summary>Log messages of the Kernel (event ids 1000–1099, spec K §11).</summary>
internal static partial class KernelLog
{
    [LoggerMessage(EventId = 1001, EventName = "CursorInvalid", Level = LogLevel.Warning, Message = "Invalid cursor received: {Reason}")]
    public static partial void CursorInvalid(ILogger logger, string reason);

    [LoggerMessage(EventId = 1002, EventName = "ValidationFailed", Level = LogLevel.Debug, Message = "Validation failed with {Count} field errors at {Paths}")]
    public static partial void ValidationFailed(ILogger logger, int count, string paths);
}
