using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Api.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Api.Tests.Support;

/// <summary>A test server running the API pipeline with fake IDN services and endpoints mapped by the test.</summary>
internal sealed class ApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ApiHost(WebApplication app, FakeTokens tokens, FakeSessions sessions, LogCollector logs, FakeTimeProvider time)
    {
        _app = app;
        Tokens = tokens;
        Sessions = sessions;
        Logs = logs;
        Time = time;
        Server = app.GetTestServer();
        Client = Server.CreateClient();
    }

    public FakeTokens Tokens { get; }

    public FakeSessions Sessions { get; }

    public LogCollector Logs { get; }

    public FakeTimeProvider Time { get; }

    public TestServer Server { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ApiHost> StartAsync(
        Action<IEndpointRouteBuilder> map,
        IReadOnlyDictionary<string, string?>? settings = null,
        bool production = false,
        Action<IServiceCollection>? services = null,
        Action<IApplicationBuilder>? before = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
        var logs = new LogCollector();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(logs);
        var tokens = new FakeTokens();
        var sessions = new FakeSessions();
        var time = TestTime.CreateProvider();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<ITokenService>(tokens);
        builder.Services.AddSingleton<ISessionRegistry>(sessions);
        services?.Invoke(builder.Services);
        builder.Services.AddWatchpointApi(builder.Configuration,
            new SystemInfo(Guid.CreateVersion7(), "node-1", production ? "production" : "development", "1.2.3", ["registry", "state"], ["https://node-1:8443"]),
            production);
        var app = builder.Build();
        before?.Invoke(app);
        app.UseWatchpointApi();
        app.MapWatchpointApi();
        map(app);
        await app.StartAsync();
        return new ApiHost(app, tokens, sessions, logs, time);
    }

    /// <summary>A client with a bearer token for a new session of a (fake) user.</summary>
    public HttpClient ClientFor(bool admin = false, ClientKind kind = ClientKind.Desktop, UserId? user = null)
    {
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Issue(admin, kind, user));
        return client;
    }

    public IEnumerable<Microsoft.AspNetCore.Http.Endpoint> Endpoints =>
        ((IEndpointRouteBuilder)_app).DataSources.SelectMany(d => d.Endpoints);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Logs.Dispose();
    }
}

/// <summary>Tokens are opaque keys of claims held in memory; "expired" and "garbage" give the IDN errors.</summary>
internal sealed class FakeTokens : ITokenService
{
    private readonly Dictionary<string, AccessTokenClaims> _issued = new(StringComparer.Ordinal);

    public string Issue(bool admin, ClientKind kind, UserId? user = null)
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        var now = TestTime.Start;
        lock (_issued)
        {
            _issued[token] = new AccessTokenClaims(user ?? UserId.New(), SessionId.New(), kind, null, admin, "fr", "urn:test", "watchpoint", now, now.AddMinutes(10), token);
        }

        return token;
    }

    public AccessTokenClaims ClaimsOf(string token)
    {
        lock (_issued)
        {
            return _issued[token];
        }
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Session session, User user) => throw new NotSupportedException();

    public Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(string jwt)
    {
        if (jwt == "expired")
        {
            return Task.FromResult<Result<AccessTokenClaims>>(IdentityErrors.TokenExpired());
        }

        lock (_issued)
        {
            return Task.FromResult(_issued.TryGetValue(jwt, out var claims) ? Result.Success(claims) : (Result<AccessTokenClaims>)IdentityErrors.TokenInvalid());
        }
    }
}

/// <summary>Counts touches; sessions listed in <see cref="Revoked"/> are revoked.</summary>
internal sealed class FakeSessions : ISessionRegistry
{
    private int _touches;

    public HashSet<SessionId> Revoked { get; } = [];

    public int Touches => Volatile.Read(ref _touches);

    public IReadOnlyList<Session> ListActive(SessionQuery query) => [];

    public Result<Session> Get(SessionId id) => throw new NotSupportedException();

    public Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> RevokeWhereAsync(Func<Session, bool> predicate, string reason, CancellationToken ct) => throw new NotSupportedException();

    public bool IsRevoked(SessionId id) => Revoked.Contains(id);

    public void Touch(SessionId id) => Interlocked.Increment(ref _touches);
}
