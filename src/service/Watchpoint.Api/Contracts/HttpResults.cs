using System.Globalization;
using Microsoft.AspNetCore.Http;
using Watchpoint.Api.Internal.Pipeline;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Contracts;

/// <summary>Maps <see cref="Result"/> values to HTTP results (spec API §5.4, README §6.2).</summary>
public static class HttpResults
{
    /// <summary>The HTTP status of an error: by kind (README §6.2); <c>common.version.conflict</c> is always 412.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The status code.</returns>
    public static int StatusOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == CommonErrorCodes.VersionConflict)
        {
            return StatusCodes.Status412PreconditionFailed;
        }

        if (error.Code.Value == "common.version.required")
        {
            return StatusCodes.Status428PreconditionRequired;
        }

        if (error.Code.Value == "api.request.too_large")
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        return error.Kind switch
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
    }

    /// <summary>A problem details response for <paramref name="error"/>.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static IResult Problem(Error error) => new ProblemResult(error);

    /// <summary>Maps a result: success → <paramref name="onSuccess"/>, failure → problem details.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="result">The result.</param>
    /// <param name="onSuccess">The success mapping.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult From<T>(Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.TryGetValue(out var value, out var error) ? onSuccess(value) : Problem(error);
    }

    /// <summary>Maps a result without value: success → <paramref name="onSuccess"/> (204 by default), failure → problem details.</summary>
    /// <param name="result">The result.</param>
    /// <param name="onSuccess">The success mapping.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult From(Result result, Func<IResult>? onSuccess = null)
    {
        return result.IsSuccess ? (onSuccess?.Invoke() ?? Results.NoContent()) : Problem(result.Error);
    }

    /// <summary>200 with <paramref name="value"/> and <c>ETag: "&lt;version&gt;"</c> (README §6.1).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The body.</param>
    /// <param name="version">The entity version.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult Versioned<T>(T value, long version) => new VersionedResult<T>(value, version, StatusCodes.Status200OK, null);

    /// <summary>201 with <paramref name="value"/>, <c>Location</c> and <c>ETag</c>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="location">The URL of the created resource.</param>
    /// <param name="value">The body.</param>
    /// <param name="version">The entity version.</param>
    /// <returns>The HTTP result.</returns>
    public static IResult CreatedVersioned<T>(string location, T value, long version) => new VersionedResult<T>(value, version, StatusCodes.Status201Created, location);

    /// <summary>The <c>ETag</c> value of a version: <c>"&lt;version&gt;"</c>.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The header value.</returns>
    public static string ETagOf(long version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    private sealed class VersionedResult<T>(T value, long version, int status, string? location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = ETagOf(version);
            if (location is not null)
            {
                httpContext.Response.Headers.Location = location;
            }

            httpContext.Response.StatusCode = status;
            return httpContext.Response.WriteAsJsonAsync(value, ApiJson.Options, httpContext.RequestAborted);
        }
    }
}
