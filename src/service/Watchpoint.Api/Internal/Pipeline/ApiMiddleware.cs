using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Pipeline;

/// <summary>
/// Exception handler (spec API §4 row 3): an unhandled exception answers 500 <c>common.internal</c> with the trace id
/// and logs 2001; <see cref="BadHttpRequestException"/> (malformed body, JSON depth &gt; 32, binding) answers 400
/// <c>api.request.invalid</c>, or 413 <c>api.request.too_large</c>.
/// </summary>
internal sealed class ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> logger, IOptions<ApiOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            var error = ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? ApiErrors.RequestTooLarge(MaxBodyOf(context, options.Value))
                : ApiErrors.RequestInvalid();
            context.Response.Clear();
            await ProblemDetailsWriter.WriteAsync(context, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nothing to answer.
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var traceId = ProblemDetailsWriter.TraceIdOf(context);
            ApiLog.UnhandledException(logger, context.Request.Method, RouteOf(context), traceId, ex);
            context.Response.Clear();
            await ProblemDetailsWriter.WriteAsync(context, Error.Internal(ex.GetType().Name)).ConfigureAwait(false);
        }
    }

    internal static long MaxBodyOf(HttpContext context, ApiOptions options) =>
        context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize ?? options.MaxBodyBytes;

    internal static string RouteOf(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
}

/// <summary>
/// Request logging and metrics (spec API §4 row 4): one line per request — method, route template (never the raw path
/// or query string), status, duration, user, session, client kind; Information for mutating methods, Debug for GET.
/// </summary>
internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, ApiMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = ExceptionHandlerMiddleware.RouteOf(context);
            var status = context.Response.StatusCode;
            var user = context.Features.Get<ICurrentUser>();
            var level = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ? LogLevel.Debug : LogLevel.Information;
            if (logger.IsEnabled(level))
            {
                var authenticated = user is { IsAuthenticated: true };
                var userId = authenticated ? user!.UserId.ToString() : "-";
                var sessionId = authenticated ? user!.SessionId.ToString() : "-";
                var clientKind = authenticated ? user!.ClientKind.ToString().ToLowerInvariant() : "-";
                var duration = Math.Round(elapsed, 1);
                ApiLog.Request(logger, level, context.Request.Method, route, status, duration, userId, sessionId, clientKind);
            }

            var tags = new TagList { { "route", route }, { "status", status } };
            metrics.Requests.Add(1, tags);
            metrics.Duration.Record(elapsed, new KeyValuePair<string, object?>("route", route));
        }
    }
}

/// <summary>Request body limit (API §4.1): a declared <c>Content-Length</c> above the limit answers 413 before the endpoint runs.</summary>
internal sealed class BodyLimitMiddleware(RequestDelegate next, IOptions<ApiOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var max = context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? options.Value.MaxBodyBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = max;
        }

        if (context.Request.ContentLength is { } length && length > max)
        {
            await ProblemDetailsWriter.WriteAsync(context, ApiErrors.RequestTooLarge(max)).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
