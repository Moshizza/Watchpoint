using Microsoft.AspNetCore.Http;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Internal.Pipeline;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Api.Internal.Auth;

/// <summary>
/// Authentication and session touch (spec API §4 rows 7–8): a bearer token (or the <c>access_token</c> query parameter,
/// only on <c>/hub/v1</c>) is validated by IDN; an invalid, expired or revoked token answers 401 with its
/// <c>identity.token.*</c> code. No token → anonymous (endpoints without <c>AllowAnonymous</c> then answer 401).
/// A valid token touches its session once per request.
/// </summary>
internal sealed class AuthenticationMiddleware(RequestDelegate next, ITokenService tokens, ISessionRegistry sessions, IPermissionEvaluator evaluator)
{
    public const string HubPath = "/hub/v1";

    public async Task InvokeAsync(HttpContext context)
    {
        var token = ReadToken(context.Request);
        if (token is null)
        {
            context.Features.Set<ICurrentUser>(CurrentUser.Anonymous(evaluator));
            await next(context).ConfigureAwait(false);
            return;
        }

        var validated = await tokens.ValidateAccessTokenAsync(token).ConfigureAwait(false);
        if (!validated.TryGetValue(out var claims, out var error))
        {
            await ProblemDetailsWriter.WriteAsync(context, error).ConfigureAwait(false);
            return;
        }

        if (sessions.IsRevoked(claims.SessionId))
        {
            await ProblemDetailsWriter.WriteAsync(context, IdentityErrors.TokenRevoked()).ConfigureAwait(false);
            return;
        }

        context.Features.Set<ICurrentUser>(CurrentUser.Of(evaluator, claims));
        sessions.Touch(claims.SessionId);
        await next(context).ConfigureAwait(false);
    }

    internal static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        if (request.Path.StartsWithSegments(HubPath, StringComparison.OrdinalIgnoreCase)
            && request.Query.TryGetValue("access_token", out var query) && !string.IsNullOrEmpty(query.ToString()))
        {
            return query.ToString();
        }

        return null;
    }
}
