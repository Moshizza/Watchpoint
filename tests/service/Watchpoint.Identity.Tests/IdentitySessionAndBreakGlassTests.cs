using System.Net;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Mfa;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Identity.Tests;

/// <summary>T3.08: break-glass (§11), client kind binding and admission (§12), lockout and sessions — IDN-U-062…IDN-U-080, IDN-U-134.</summary>
[Trait("layer", "U")]
[Trait("spec", "IDN")]
public sealed class IdentitySessionAndBreakGlassTests : IDisposable
{
    private const string Password = "ValidPassword123!";
    private const string BreakGlassPassword = "Sealed-Envelope-Password-7731!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ChangeContext _systemCtx = new(null, "test");
    private static readonly ClientContext _desktop = new(ClientKind.Desktop, "inst-1", RemoteIp: IPAddress.Parse("10.1.2.3"));
    private static readonly ClientContext _web = new(ClientKind.Web, "web-1", RemoteIp: IPAddress.Parse("10.1.2.4"), BrowserOrigin: "https://watchpoint.example.com");
    private readonly TestIdentityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IDN_U_062_Break_glass_is_local_needs_a_reason_desktop_only_and_two_hour_sessions()
    {
        // Local authentication only: a directory or OIDC break-glass account cannot be created.
        (await _harness.UserDirectory.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass, AuthSource: AuthSource.Directory, ExternalId: "cn=x"), null, _systemCtx, Ct))
            .Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");
        var bg = await BreakGlassAsync();
        bg.Administrator.ShouldBeTrue();
        bg.AuthSource.ShouldBe(AuthSource.Local);
        var auth = _harness.CreateAuthService();

        // Reason required, only after the password is right (nothing is learnt without it).
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", "WrongPassword123!", _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
        var noReason = await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop), Ct);
        noReason.Error.FieldErrors[0].Code.Value.ShouldBe("identity.signin.reason_required");
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "   "), Ct)).IsSuccess.ShouldBeFalse();
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, new string('x', 513)), Ct)).IsSuccess.ShouldBeFalse();

        // Desktop only.
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _web, "Directory down"), Ct)).Error.Code.Value.ShouldBe("identity.signin.client_not_allowed");
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, new ClientContext(ClientKind.Mobile, "m-1"), "Directory down"), Ct)).Error.Code.Value.ShouldBe("identity.signin.client_not_allowed");
        _harness.SessionRegistry.ListActive(new SessionQuery(bg.Id)).ShouldBeEmpty();

        // Desktop with a reason: signed in, session limited to two hours, access token too.
        var signed = (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Directory down, door 7 stuck"), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        var session = _harness.SessionRegistry.Get(signed.Tokens.Session).Value;
        session.ExpiresAt.ShouldBe(_harness.Clock.GetUtcNow().AddHours(2));
        signed.Tokens.RefreshExpiresAt.ShouldBeLessThanOrEqualTo(session.ExpiresAt);
        _harness.Clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(5));
        var late = (await auth.RefreshAsync(signed.Tokens.RefreshToken, _desktop, Ct)).Value;
        late.AccessExpiresAt.ShouldBe(session.ExpiresAt);
        _harness.Clock.Advance(TimeSpan.FromMinutes(6));
        (await auth.RefreshAsync(late.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_063_Break_glass_use_produces_a_critical_audit_record_and_a_system_event()
    {
        var bg = await BreakGlassAsync();
        using var logs = new LogCollector();
        using var loggers = new LoggerFactory([logs]);
        var auth = _harness.CreateAuthService(logger: loggers.CreateLogger<AuthenticationService>());

        var signed = (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Primary admin unavailable"), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();

        var record = _harness.Audit.Of("identity.breakGlassUsed").Single(r => r.Outcome == IdentityAuditOutcome.Success);
        record.Severity.ShouldBe(IdentityAuditSeverity.Critical);
        record.Actor.ShouldBe(bg.Id);
        record.Reason.ShouldBe("Primary admin unavailable");
        record.Details["remoteIp"].ShouldBe("10.1.2.3");
        record.Details["sessionId"].ShouldBe(signed.Tokens.Session.ToString());

        var systemEvent = _harness.Events.Of("identity.breakGlassUsed").Single(e => e.Payload["outcome"] == "Success");
        systemEvent.Severity.ShouldBe(IdentitySystemEventSeverity.Alarm);
        systemEvent.Payload["userId"].ShouldBe(bg.Id.ToString());
        systemEvent.Payload["reason"].ShouldBe("Primary admin unavailable");

        var log = logs.WithEventId(2730).ShouldHaveSingleItem();
        log.RenderedMessage.ShouldContain("10.1.2.3");
        log.RenderedMessage.ShouldContain("Primary admin unavailable");

        // A refused attempt (no reason) is recorded too, as denied.
        await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop), Ct);
        _harness.Audit.Of("identity.breakGlassUsed").Count(r => r.Outcome == IdentityAuditOutcome.Denied).ShouldBe(1);
        _harness.Events.Of("identity.breakGlassUsed").Count().ShouldBe(2);
    }

    [Fact]
    public async Task IDN_U_064_Break_glass_bypasses_mfa_but_not_rate_limits_or_lockout()
    {
        using var everyone = new TestIdentityHarness(mfaOptions: new MfaOptions(MfaRequirement.Everyone));
        var bgId = (await everyone.UserDirectory.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass), BreakGlassPassword, _systemCtx, Ct)).Value;
        var adminId = (await everyone.UserDirectory.CreateAsync(new NewUser("site.admin", "Admin", Administrator: true), Password, _systemCtx, Ct)).Value;
        var auth = everyone.CreateAuthService(new SignInRateLimiter(everyone.Clock, new SignInRateLimiterOptions(RatePerIp: 100, RatePerLogin: 3)));

        // Everyone needs MFA, the break-glass account signs in without any.
        (await auth.SignInLocalAsync(new SignInRequest("site.admin", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaEnrolmentRequired>();
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await everyone.Mfa.IsRequiredAsync(everyone.UserDirectory.Get(bgId).Value, Ct)).ShouldBeFalse();

        // Rate limit per login applies (3 per minute here): the fourth attempt is refused whatever the password.
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).IsSuccess.ShouldBeTrue();
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).IsSuccess.ShouldBeTrue();
        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).Error.Code.Value.ShouldBe("common.rate_limited");
        everyone.Clock.Advance(TimeSpan.FromMinutes(2));

        // Lockout applies: five wrong passwords lock it; the right password then says so.
        for (var i = 0; i < 5; i++)
        {
            (await auth.SignInLocalAsync(new SignInRequest("breakglass", "Guess" + i + "Password123!", _desktop, "Emergency"), Ct)).Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
            everyone.Clock.Advance(TimeSpan.FromSeconds(61));
        }

        (await auth.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).Error.Code.Value.ShouldBe("identity.signin.locked");
        _ = adminId;
    }

    [Fact]
    public async Task IDN_U_065_Client_kind_claim_comes_from_the_request_and_a_browser_cannot_claim_desktop()
    {
        var user = await UserAsync("kinds.user");
        var auth = _harness.CreateAuthService();

        var web = (await auth.SignInLocalAsync(new SignInRequest("kinds.user", Password, _web), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await _harness.TokenService.ValidateAccessTokenAsync(web.Tokens.AccessToken)).Value.ClientKind.ShouldBe(ClientKind.Web);
        var desktop = (await auth.SignInLocalAsync(new SignInRequest("kinds.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await _harness.TokenService.ValidateAccessTokenAsync(desktop.Tokens.AccessToken)).Value.ClientKind.ShouldBe(ClientKind.Desktop);
        _harness.SessionRegistry.Get(desktop.Tokens.Session).Value.WorkstationVerified.ShouldBeFalse();

        // A request made by a browser (origin present) may only be the Web client: desktop, mobile and API are refused.
        foreach (var kind in new[] { ClientKind.Desktop, ClientKind.Mobile, ClientKind.Api })
        {
            var fromBrowser = _web with { Kind = kind, ClientInstanceId = "inst-" + kind };
            (await auth.SignInLocalAsync(new SignInRequest("kinds.user", Password, fromBrowser), Ct)).Error.Code.Value.ShouldBe("identity.signin.client_not_allowed");
        }

        // Nor can a browser refresh a desktop session (the cookie path only ever carries the web session).
        (await auth.RefreshAsync(desktop.Tokens.RefreshToken, _desktop with { BrowserOrigin = "https://watchpoint.example.com" }, Ct)).Error.Code.Value.ShouldBe("identity.refresh.invalid");
        (await auth.RefreshAsync(desktop.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue("the refusal did not burn the token");
        (await auth.RefreshAsync(web.Tokens.RefreshToken, _web, Ct)).IsSuccess.ShouldBeTrue();
        _ = user;
    }

    [Fact]
    public async Task IDN_U_066_Verified_workstation_is_carried_by_the_session_and_the_token()
    {
        var user = await UserAsync("wks.user");
        var auth = _harness.CreateAuthService();
        var verified = _desktop with { WorkstationId = "WKS-7", WorkstationVerified = true };

        var signed = (await auth.SignInLocalAsync(new SignInRequest("wks.user", Password, verified), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        var session = _harness.SessionRegistry.Get(signed.Tokens.Session).Value;
        session.WorkstationVerified.ShouldBeTrue();
        session.WorkstationId.ShouldBe("WKS-7");
        var claims = (await _harness.TokenService.ValidateAccessTokenAsync(signed.Tokens.AccessToken)).Value;
        claims.WorkstationVerified.ShouldBeTrue();
        claims.WorkstationId.ShouldBe("WKS-7");

        // It survives a refresh and a restart of the registry (persisted).
        var refreshed = (await auth.RefreshAsync(signed.Tokens.RefreshToken, verified, Ct)).Value;
        (await _harness.TokenService.ValidateAccessTokenAsync(refreshed.AccessToken)).Value.WorkstationVerified.ShouldBeTrue();
        var reloaded = new SessionRegistry(_harness.DbFactory, _harness.Clock);
        reloaded.Get(signed.Tokens.Session).Value.WorkstationVerified.ShouldBeTrue();

        // Only the desktop client can be verified: the flag is ignored for the others; an unverified desktop stays false.
        var mobile = (await auth.SignInLocalAsync(new SignInRequest("wks.user", Password, new ClientContext(ClientKind.Mobile, "m-1", WorkstationVerified: true)), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await _harness.TokenService.ValidateAccessTokenAsync(mobile.Tokens.AccessToken)).Value.WorkstationVerified.ShouldBeFalse();
        var plain = (await auth.SignInLocalAsync(new SignInRequest("wks.user", Password, _desktop with { WorkstationId = "WKS-7" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await _harness.TokenService.ValidateAccessTokenAsync(plain.Tokens.AccessToken)).Value.WorkstationVerified.ShouldBeFalse();
        _ = user;
    }

    [Fact]
    public async Task IDN_U_067_Enrolled_workstation_required_for_configuration_sessions_when_enabled()
    {
        var admin = await UserAsync("cfg.admin", administrator: true);
        var configUser = await UserAsync("cfg.user");
        _harness.Permissions.Granted[configUser.Id] = ["config.devices.edit"];
        var operatorUser = await UserAsync("ops.user");
        _harness.Permissions.Granted[operatorUser.Id] = ["operate.alarms.view"];
        var bg = await BreakGlassAsync();
        var verified = _desktop with { WorkstationId = "WKS-1", WorkstationVerified = true };

        // Disabled (default): everybody signs in from an unverified desktop.
        var relaxed = _harness.CreateAuthService();
        (await relaxed.SignInLocalAsync(new SignInRequest("cfg.admin", Password, _desktop), Ct)).IsSuccess.ShouldBeTrue();

        // Enabled: administrators and configuration users need a verified workstation; operators and break-glass do not.
        _harness.ClientBinding = new ClientBindingOptions(RequireEnrolledWorkstationForConfiguration: true);
        var strict = _harness.CreateAuthService();
        (await strict.SignInLocalAsync(new SignInRequest("cfg.admin", Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.workstation_not_verified");
        (await strict.SignInLocalAsync(new SignInRequest("cfg.admin", Password, _web), Ct)).Error.Code.Value.ShouldBe("identity.signin.workstation_not_verified");
        (await strict.SignInLocalAsync(new SignInRequest("cfg.user", Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.workstation_not_verified");
        (await strict.SignInLocalAsync(new SignInRequest("cfg.admin", Password, verified), Ct)).IsSuccess.ShouldBeTrue();
        (await strict.SignInLocalAsync(new SignInRequest("cfg.user", Password, verified with { ClientInstanceId = "inst-2" }), Ct)).IsSuccess.ShouldBeTrue();
        (await strict.SignInLocalAsync(new SignInRequest("ops.user", Password, _desktop), Ct)).IsSuccess.ShouldBeTrue();
        (await strict.SignInLocalAsync(new SignInRequest("ops.user", Password, _web), Ct)).IsSuccess.ShouldBeTrue();
        (await strict.SignInLocalAsync(new SignInRequest("breakglass", BreakGlassPassword, _desktop, "Emergency"), Ct)).IsSuccess.ShouldBeTrue();
        _ = (admin, bg);
    }

    [Fact]
    public async Task IDN_U_068_Concurrent_session_limit_refuses_the_new_sign_in_and_keeps_the_old_sessions()
    {
        var id = (await _harness.UserDirectory.CreateAsync(new NewUser("limited.user", "Limited", SessionPolicy: new SessionPolicy(2, TimeSpan.FromMinutes(15), TimeSpan.FromDays(7))), Password, _systemCtx, Ct)).Value;
        var auth = _harness.CreateAuthService();

        var first = (await auth.SignInLocalAsync(new SignInRequest("limited.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        var second = (await auth.SignInLocalAsync(new SignInRequest("limited.user", Password, _desktop with { ClientInstanceId = "inst-2" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        var third = await auth.SignInLocalAsync(new SignInRequest("limited.user", Password, _desktop with { ClientInstanceId = "inst-3" }), Ct);
        third.Error.Code.Value.ShouldBe("identity.session.limit_reached");
        third.Error.Args["max"].ShouldBe(2);

        // The oldest session is not revoked: both keep working.
        _harness.SessionRegistry.ListActive(new SessionQuery(id)).Count.ShouldBe(2);
        (await auth.RefreshAsync(first.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();
        (await auth.RefreshAsync(second.Tokens.RefreshToken, _desktop with { ClientInstanceId = "inst-2" }, Ct)).IsSuccess.ShouldBeTrue();

        // Once one is signed out, a new one is admitted.
        await auth.SignOutAsync(first.Tokens.Session, Ct);
        (await auth.SignInLocalAsync(new SignInRequest("limited.user", Password, _desktop with { ClientInstanceId = "inst-3" }), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_069_Session_list_and_revoke_by_administrator()
    {
        var admin = await UserAsync("sess.admin", administrator: true);
        var alice = await UserAsync("sess.alice");
        var bob = await UserAsync("sess.bob");
        var auth = _harness.CreateAuthService();
        var aliceTokens = (await auth.SignInLocalAsync(new SignInRequest("sess.alice", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var aliceWeb = (await auth.SignInLocalAsync(new SignInRequest("sess.alice", Password, _web), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var bobTokens = (await auth.SignInLocalAsync(new SignInRequest("sess.bob", Password, _desktop with { ClientInstanceId = "bob-1" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var management = new SessionManagement(_harness.SessionRegistry, _harness.UserDirectory, _harness.Auditor, _harness.Permissions);

        // A user sees their own sessions only.
        (await management.ListAsync(alice.Id, alice.Id, Ct)).Value.Select(s => s.Id).ShouldBe([aliceTokens.Session, aliceWeb.Session], ignoreOrder: true);
        (await management.ListAsync(alice.Id, bob.Id, Ct)).Error.Code.Value.ShouldBe("identity.session.forbidden");
        (await management.ListAsync(alice.Id, null, Ct)).Error.Code.Value.ShouldBe("identity.session.forbidden");
        (await management.ListAsync(alice.Id, alice.Id, Ct)).Value.ShouldAllBe(s => s.UserId == alice.Id);

        // The administrator sees everyone's and revokes Bob's; the revocation is audited with the actor.
        (await management.ListAsync(admin.Id, null, Ct)).Value.Count.ShouldBe(3);
        (await management.ListAsync(admin.Id, bob.Id, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(bobTokens.Session);
        (await management.RevokeAsync(alice.Id, bobTokens.Session, "nope", Ct)).Error.Code.Value.ShouldBe("identity.session.forbidden");
        (await management.RevokeAsync(admin.Id, bobTokens.Session, "Workstation stolen", Ct)).IsSuccess.ShouldBeTrue();
        (await management.RevokeAsync(admin.Id, SessionId.New(), "x", Ct)).Error.Code.Value.ShouldBe("identity.session.not_found");

        // Revocation takes effect at the next refresh and for the access token at once.
        (await auth.RefreshAsync(bobTokens.RefreshToken, _desktop with { ClientInstanceId = "bob-1" }, Ct)).Error.Code.Value.ShouldBe("identity.token.revoked");
        (await _harness.TokenService.ValidateAccessTokenAsync(bobTokens.AccessToken)).Error.Code.Value.ShouldBe("identity.token.revoked");
        _harness.SessionRegistry.Get(bobTokens.Session).Value.RevokedReason.ShouldBe("Workstation stolen");
        var record = _harness.Audit.Of("identity.sessionRevoked").Single(r => r.Outcome == IdentityAuditOutcome.Success);
        record.Actor.ShouldBe(admin.Id);
        record.Subject.ShouldBe(bob.Id);
        record.Severity.ShouldBe(IdentityAuditSeverity.Sensitive);
        _harness.Audit.Of("identity.sessionRevoked").Count(r => r.Outcome == IdentityAuditOutcome.Denied).ShouldBe(1);

        // Alice revokes her own web session: allowed, the desktop one stays.
        (await management.RevokeAsync(alice.Id, aliceWeb.Session, "Shared computer", Ct)).IsSuccess.ShouldBeTrue();
        (await management.ListAsync(alice.Id, alice.Id, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe(aliceTokens.Session);

        // A disabled actor can do nothing.
        var version = _harness.UserDirectory.Get(alice.Id).Value.Version;
        await _harness.UserDirectory.UpdateAsync(alice.Id, new UserUpdate(Enabled: false), version, new ChangeContext(admin.Id, "api"), Ct);
        (await management.ListAsync(alice.Id, alice.Id, Ct)).Error.Code.Value.ShouldBe("identity.session.forbidden");
    }

    [Fact]
    public async Task IDN_U_070_Revoke_where_predicate_revokes_the_matching_sessions_only()
    {
        var alice = await UserAsync("where.alice");
        var bob = await UserAsync("where.bob");
        var auth = _harness.CreateAuthService();
        var aliceDesktop = (await auth.SignInLocalAsync(new SignInRequest("where.alice", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var aliceWeb = (await auth.SignInLocalAsync(new SignInRequest("where.alice", Password, _web), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var bobWeb = (await auth.SignInLocalAsync(new SignInRequest("where.bob", Password, _web with { ClientInstanceId = "web-2" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;

        // Maintenance (MNT) closes every web session that is not an administrator's.
        var count = await _harness.SessionRegistry.RevokeWhereAsync(s => s.ClientKind == ClientKind.Web, "Maintenance mode", Ct);
        count.ShouldBe(2);
        _harness.SessionRegistry.IsRevoked(aliceWeb.Session).ShouldBeTrue();
        _harness.SessionRegistry.IsRevoked(bobWeb.Session).ShouldBeTrue();
        _harness.SessionRegistry.IsRevoked(aliceDesktop.Session).ShouldBeFalse();
        _harness.SessionRegistry.Get(bobWeb.Session).Value.RevokedReason.ShouldBe("Maintenance mode");
        (await auth.RefreshAsync(aliceDesktop.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();

        // Already revoked sessions are not counted again.
        (await _harness.SessionRegistry.RevokeWhereAsync(s => s.ClientKind == ClientKind.Web, "Maintenance mode", Ct)).ShouldBe(0);
        _ = (alice, bob);
    }

    [Fact]
    public async Task IDN_U_071_User_disabled_revokes_the_sessions()
    {
        var admin = await UserAsync("dis.admin", administrator: true);
        var user = await UserAsync("dis.user");
        var auth = _harness.CreateAuthService();
        var tokens = (await auth.SignInLocalAsync(new SignInRequest("dis.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var web = (await auth.SignInLocalAsync(new SignInRequest("dis.user", Password, _web), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;

        (await _harness.UserDirectory.UpdateAsync(user.Id, new UserUpdate(Enabled: false), VersionOf(user.Id), new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();

        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldBeEmpty();
        (await _harness.TokenService.ValidateAccessTokenAsync(tokens.AccessToken)).Error.Code.Value.ShouldBe("identity.token.revoked");
        (await auth.RefreshAsync(web.RefreshToken, _web, Ct)).Error.Code.Value.ShouldBe("identity.token.revoked");
        (await auth.SignInLocalAsync(new SignInRequest("dis.user", Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.disabled");
    }

    [Fact]
    public async Task IDN_U_072_Administrator_flag_removed_revokes_the_sessions()
    {
        var admin = await UserAsync("flag.admin", administrator: true);
        var other = await UserAsync("flag.other", administrator: true);
        var auth = _harness.CreateAuthService();
        var tokens = (await auth.SignInLocalAsync(new SignInRequest("flag.other", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        (await _harness.TokenService.ValidateAccessTokenAsync(tokens.AccessToken)).Value.Administrator.ShouldBeTrue();

        (await _harness.UserDirectory.UpdateAsync(other.Id, new UserUpdate(Administrator: false), VersionOf(other.Id), new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();

        // The token with adm=true is dead at once; the next sign-in has no adm claim.
        (await _harness.TokenService.ValidateAccessTokenAsync(tokens.AccessToken)).Error.Code.Value.ShouldBe("identity.token.revoked");
        (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeFalse();
        var again = (await auth.SignInLocalAsync(new SignInRequest("flag.other", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        (await _harness.TokenService.ValidateAccessTokenAsync(again.Tokens.AccessToken)).Value.Administrator.ShouldBeFalse();

        // An unrelated change (display name) keeps the sessions.
        var version = _harness.UserDirectory.Get(other.Id).Value.Version;
        (await _harness.UserDirectory.UpdateAsync(other.Id, new UserUpdate(DisplayName: "Renamed"), version, new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.SessionRegistry.IsRevoked(again.Tokens.Session).ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_073_Account_locked_system_event_published()
    {
        var user = await UserAsync("locked.user");
        var auth = _harness.CreateAuthService();
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInLocalAsync(new SignInRequest("locked.user", "Wrong" + i + "Password123!", _desktop), Ct);
        }

        var until = (await _harness.LockoutService.CheckLockoutAsync(user.Id, Ct)).LockedUntil!.Value;
        var systemEvent = _harness.Events.Of("identity.accountLocked").ShouldHaveSingleItem();
        systemEvent.Severity.ShouldBe(IdentitySystemEventSeverity.Warning);
        systemEvent.Payload["userId"].ShouldBe(user.Id.ToString());
        systemEvent.Payload["until"].ShouldBe(until.ToString("o"));
        systemEvent.At.ShouldBe(_harness.Clock.GetUtcNow());

        var audit = _harness.Audit.Of("identity.accountLocked").ShouldHaveSingleItem();
        audit.Subject.ShouldBe(user.Id);
        audit.Severity.ShouldBe(IdentityAuditSeverity.Sensitive);
        audit.Details["lockCount24h"].ShouldBe("1");

        // Further wrong passwords while locked do not publish again.
        await auth.SignInLocalAsync(new SignInRequest("locked.user", "StillWrongPassword123!", _desktop), Ct);
        _harness.Events.Of("identity.accountLocked").Count().ShouldBe(1);
    }

    [Fact]
    public async Task IDN_U_074_Logins_are_hashed_in_warning_logs_and_secrets_never_logged()
    {
        using var logs = new LogCollector();
        using var loggers = new LoggerFactory([logs]);
        var leaks = new SecretLeakDetector();
        using var harness = new TestIdentityHarness(webAuthnOptions: new WebAuthnOptions("watchpoint.example.com", ["https://watchpoint.example.com"]));
        var auditSink = new Internal.Audit.LoggingIdentityAuditSink(loggers.CreateLogger<Internal.Audit.LoggingIdentityAuditSink>());
        var auditor = new Internal.Audit.IdentityAuditor(harness.Clock, auditSink, harness.Events);
        var lockout = new LockoutService(harness.DbFactory, harness.Clock, logger: loggers.CreateLogger<LockoutService>(), auditor: auditor);
        var users = new UserDirectory(harness.DbFactory, harness.SessionRegistry, harness.PasswordPolicy, harness.FastHasher, harness.Clock, lockout, null, auditor, loggers.CreateLogger<UserDirectory>());
        using var mfa = new MfaService(harness.DbFactory, users, harness.FastHasher, harness.Clock, harness.WebAuthn, auditor, new MfaOptions(MfaRequirement.Administrators), harness.Permissions, harness.Vault, loggers.CreateLogger<MfaService>());
        var auth = new AuthenticationService(harness.DbFactory, users, harness.FastHasher, harness.TokenService, harness.RefreshStore, harness.SessionRegistry, lockout,
            harness.RateLimiter, [], harness.Clock, mfa, auditor, logger: loggers.CreateLogger<AuthenticationService>());

        var password = "Secret-Login-Pass-9921!";
        var totpPassword = "Totp-User-Pass-8812!";
        leaks.Register(password);
        leaks.Register(totpPassword);
        var login = "Audrey.Logged";
        var uid = (await users.CreateAsync(new NewUser(login, "Audrey", Administrator: true), totpPassword, _systemCtx, Ct)).Value;
        var bgId = (await users.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass), password, _systemCtx, Ct)).Value;

        // Wrong password: warning 2711 carries a hash of the login, never the login itself nor the password.
        await auth.SignInLocalAsync(new SignInRequest(login, "WrongPassword123!", _desktop), Ct);
        await auth.SignInLocalAsync(new SignInRequest("nobody.here", "WrongPassword123!", _desktop), Ct);
        var warning = logs.WithEventId(2711).First();
        warning.RenderedMessage.ShouldNotContain(login, Case.Insensitive);
        warning.RenderedMessage.ShouldNotContain("nobody.here");
        warning.RenderedMessage.ShouldContain("identity.signin.invalid_credentials");
        leaks.Register("WrongPassword123!");

        // TOTP enrolment and sign-in with MFA, break-glass sign-in, step-up, refresh: tokens, secrets and codes never logged.
        var enrolment = (await mfa.EnrollTotpAsync(uid, Ct)).Value;
        leaks.Register(enrolment.SecretBase32);
        var secret = harness.Vault.All.Single(s => s.Owner.EntityId == uid.Value).Value;
        leaks.Register(Convert.ToBase64String(secret));
        var code = Totp.Compute(secret, Totp.StepOf(harness.Clock.GetUtcNow()));
        var recovery = (await mfa.ConfirmTotpAsync(uid, code, Ct)).Value.Codes;
        foreach (var r in recovery)
        {
            leaks.Register(r);
        }

        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var pending = (await auth.SignInLocalAsync(new SignInRequest(login, totpPassword, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        leaks.Register(pending.MfaToken);
        await auth.VerifyMfaAsync(new MfaVerification(pending.MfaToken, MfaMethod.Totp, _desktop, "000000"), Ct);
        var code2 = Totp.Compute(secret, Totp.StepOf(harness.Clock.GetUtcNow()));
        var tokens = (await auth.VerifyMfaAsync(new MfaVerification(pending.MfaToken, MfaMethod.Totp, _desktop, code2), Ct)).Value;
        leaks.Register(tokens.AccessToken);
        leaks.Register(tokens.RefreshToken);
        var refreshed = (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).Value;
        leaks.Register(refreshed.RefreshToken);
        var proof = (await auth.StepUpAsync(tokens.Session, new StepUpRequest(totpPassword), Ct)).Value;
        leaks.Register(proof.StepUpToken);
        var bg = (await auth.SignInLocalAsync(new SignInRequest("breakglass", password, _desktop, "Emergency: directory down"), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        leaks.Register(bg.Tokens.AccessToken);
        leaks.Register(bg.Tokens.RefreshToken);
        var adminCtx = new ChangeContext(uid, "api");
        await mfa.ResetAsync(uid, adminCtx, Ct);
        await lockout.UnlockAsync(uid, adminCtx, Ct);

        logs.Entries.Count.ShouldBeGreaterThan(10);
        logs.WithEventId(2790).ShouldNotBeEmpty();
        logs.WithEventId(2792).ShouldNotBeEmpty("break-glass audit is critical");
        leaks.AssertNoLeak(logs);
        _ = bgId;
    }

    [Fact]
    public async Task IDN_U_075_Password_longer_than_256_is_rejected_before_hashing()
    {
        var tooLong = new string('a', 250) + "Bb1!xyz";
        tooLong.Length.ShouldBe(257);

        // A hasher whose pool is held: anything that hashes answers rate_limited, anything refused before hashing does not.
        using var pool = new SemaphoreSlim(0, 1);
        var busy = new Argon2PasswordHasher(Argon2PasswordHasher.TestParameters, pool, TimeSpan.FromMilliseconds(20));
        var policy = new PasswordPolicy(new PasswordPolicyOptions(), _harness.DbFactory, busy);
        var users = new UserDirectory(_harness.DbFactory, _harness.SessionRegistry, policy, busy, _harness.Clock, _harness.LockoutService);

        var created = await users.CreateAsync(new NewUser("long.user", "Long"), tooLong, _systemCtx, Ct);
        created.Error.Code.Value.ShouldBe("common.validation.failed");
        created.Error.Kind.ShouldBe(ErrorKind.Validation);
        created.Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.too_long");

        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("long.user", "Long"), Password, _systemCtx, Ct)).Value;
        (await users.SetPasswordAsync(uid, tooLong, false, _systemCtx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.too_long");
        (await users.ChangeOwnPasswordAsync(uid, Password, tooLong, null, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.too_long");

        // The policy alone, with a 256-char password, is fine; 257 is refused; so is a lone surrogate.
        (await policy.ValidateAsync(new string('a', 240) + "Bb1!xyz-Qq9#long", "l", "L", null, Ct)).IsSuccess.ShouldBeTrue();
        (await policy.ValidateAsync("Broken\ud800Password-123", "l", "L", null, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.invalid_characters");

        // And a valid password with the held pool does reach the hasher (so the refusals above were before hashing).
        (await users.SetPasswordAsync(uid, "AnotherValid-Password-123!", false, _systemCtx, Ct)).Error.Code.Value.ShouldBe("common.rate_limited");
    }

    [Fact]
    public async Task IDN_U_076_Sign_in_admission_by_maintenance_returns_the_maintenance_error()
    {
        var user = await UserAsync("mnt.user");
        var tokens = (await _harness.CreateAuthService().SignInLocalAsync(new SignInRequest("mnt.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;

        var maintenance = Error.Create(ErrorKind.Unavailable, "maintenance.active", ("until", "2026-01-05T10:00:00Z"));
        var gate = new RefusingAdmission(maintenance);
        _harness.Admissions.Add(gate);
        var auth = _harness.CreateAuthService();

        var refused = await auth.SignInLocalAsync(new SignInRequest("mnt.user", Password, _desktop), Ct);
        refused.Error.Code.Value.ShouldBe("maintenance.active");
        refused.Error.Args["until"].ShouldBe("2026-01-05T10:00:00Z");
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldHaveSingleItem("no new session");

        // The refresh of the existing session is refused the same way, without burning the token.
        (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("maintenance.active");
        gate.Open = true;
        (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_077_Expired_user_mid_session_cannot_refresh()
    {
        var admin = await UserAsync("exp.admin", administrator: true);
        var user = await UserAsync("exp.user");
        var auth = _harness.CreateAuthService();
        var tokens = (await auth.SignInLocalAsync(new SignInRequest("exp.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;

        var until = _harness.Clock.GetUtcNow().AddHours(1);
        (await _harness.UserDirectory.UpdateAsync(user.Id, new UserUpdate(ValidUntil: until), VersionOf(user.Id), new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.Clock.Advance(TimeSpan.FromMinutes(30));
        var refreshed = (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).Value;

        _harness.Clock.Advance(TimeSpan.FromMinutes(31));
        (await auth.RefreshAsync(refreshed.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.signin.expired");
        _harness.SessionRegistry.IsRevoked(tokens.Session).ShouldBeTrue();
        (await _harness.TokenService.ValidateAccessTokenAsync(refreshed.AccessToken)).IsSuccess.ShouldBeFalse();
        (await auth.SignInLocalAsync(new SignInRequest("exp.user", Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.expired");
    }

    [Fact]
    public async Task IDN_U_078_Language_of_the_user_is_in_the_token_and_in_me()
    {
        var id = (await _harness.UserDirectory.CreateAsync(new NewUser("lang.user", "Langue", Language: "fr"), Password, _systemCtx, Ct)).Value;
        var auth = _harness.CreateAuthService();
        var tokens = (await auth.SignInLocalAsync(new SignInRequest("lang.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var claims = (await _harness.TokenService.ValidateAccessTokenAsync(tokens.AccessToken)).Value;
        claims.Language.ShouldBe("fr");

        var me = new CurrentUserService(_harness.UserDirectory, _harness.SessionRegistry, _harness.Mfa, _harness.Clock, _harness.Permissions);
        var current = (await me.GetAsync(claims, Ct)).Value;
        current.User.Language.ShouldBe("fr");
        current.Session.Id.ShouldBe(tokens.Session);

        // A changed language shows at the next token, not in the old one (claims are a snapshot).
        var version = _harness.UserDirectory.Get(id).Value.Version;
        await _harness.UserDirectory.UpdateAsync(id, new UserUpdate(Language: "ar"), version, _systemCtx, Ct);
        (await me.GetAsync(claims, Ct)).Value.User.Language.ShouldBe("ar");
        var refreshed = (await auth.RefreshAsync(tokens.RefreshToken, _desktop, Ct)).Value;
        (await _harness.TokenService.ValidateAccessTokenAsync(refreshed.AccessToken)).Value.Language.ShouldBe("ar");
    }

    [Fact]
    public async Task IDN_U_079_Me_returns_the_effective_permissions_summary()
    {
        var admin = await UserAsync("me.admin", administrator: true);
        var user = await UserAsync("me.user");
        _harness.Permissions.Granted[user.Id] = ["operate.alarms.view", "operate.commands.execute"];
        var auth = _harness.CreateAuthService();
        var me = new CurrentUserService(_harness.UserDirectory, _harness.SessionRegistry, _harness.Mfa, _harness.Clock, _harness.Permissions);

        var userTokens = (await auth.SignInLocalAsync(new SignInRequest("me.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var userClaims = (await _harness.TokenService.ValidateAccessTokenAsync(userTokens.AccessToken)).Value;
        var current = (await me.GetAsync(userClaims, Ct)).Value;
        current.Permissions.ShouldBe(["operate.alarms.view", "operate.commands.execute"]);
        current.Mfa.HasMethod.ShouldBeFalse();
        current.User.Id.ShouldBe(user.Id);

        var adminTokens = (await auth.SignInLocalAsync(new SignInRequest("me.admin", Password, _desktop with { ClientInstanceId = "a-1" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var adminClaims = (await _harness.TokenService.ValidateAccessTokenAsync(adminTokens.AccessToken)).Value;
        (await me.GetAsync(adminClaims, Ct)).Value.Permissions.ShouldBe(["*"]);

        // The rights hook is consulted each time: a change shows at once; a revoked session gives nothing.
        _harness.Permissions.Granted[user.Id] = ["operate.alarms.view"];
        (await me.GetAsync(userClaims, Ct)).Value.Permissions.ShouldBe(["operate.alarms.view"]);
        await auth.SignOutAsync(userTokens.Session, Ct);
        (await me.GetAsync(userClaims, Ct)).Error.Code.Value.ShouldBe("identity.token.revoked");
    }

    [Fact]
    public async Task IDN_U_080_Unlock_by_administrator_clears_the_lockout()
    {
        var admin = await UserAsync("unlock.admin", administrator: true);
        var other = await UserAsync("unlock.other");
        var user = await UserAsync("unlock.user");
        var auth = _harness.CreateAuthService(new SignInRateLimiter(_harness.Clock, new SignInRateLimiterOptions(1000, 1000)));
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInLocalAsync(new SignInRequest("unlock.user", "Wrong" + i + "Password123!", _desktop), Ct);
        }

        (await auth.SignInLocalAsync(new SignInRequest("unlock.user", Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.locked");
        var manager = _harness.LockoutService;
        (await manager.GetStatusAsync(user.Id, Ct))!.IsLockedAt(_harness.Clock.GetUtcNow()).ShouldBeTrue();

        // Only an administrator (or the system) unlocks; the refusal and the unlock are audited.
        (await manager.UnlockAsync(user.Id, new ChangeContext(other.Id, "api"), Ct)).Error.Code.Value.ShouldBe("identity.user.admin_required");
        (await manager.UnlockAsync(UserId.New(), new ChangeContext(admin.Id, "api"), Ct)).Error.Code.Value.ShouldBe("identity.user.not_found");
        (await manager.UnlockAsync(user.Id, new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();

        var status = await manager.GetStatusAsync(user.Id, Ct);
        status!.LockedUntil.ShouldBeNull();
        status.RecentFailures.ShouldBe(0);
        status.LockCount24h.ShouldBe(0);
        (await auth.SignInLocalAsync(new SignInRequest("unlock.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>();
        _harness.Audit.Of("identity.accountUnlocked").Select(r => r.Outcome).ShouldBe([IdentityAuditOutcome.Denied, IdentityAuditOutcome.Success]);
        _harness.Audit.Of("identity.accountUnlocked").Last().Actor.ShouldBe(admin.Id);

        // The progressive duration starts again at the base after an unlock (the lock count is cleared).
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInLocalAsync(new SignInRequest("unlock.user", "Wrong" + i + "Password123!", _desktop), Ct);
        }

        (await _harness.LockoutService.CheckLockoutAsync(user.Id, Ct)).LockedUntil.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(15));
    }

    [Fact]
    public async Task IDN_U_134_Break_glass_account_is_single_fixed_and_never_changed_silently()
    {
        var admin = await UserAsync("bg.admin", administrator: true);
        var nonAdmin = await UserAsync("bg.user");
        using var logs = new LogCollector();
        using var loggers = new LoggerFactory([logs]);
        var users = new UserDirectory(_harness.DbFactory, _harness.SessionRegistry, _harness.PasswordPolicy, _harness.FastHasher, _harness.Clock, _harness.LockoutService, null, _harness.Auditor, loggers.CreateLogger<UserDirectory>());

        // The login is reserved for the kind, and the kind needs the login.
        (await users.CreateAsync(new NewUser("breakglass", "Looks like one"), Password, _systemCtx, Ct)).Error.FieldErrors[0].Path.ShouldBe("/kind");
        (await users.CreateAsync(new NewUser("emergency", "BG", Kind: UserKind.BreakGlass), Password, _systemCtx, Ct)).Error.FieldErrors[0].Path.ShouldBe("/login");

        // A non-administrator cannot create it (it is an administrator account); the system (setup wizard) and administrators can.
        (await users.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass), BreakGlassPassword, new ChangeContext(nonAdmin.Id, "api"), Ct)).Error.Code.Value.ShouldBe("identity.user.admin_required");
        var bgId = (await users.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass, Administrator: false, MustChangePassword: true, Mfa: MfaPolicy.Required), BreakGlassPassword, new ChangeContext(admin.Id, "setup"), Ct)).Value;
        var bg = users.Get(bgId).Value;
        bg.Administrator.ShouldBeTrue();
        bg.MustChangePassword.ShouldBeFalse();
        bg.Mfa.ShouldBe(MfaPolicy.NotRequired);
        logs.WithEventId(2731).ShouldHaveSingleItem().RenderedMessage.ShouldContain("created");
        var created = _harness.Audit.Of("identity.breakGlassAccountChanged").ShouldHaveSingleItem();
        created.Severity.ShouldBe(IdentityAuditSeverity.Critical);
        created.Actor.ShouldBe(admin.Id);
        created.Details["action"].ShouldBe("created");

        // Only one.
        (await users.CreateAsync(new NewUser("BreakGlass", "Second", Kind: UserKind.BreakGlass), BreakGlassPassword, _systemCtx, Ct)).Error.Code.Value.ShouldBe("identity.user.login_taken");
        (await _harness.UserDirectory.DeleteAsync(bgId, new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.Clock.Advance(TimeSpan.FromDays(31));
        var bgId2 = (await users.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass), BreakGlassPassword, _systemCtx, Ct)).Value;
        (await users.CreateAsync(new NewUser("breakglass", "BG", Kind: UserKind.BreakGlass), BreakGlassPassword, _systemCtx, Ct)).Error.Code.Value.ShouldBe("identity.user.login_taken");

        // Its kind, administrator flag, authentication source and MFA exemption cannot change; nobody else becomes break-glass.
        var ctx = new ChangeContext(admin.Id, "api");
        var v = users.Get(bgId2).Value.Version;
        (await users.UpdateAsync(bgId2, new UserUpdate(Kind: UserKind.Person), v, ctx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");
        (await users.UpdateAsync(bgId2, new UserUpdate(Administrator: false), v, ctx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");
        (await users.UpdateAsync(bgId2, new UserUpdate(AuthSource: AuthSource.Oidc), v, ctx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");
        (await users.UpdateAsync(bgId2, new UserUpdate(Mfa: MfaPolicy.Required), v, ctx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");
        (await users.UpdateAsync(nonAdmin.Id, new UserUpdate(Kind: UserKind.BreakGlass), nonAdmin.Version, ctx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.breakglass.invalid");

        // Disabling is allowed (sealed away); enabling again and a password reset are critical, logged acts.
        (await users.UpdateAsync(bgId2, new UserUpdate(Enabled: false), v, ctx, Ct)).IsSuccess.ShouldBeTrue();
        v = users.Get(bgId2).Value.Version;
        (await users.UpdateAsync(bgId2, new UserUpdate(Enabled: true), v, ctx, Ct)).IsSuccess.ShouldBeTrue();
        (await users.SetPasswordAsync(bgId2, "New-Sealed-Password-4410!", false, ctx, Ct)).IsSuccess.ShouldBeTrue();
        _harness.Audit.Of("identity.breakGlassAccountChanged").Select(r => r.Details["action"]).ShouldBe(["created", "created", "enabled", "passwordReset"]);
        logs.WithEventId(2731).Count().ShouldBe(4);
    }

    // Administrators are created with MFA not required: the site default (administrators) is covered by the MFA tests.
    private async Task<User> UserAsync(string login, bool administrator = false)
    {
        var id = (await _harness.UserDirectory.CreateAsync(new NewUser(login, "User " + login, Administrator: administrator, Mfa: administrator ? MfaPolicy.NotRequired : MfaPolicy.Inherit), Password, _systemCtx, Ct)).Value;
        return _harness.UserDirectory.Get(id).Value;
    }

    private long VersionOf(UserId id) => _harness.UserDirectory.Get(id).Value.Version;

    private async Task<User> BreakGlassAsync()
    {
        var created = await _harness.UserDirectory.CreateAsync(new NewUser("breakglass", "Break Glass", Kind: UserKind.BreakGlass), BreakGlassPassword, _systemCtx, Ct);
        created.IsSuccess.ShouldBeTrue();
        return _harness.UserDirectory.Get(created.Value).Value;
    }

    private sealed class RefusingAdmission(Error error) : ISessionAdmission
    {
        public int Order => 10;

        public bool Open { get; set; }

        public ValueTask<Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase, CancellationToken ct) =>
            ValueTask.FromResult(Open ? Result.Success() : Result.Failure(error));
    }
}
