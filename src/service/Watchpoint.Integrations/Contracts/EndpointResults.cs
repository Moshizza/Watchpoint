using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Contracts;

/// <summary>Results of the integration runtime's endpoints until the API framework (T1.19) replaces them.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Endpoint lambdas return IResult from several branches.")]
internal static class EndpointResults
{
    /// <summary>
    /// Endpoint filter of the endpoints that change the node before the API's permission filter exists (spec IRT §10):
    /// a request from a non-loopback address gets 404, as if the endpoint did not exist.
    /// </summary>
    public static async ValueTask<object?> LoopbackOnly(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            return Results.NotFound();
        }

        return await next(context).ConfigureAwait(false);
    }

    public static IResult Problem(Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        // RFC 9457 problem details (Architecture README §6.2); the API framework (T1.19) replaces this local mapping.
        return Json(status, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "https://watchpoint.dev/errors/" + error.Code.Value);
            writer.WriteString("title", error.Code.Value);
            writer.WriteNumber("status", status);
            writer.WriteString("code", error.Code.Value);
            writer.WriteString("messageKey", error.MessageKey);
            writer.WriteStartObject("args");
            if (error.Kind != ErrorKind.Internal)
            {
                foreach (var (key, value) in error.Args)
                {
                    writer.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            writer.WriteEndObject();
            if (error.FieldErrors is { Count: > 0 } fields)
            {
                writer.WriteStartArray("errors");
                foreach (var field in fields)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", field.Path);
                    writer.WriteString("code", field.Code.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }, contentType: "application/problem+json");
    }

    public static IResult Json(int status, Action<Utf8JsonWriter> write, string? location = null, string contentType = "application/json") =>
        new JsonResult(status, write, location, contentType);

    private sealed class JsonResult(int status, Action<Utf8JsonWriter> write, string? location, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                write(writer);
            }

            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = contentType;
            if (location is not null)
            {
                httpContext.Response.Headers.Location = location;
            }

            await httpContext.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
