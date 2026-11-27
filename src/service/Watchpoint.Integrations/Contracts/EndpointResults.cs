using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Watchpoint.Api.Contracts;
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
    public static async ValueTask<object?> LoopbackOnlyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            return Results.NotFound();
        }

        return await next(context).ConfigureAwait(false);
    }

    public static IResult Problem(Error error) => HttpResults.Problem(error);

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
