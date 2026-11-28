using Microsoft.Extensions.Logging;

namespace Watchpoint.Api;

/// <summary>Log events of the API (event ids 2000–2199, spec API §10).</summary>
internal static partial class ApiLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Route} trace {TraceId}")]
    public static partial void UnhandledException(ILogger logger, string method, string route, string traceId, Exception exception);

    [LoggerMessage(EventId = 2002, Message = "{Method} {Route} → {Status} in {DurationMs} ms (user {UserId}, session {SessionId}, client {ClientKind})")]
    public static partial void Request(ILogger logger, LogLevel level, string method, string route, int status, double durationMs, string userId, string sessionId, string clientKind);
}
