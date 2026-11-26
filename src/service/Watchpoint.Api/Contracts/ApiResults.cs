using Microsoft.AspNetCore.Http;
using Watchpoint.Api.Internal.Platform;

namespace Watchpoint.Api.Contracts;

/// <summary>Framework results beyond <see cref="HttpResults"/>.</summary>
public static class ApiResults
{
    /// <summary>202 Accepted with <c>{ jobId, status: "running" }</c> and <c>Location: /api/v1/jobs/{id}</c> (spec API §5.6).</summary>
    /// <param name="job">The started job.</param>
    /// <returns>The result.</returns>
    public static IResult Accepted(JobInfo job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new WithLocation(Results.Json(JobDto.Of(job), ApiJson.Options, statusCode: StatusCodes.Status202Accepted), "/api/v1/jobs/" + job.Id);
    }

    /// <summary>200 with a page <c>{ items, nextCursor }</c> (README §6.1).</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items.</param>
    /// <param name="nextCursor">The cursor of the next page.</param>
    /// <returns>The result.</returns>
    public static IResult Page<T>(IReadOnlyList<T> items, string? nextCursor) => Results.Json(new PageDto<T>(items, nextCursor), ApiJson.Options);

    /// <summary>200 with <paramref name="value"/> serialised with <see cref="ApiJson.Options"/>.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The body.</param>
    /// <returns>The result.</returns>
    public static IResult Ok<T>(T value) => Results.Json(value, ApiJson.Options);

    private sealed class WithLocation(IResult inner, string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            return inner.ExecuteAsync(httpContext);
        }
    }
}

/// <summary>A page of items (README §6.1).</summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Items">The items.</param>
/// <param name="NextCursor">The cursor of the next page, <see langword="null"/> on the last page.</param>
public sealed record PageDto<T>(IReadOnlyList<T> Items, string? NextCursor);
