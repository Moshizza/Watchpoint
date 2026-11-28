using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Api.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.IntegrationTests.Testing;

/// <summary>
/// The API framework for module test nodes that run without IDN: one fixed administrator token from the desktop
/// client (<see cref="AdminToken"/>). Real sign-in through IDN is covered by the API integration tests (API-I-001).
/// </summary>
internal static class TestApi
{
    public const string AdminToken = "test-admin-token";

    public static IServiceCollection AddTestApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ITokenService, FixedTokens>();
        services.AddSingleton<ISessionRegistry, NoSessions>();
        return services.AddWatchpointApi(configuration, new SystemInfo(Guid.CreateVersion7(), "test", "development", "1.0.0", [], []), isProduction: false);
    }

    public static HttpClient WithAdminToken(this HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private sealed class FixedTokens : ITokenService
    {
        private static readonly AccessTokenClaims _admin = new(UserId.New(), SessionId.New(), ClientKind.Desktop, null, true, "en", "urn:test", "watchpoint",
            DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, "test");

        public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Session session, User user) => throw new NotSupportedException();

        public Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(string jwt) =>
            Task.FromResult(jwt == AdminToken ? Result.Success(_admin) : (Result<AccessTokenClaims>)IdentityErrors.TokenInvalid());
    }

    private sealed class NoSessions : ISessionRegistry
    {
        public IReadOnlyList<Session> ListActive(SessionQuery query) => [];

        public Result<Session> Get(SessionId id) => throw new NotSupportedException();

        public Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RevokeWhereAsync(Func<Session, bool> predicate, string reason, CancellationToken ct) => throw new NotSupportedException();

        public bool IsRevoked(SessionId id) => false;

        public void Touch(SessionId id)
        {
        }
    }
}
