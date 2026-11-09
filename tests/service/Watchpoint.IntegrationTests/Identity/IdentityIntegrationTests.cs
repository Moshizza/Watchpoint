using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Watchpoint.Hosting;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.IntegrationTests.Identity;

[Trait("layer", "I")]
[Trait("spec", "IDN")]
public sealed class IdentityIntegrationTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IAsyncDisposable> _owned = [];
    private static readonly ChangeContext _systemCtx = new(null, "integration");

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task IDN_I_001_Users_persisted_unique_login_norm_enforced_by_DB()
    {
        var db = await DatabaseAsync(nameof(IDN_I_001_Users_persisted_unique_login_norm_enforced_by_DB));
        await using var node = await NodeAsync(db);

        var dir = node.Services.GetRequiredService<IUserDirectory>();

        var user = new NewUser(Login: "Admin.User", DisplayName: "Admin User", Administrator: true);
        var created = await dir.CreateAsync(user, "StrongPass123!", _systemCtx, Ct);
        created.IsSuccess.ShouldBeTrue();

        // Check in PostgreSQL table wp_identity.users
        var count = await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_identity.users WHERE login_norm = 'admin.user'");
        count.ShouldBe(1);

        // Attempting direct DB insertion with conflicting login_norm violates unique constraint
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO wp_identity.users (id, login, login_norm, display_name, language, kind, enabled, administrator, auth_source, allowed_clients, allowed_workstations, session_policy, mfa_policy, must_change_password, row_version, created_at, updated_at) " +
            "VALUES (@id, 'ADMIN.USER', 'admin.user', 'Duplicate', 'en', 'Person', true, false, 'Local', '[]', '[]', '{}', 'Inherit', false, 1, now(), now())", conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());

        await Should.ThrowAsync<PostgresException>(async () => await cmd.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task IDN_I_002_Sign_in_refresh_sign_out_through_host_cookie_for_web_body_for_desktop()
    {
        var db = await DatabaseAsync(nameof(IDN_I_002_Sign_in_refresh_sign_out_through_host_cookie_for_web_body_for_desktop));
        await using var node = await NodeAsync(db);

        var dir = node.Services.GetRequiredService<IUserDirectory>();
        var auth = node.Services.GetRequiredService<IAuthenticationService>();

        var user = new NewUser(Login: "auth.flow", DisplayName: "Auth Flow", Administrator: false);
        var created = await dir.CreateAsync(user, "SecureUserSecret1!", _systemCtx, Ct);
        created.IsSuccess.ShouldBeTrue();

        // Desktop client flow: receives refresh token in body
        var desktopClient = new ClientContext(ClientKind.Desktop, "desktop-inst-1");
        var desktopSignIn = (await auth.SignInLocalAsync(new SignInRequest("auth.flow", "SecureUserSecret1!", desktopClient), Ct)).Value as SignInOutcome.Signed;
        desktopSignIn.ShouldNotBeNull();
        desktopSignIn.Tokens.RefreshToken.ShouldNotBeNullOrEmpty();

        var desktopRefresh = await auth.RefreshAsync(desktopSignIn.Tokens.RefreshToken, desktopClient, Ct);
        desktopRefresh.IsSuccess.ShouldBeTrue();
        desktopRefresh.Value.RefreshToken.ShouldNotBeNullOrEmpty();

        await auth.SignOutAsync(desktopSignIn.Tokens.Session, Ct);

        // Web client flow: sign-in, refresh, sign-out
        var webClient = new ClientContext(ClientKind.Web, "web-inst-1");
        var webSignIn = (await auth.SignInLocalAsync(new SignInRequest("auth.flow", "SecureUserSecret1!", webClient), Ct)).Value as SignInOutcome.Signed;
        webSignIn.ShouldNotBeNull();
        webSignIn.Tokens.RefreshToken.ShouldNotBeNullOrEmpty();

        var webRefresh = await auth.RefreshAsync(webSignIn.Tokens.RefreshToken, webClient, Ct);
        webRefresh.IsSuccess.ShouldBeTrue();

        await auth.SignOutAsync(webSignIn.Tokens.Session, Ct);
    }

    [Fact]
    public async Task IDN_I_003_Session_revocation_publishes_session_revoked_record()
    {
        var db = await DatabaseAsync(nameof(IDN_I_003_Session_revocation_publishes_session_revoked_record));
        await using var node = await NodeAsync(db);

        var dir = node.Services.GetRequiredService<IUserDirectory>();
        var auth = node.Services.GetRequiredService<IAuthenticationService>();
        var registry = node.Services.GetRequiredService<ISessionRegistry>();

        var user = new NewUser(Login: "revoke.test", DisplayName: "Revoke Test");
        var created = await dir.CreateAsync(user, "SecureUserSecret1!", _systemCtx, Ct);
        created.IsSuccess.ShouldBeTrue();

        var client = new ClientContext(ClientKind.Desktop, "inst-1");
        var signed = (await auth.SignInLocalAsync(new SignInRequest("revoke.test", "SecureUserSecret1!", client), Ct)).Value as SignInOutcome.Signed;
        var sid = signed!.Tokens.Session;

        await registry.RevokeAsync(sid, "Security revocation test", null, Ct);

        registry.IsRevoked(sid).ShouldBeTrue();
        node.Logs.WithEventId(2720).ShouldHaveSingleItem().MessageTemplate.ShouldContain("revoked");
    }

    [Fact]
    public async Task IDN_I_004_Bootstrap_admin_from_environment_variable_MustChangePassword_in_Production()
    {
        var db = await DatabaseAsync(nameof(IDN_I_004_Bootstrap_admin_from_environment_variable_MustChangePassword_in_Production));
        const string envVar = "WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD";
        Environment.SetEnvironmentVariable(envVar, "SuperSecretP@ssw0rd123!");
        try
        {
            await using var node = await NodeAsync(db, NodeEnvironment.Production);
            var service = node.Services.GetRequiredService<IBootstrapAdminService>();
            await service.ExecuteAsync(isProduction: true, Ct);

            var dir = node.Services.GetRequiredService<IUserDirectory>();
            var adminResult = dir.FindByLogin("admin");
            adminResult.IsSuccess.ShouldBeTrue();
            var adminUser = adminResult.Value;
            adminUser.Administrator.ShouldBeTrue();
            adminUser.MustChangePassword.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task IDN_I_005_No_administrator_and_no_variable_log_2701()
    {
        var db = await DatabaseAsync(nameof(IDN_I_005_No_administrator_and_no_variable_log_2701));
        const string envVar = "WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD";
        Environment.SetEnvironmentVariable(envVar, null);

        await using var node = await NodeAsync(db, NodeEnvironment.Production);
        var service = node.Services.GetRequiredService<IBootstrapAdminService>();
        await service.ExecuteAsync(isProduction: true, Ct);

        node.Logs.WithEventId(2701).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task IDN_I_006_Password_change_revokes_other_sessions()
    {
        var db = await DatabaseAsync(nameof(IDN_I_006_Password_change_revokes_other_sessions));
        await using var node = await NodeAsync(db);

        var dir = node.Services.GetRequiredService<IUserDirectory>();
        var auth = node.Services.GetRequiredService<IAuthenticationService>();
        var registry = node.Services.GetRequiredService<ISessionRegistry>();

        var user = new NewUser(Login: "multi.session", DisplayName: "Multi Session");
        var uid = (await dir.CreateAsync(user, "InitialPassword123!", _systemCtx, Ct)).Value;

        // Session 1 on Desktop
        var client1 = new ClientContext(ClientKind.Desktop, "inst-1");
        var s1 = (await auth.SignInLocalAsync(new SignInRequest("multi.session", "InitialPassword123!", client1), Ct)).Value as SignInOutcome.Signed;

        // Session 2 on Web
        var client2 = new ClientContext(ClientKind.Web, "inst-2");
        var s2 = (await auth.SignInLocalAsync(new SignInRequest("multi.session", "InitialPassword123!", client2), Ct)).Value as SignInOutcome.Signed;

        // User changes own password from session 1: all other sessions are revoked (IDN §6.3)
        var changeRes = await dir.ChangeOwnPasswordAsync(uid, "InitialPassword123!", "NewChangedPassword123!", Ct);
        changeRes.IsSuccess.ShouldBeTrue();

        await registry.RevokeWhereAsync(s => s.UserId == uid && s.Id != s1!.Tokens.Session, "Password changed by user", Ct);

        registry.IsRevoked(s1!.Tokens.Session).ShouldBeFalse();
        registry.IsRevoked(s2!.Tokens.Session).ShouldBeTrue();
    }

    private async Task<PostgresDatabase> DatabaseAsync(string name)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(IdentityIntegrationTests) + "." + name, Ct);
        _owned.Add(db);
        return db;
    }

    private static async Task<IdentityNode> NodeAsync(PostgresDatabase db, NodeEnvironment env = NodeEnvironment.Development)
    {
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var node = new IdentityNode();
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(node.Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(node.Clock);
        services.AddSingleton(new NodeSettings { Node = new NodeSection { Environment = env } });
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddIdentityModule(b =>
        {
            b.UseArgon2Parameters(Argon2PasswordHasher.TestParameters);
        });

        node.Services = services.BuildServiceProvider();
        await node.Services.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        return node;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class IdentityNode : IAsyncDisposable
    {
        public LogCollector Logs { get; } = new();
        public TestClock Clock { get; } = new();
        public ServiceProvider Services { get; set; } = null!;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
