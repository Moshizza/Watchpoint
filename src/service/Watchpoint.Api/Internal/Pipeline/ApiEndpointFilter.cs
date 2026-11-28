using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Internal.Idempotency;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Pipeline;

/// <summary>
/// The framework's endpoint filter (spec API §4 row 9): permission, client kind, version precondition and
/// idempotency, in that order, driven by the metadata of <see cref="EndpointConventions"/>. Installed once per endpoint.
/// </summary>
internal static partial class ApiEndpointFilter
{
    private sealed class Installed
    {
        public static readonly Installed Instance = new();
    }

    public static void Install(EndpointBuilder endpoint)
    {
        if (endpoint.Metadata.Contains(Installed.Instance))
        {
            return;
        }

        endpoint.Metadata.Add(Installed.Instance);
        endpoint.FilterFactories.Add((_, next) => context => InvokeAsync(context, next));
    }

    public static async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var metadata = context.GetEndpoint()?.Metadata ?? new EndpointMetadataCollection();
        var user = context.CurrentUser();

        if (metadata.GetMetadata<PermissionRequirement>() is { } required)
        {
            if (!user.IsAuthenticated)
            {
                return HttpResults.Problem(CommonErrors.Unauthenticated());
            }

            if (!await user.HasPermissionAsync(required.Permission, context.RequestAborted).ConfigureAwait(false))
            {
                return HttpResults.Problem(CommonErrors.Forbidden(required.Permission));
            }
        }

        if (metadata.GetMetadata<AuthenticationRequired>() is not null && !user.IsAuthenticated)
        {
            return HttpResults.Problem(CommonErrors.Unauthenticated());
        }

        if (metadata.GetMetadata<ConfigurationEndpointMarker>() is not null && user.ClientKind != Identity.Contracts.ClientKind.Desktop)
        {
            return HttpResults.Problem(ApiErrors.ClientKindNotAllowed("desktop"));
        }

        if (metadata.GetMetadata<AllowedClientKinds>() is { } kinds && !kinds.Kinds.Contains(user.ClientKind))
        {
            return HttpResults.Problem(ApiErrors.ClientKindNotAllowed(string.Join(',', kinds.Kinds.Select(k => k.ToString().ToLowerInvariant()))));
        }

        if (metadata.GetMetadata<VersionedMarker>() is not null)
        {
            var ifMatch = context.Request.Headers.IfMatch.ToString();
            if (ifMatch.Length > 0)
            {
                if (ApiRequest.ParseETag(ifMatch) is not { } expected)
                {
                    return HttpResults.Problem(ApiErrors.VersionInvalid());
                }

                context.Items[ApiRequest.ExpectedVersionKey] = expected;
            }
            else if (HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
            {
                return HttpResults.Problem(ApiErrors.VersionRequired());
            }
        }

        if (metadata.GetMetadata<IdempotentMarker>() is not null && context.Request.Headers.TryGetValue(IdempotencyHeader, out var keyValues))
        {
            return await IdempotentAsync(invocation, next, keyValues.ToString(), user).ConfigureAwait(false);
        }

        return await next(invocation).ConfigureAwait(false);
    }

    public const string IdempotencyHeader = "Idempotency-Key";

    public const string ReplayedHeader = "Idempotent-Replayed";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static async ValueTask<object?> IdempotentAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next, string key, ICurrentUser user)
    {
        var context = invocation.HttpContext;
        if (!KeyPattern().IsMatch(key))
        {
            return HttpResults.Problem(ApiErrors.IdempotencyKeyInvalid());
        }

        var store = context.RequestServices.GetRequiredService<IIdempotencyStore>();
        var time = context.RequestServices.GetRequiredService<TimeProvider>();
        context.Request.EnableBuffering();
        string hash;
        context.Request.Body.Position = 0;
        using (var sha = SHA256.Create())
        {
            hash = Convert.ToHexStringLower(await sha.ComputeHashAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false));
        }

        context.Request.Body.Position = 0;
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? string.Empty;
        var request = new IdempotencyRequest(user.UserId, key, context.Request.Method, route + " " + context.Request.Path.Value, hash);
        var begin = await store.BeginAsync(request, context.RequestAborted).ConfigureAwait(false);
        switch (begin.Kind)
        {
            case IdempotencyBeginKind.Mismatch:
                return HttpResults.Problem(ApiErrors.IdempotencyMismatch());
            case IdempotencyBeginKind.InProgress:
                return HttpResults.Problem(ApiErrors.IdempotencyInProgress());
            case IdempotencyBeginKind.Replay:
                return new ReplayResult(begin.Response!);
        }

        try
        {
            var result = await next(invocation).ConfigureAwait(false);
            var original = context.Response.Body;
            using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await ExecuteAsync(result, context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = original;
            }

            var body = buffer.ToArray();
            if (context.Response.StatusCode < 500)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "Content-Type", "Location", "ETag" })
                {
                    if (context.Response.Headers.TryGetValue(name, out var value))
                    {
                        headers[name] = value.ToString();
                    }
                }

                await store.CompleteAsync(request, new StoredResponse(context.Response.StatusCode, body, headers, time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await store.AbandonAsync(request, CancellationToken.None).ConfigureAwait(false);
            }

            await original.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        }
        catch
        {
            await store.AbandonAsync(request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Task ExecuteAsync(object? result, HttpContext context) => result switch
    {
        IResult r => r.ExecuteAsync(context),
        null => Task.CompletedTask,
        string s => context.Response.WriteAsync(s, context.RequestAborted),
        _ => context.Response.WriteAsJsonAsync(result, result.GetType(), ApiJson.Options, context.RequestAborted),
    };

    private sealed class ReplayResult(StoredResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = response.Status;
            foreach (var (name, value) in response.Headers)
            {
                httpContext.Response.Headers[name] = value;
            }

            httpContext.Response.Headers[ReplayedHeader] = "true";
            await httpContext.Response.Body.WriteAsync(response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
