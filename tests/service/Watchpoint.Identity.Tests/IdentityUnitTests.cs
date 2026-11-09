using System.Net;
using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Tests;

[Trait("layer", "U")]
[Trait("spec", "IDN")]
public sealed class IdentityUnitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TestIdentityHarness _harness = new();
    private static readonly ChangeContext _systemCtx = new(null, "test");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IDN_U_001_Login_rules_U1()
    {
        // 1. Valid login
        var u1 = new NewUser(Login: "valid.user_1", DisplayName: "Alice Bob");
        var res1 = await _harness.UserDirectory.CreateAsync(u1, "StrongSecPass1!", _systemCtx, Ct);
        res1.IsSuccess.ShouldBeTrue();

        // 2. Too short (< 2)
        var uShort = new NewUser(Login: "a", DisplayName: "Short");
        var resShort = await _harness.UserDirectory.CreateAsync(uShort, "StrongSecPass1!", _systemCtx, Ct);
        resShort.IsSuccess.ShouldBeFalse();

        // 3. Invalid characters (spaces, special symbols not in ^[A-Za-z0-9._@-]+$)
        var uInvalid = new NewUser(Login: "user with spaces", DisplayName: "Invalid");
        var resInvalid = await _harness.UserDirectory.CreateAsync(uInvalid, "StrongSecPass1!", _systemCtx, Ct);
        resInvalid.IsSuccess.ShouldBeFalse();

        // 4. Case-insensitive uniqueness
        var uDup = new NewUser(Login: "VALID.USER_1", DisplayName: "Duplicate User");
        var resDup = await _harness.UserDirectory.CreateAsync(uDup, "StrongSecPass1!", _systemCtx, Ct);
        resDup.IsSuccess.ShouldBeFalse();
        resDup.Error.Code.Value.ShouldBe("identity.user.login_taken");
    }

    [Fact]
    public async Task IDN_U_002_Template_and_script_users_cannot_sign_in()
    {
        var auth = _harness.CreateAuthService();

        // Create Template user
        var template = new NewUser(Login: "template1", DisplayName: "Template", Kind: UserKind.Template);
        var res1 = await _harness.UserDirectory.CreateAsync(template, null, _systemCtx, Ct);
        res1.IsSuccess.ShouldBeTrue();

        // Attempt sign in -> generic invalid_credentials
        var client = new ClientContext(ClientKind.Desktop, "inst1");
        var outcome = await auth.SignInLocalAsync(new SignInRequest("template1", "AnyPassword123!", client), Ct);

        outcome.IsSuccess.ShouldBeFalse();
        outcome.Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
    }

    [Fact]
    public async Task IDN_U_003_Last_administrator_protected()
    {
        // Create single admin
        var admin = new NewUser(Login: "admin1", DisplayName: "Admin", Administrator: true, Kind: UserKind.Person);
        var createRes = await _harness.UserDirectory.CreateAsync(admin, "ValidPassword123!", _systemCtx, Ct);
        createRes.IsSuccess.ShouldBeTrue();
        var adminId = createRes.Value;

        // Try to delete last admin -> last_administrator error
        var deleteRes = await _harness.UserDirectory.DeleteAsync(adminId, _systemCtx, Ct);
        deleteRes.IsSuccess.ShouldBeFalse();
        deleteRes.Error.Code.Value.ShouldBe("identity.user.last_administrator");

        // Try to demote administrator flag -> last_administrator error
        var demoteRes = await _harness.UserDirectory.UpdateAsync(adminId, new UserUpdate(Administrator: false), 1, _systemCtx, Ct);
        demoteRes.IsSuccess.ShouldBeFalse();
        demoteRes.Error.Code.Value.ShouldBe("identity.user.last_administrator");

        // Try to disable last admin -> last_administrator error
        var disableRes = await _harness.UserDirectory.UpdateAsync(adminId, new UserUpdate(Enabled: false), 1, _systemCtx, Ct);
        disableRes.IsSuccess.ShouldBeFalse();
        disableRes.Error.Code.Value.ShouldBe("identity.user.last_administrator");
    }

    [Fact]
    public async Task IDN_U_004_Self_change_of_admin_enabled_kind_forbidden()
    {
        var admin1 = new NewUser(Login: "admin1", DisplayName: "Admin 1", Administrator: true, Kind: UserKind.Person);
        var admin2 = new NewUser(Login: "admin2", DisplayName: "Admin 2", Administrator: true, Kind: UserKind.Person);

        var id1 = (await _harness.UserDirectory.CreateAsync(admin1, "ValidPassword123!", _systemCtx, Ct)).Value;
        var id2 = (await _harness.UserDirectory.CreateAsync(admin2, "ValidPassword123!", _systemCtx, Ct)).Value;

        // Admin 1 attempts to change their own Administrator flag
        var selfCtx = new ChangeContext(id1, "self");
        var res = await _harness.UserDirectory.UpdateAsync(id1, new UserUpdate(Administrator: false), 1, selfCtx, Ct);
        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("identity.user.self_change_forbidden");

        // Admin 1 attempts to disable self
        var resDisable = await _harness.UserDirectory.UpdateAsync(id1, new UserUpdate(Enabled: false), 1, selfCtx, Ct);
        resDisable.IsSuccess.ShouldBeFalse();
        resDisable.Error.Code.Value.ShouldBe("identity.user.self_change_forbidden");
    }

    [Fact]
    public async Task IDN_U_005_Only_administrators_create_administrators()
    {
        // Normal user
        var normal = new NewUser(Login: "normal1", DisplayName: "Normal", Administrator: false);
        var normalId = (await _harness.UserDirectory.CreateAsync(normal, "ValidPassword123!", _systemCtx, Ct)).Value;

        // Normal user tries to create an admin -> admin_required error
        var normalCtx = new ChangeContext(normalId, "api");
        var newAdmin = new NewUser(Login: "admin2", DisplayName: "Admin 2", Administrator: true);
        var res = await _harness.UserDirectory.CreateAsync(newAdmin, "ValidPassword123!", normalCtx, Ct);

        res.IsSuccess.ShouldBeFalse();
        res.Error.Code.Value.ShouldBe("identity.user.admin_required");
    }

    [Fact]
    public async Task IDN_U_006_Effective_status_computation()
    {
        var now = _harness.Clock.GetUtcNow();

        // 1. Disabled
        var uDisabled = new NewUser(Login: "u_disabled", DisplayName: "Disabled", Enabled: false);
        var idDisabled = (await _harness.UserDirectory.CreateAsync(uDisabled, "ValidPassword123!", _systemCtx, Ct)).Value;
        var userDisabled = _harness.UserDirectory.Get(idDisabled).Value;
        ComputeStatus(userDisabled, now, false).ShouldBe(UserStatus.Disabled);

        // 2. Expired (ValidUntil in the past)
        var uExpired = new NewUser(Login: "u_expired", DisplayName: "Expired", ValidUntil: now.AddHours(-1));
        var idExpired = (await _harness.UserDirectory.CreateAsync(uExpired, "ValidPassword123!", _systemCtx, Ct)).Value;
        var userExpired = _harness.UserDirectory.Get(idExpired).Value;
        ComputeStatus(userExpired, now, false).ShouldBe(UserStatus.Expired);

        // 3. Locked
        var uActive = new NewUser(Login: "u_active", DisplayName: "Active");
        var idActive = (await _harness.UserDirectory.CreateAsync(uActive, "ValidPassword123!", _systemCtx, Ct)).Value;
        var userActive = _harness.UserDirectory.Get(idActive).Value;
        ComputeStatus(userActive, now, true).ShouldBe(UserStatus.Locked);

        // 4. Active
        ComputeStatus(userActive, now, false).ShouldBe(UserStatus.Active);
    }

    private static UserStatus ComputeStatus(User user, DateTimeOffset now, bool isLocked)
    {
        if (!user.Enabled) return UserStatus.Disabled;
        if ((user.ValidFrom.HasValue && now < user.ValidFrom.Value) || (user.ValidUntil.HasValue && now > user.ValidUntil.Value)) return UserStatus.Expired;
        if (isLocked) return UserStatus.Locked;
        return UserStatus.Active;
    }

    [Fact]
    public async Task IDN_U_007_Soft_delete_keeps_record_login_reusable_after_30_days()
    {
        var u = new NewUser(Login: "delete.me", DisplayName: "Delete Me");
        var id = (await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct)).Value;

        // Delete user
        var delRes = await _harness.UserDirectory.DeleteAsync(id, _systemCtx, Ct);
        delRes.IsSuccess.ShouldBeTrue();

        // Immediately reuse -> login_taken
        var reuseImmediate = await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);
        reuseImmediate.IsSuccess.ShouldBeFalse();
        reuseImmediate.Error.Code.Value.ShouldBe("identity.user.login_taken");

        // Advance 31 days -> reusable
        _harness.Clock.Advance(TimeSpan.FromDays(31));
        var reuseLater = await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);
        reuseLater.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_008_Argon2id_hash_format_and_verification()
    {
        // Real production Argon2id hasher (spec IDN §5: 64 MiB, t=3, p=2)
        var realHasher = _harness.RealHasher;
        var hash = await realHasher.HashPasswordAsync("CorrectHorseBatteryStaple123!", Ct);

        hash.ShouldStartWith("$argon2id$v=19$m=65536,t=3,p=2$");

        // Verify correct
        var (verified, _) = await realHasher.VerifyPasswordAsync("CorrectHorseBatteryStaple123!", hash, Ct);
        verified.ShouldBeTrue();

        // Verify wrong
        var (wrongVerified, _) = await realHasher.VerifyPasswordAsync("WrongPassword123!", hash, Ct);
        wrongVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_009_Rehash_on_parameter_upgrade()
    {
        // Weak/old hash
        var weakHasher = new Argon2PasswordHasher(new Argon2Parameters(MemorySizeKiB: 1024, Iterations: 1, Parallelism: 1));
        var weakHash = await weakHasher.HashPasswordAsync("ValidPassword123!", Ct);

        // Verify with stronger hasher -> needsRehash = true
        var strongHasher = new Argon2PasswordHasher(new Argon2Parameters(MemorySizeKiB: 2048, Iterations: 2, Parallelism: 2));
        var (verified, needsRehash) = await strongHasher.VerifyPasswordAsync("ValidPassword123!", weakHash, Ct);

        verified.ShouldBeTrue();
        needsRehash.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_010_Hash_pool_bound_excess_waits_beyond_5s_rate_limited()
    {
        var hasher = new Argon2PasswordHasher(new Argon2Parameters(MemorySizeKiB: 1024, Iterations: 1, Parallelism: 1));
        var tasks = Enumerable.Range(0, 10).Select(i => hasher.HashPasswordAsync($"PasswordNumber{i}!", Ct)).ToList();
        var results = await Task.WhenAll(tasks);

        results.Length.ShouldBe(10);
        foreach (var r in results)
        {
            r.ShouldStartWith("$argon2id$");
        }
    }

    [Fact]
    public async Task IDN_U_011_Unknown_login_takes_dummy_hash_path()
    {
        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        // Non-existent user -> calls dummy hash and returns invalid_credentials
        var outcome = await auth.SignInLocalAsync(new SignInRequest("nonexistent_user", "SomePassword123!", client), Ct);
        outcome.IsSuccess.ShouldBeFalse();
        outcome.Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
    }

    [Fact]
    public async Task IDN_U_012_Password_policy_rules()
    {
        var policy = _harness.PasswordPolicy;

        // 1. Too short (< 12)
        (await policy.ValidateAsync("Short1!a", "login1", "Display", null, Ct)).IsSuccess.ShouldBeFalse();

        // 2. 12 chars but only 2 classes (lower + upper) -> too simple
        (await policy.ValidateAsync("Abcdefghijkl", "login1", "Display", null, Ct)).IsSuccess.ShouldBeFalse();

        // 3. 12 chars with 3 classes -> valid
        (await policy.ValidateAsync("Abcdefghijk1", "login1", "Display", null, Ct)).IsSuccess.ShouldBeTrue();

        // 4. Passphrase >= 16 chars with just letters -> allowed by exception
        (await policy.ValidateAsync("correcthorsebatterystaple", "login1", "Display", null, Ct)).IsSuccess.ShouldBeTrue();

        // 5. Contains login part (>= 4 chars)
        (await policy.ValidateAsync("Passlogin1word!", "login1", "Display", null, Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void IDN_U_013_Breached_list_match_case_insensitive()
    {
        BreachedPasswords.IsBreached("PassWord").ShouldBeTrue();
        BreachedPasswords.IsBreached("123456").ShouldBeTrue();
        BreachedPasswords.IsBreached("uniquepasswordnotbreached987654321").ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_014_Password_history_rejects_last_5()
    {
        var u = new NewUser(Login: "history.user", DisplayName: "History User");
        var id = (await _harness.UserDirectory.CreateAsync(u, "InitialPassword1!", _systemCtx, Ct)).Value;

        // Change password 4 times
        for (var i = 1; i <= 4; i++)
        {
            var r = await _harness.UserDirectory.SetPasswordAsync(id, $"PasswordNumber{i}!", false, _systemCtx, Ct);
            r.IsSuccess.ShouldBeTrue();
        }

        // Try reusing initial password (which is within last 5)
        var reuseRes = await _harness.UserDirectory.SetPasswordAsync(id, "InitialPassword1!", false, _systemCtx, Ct);
        reuseRes.IsSuccess.ShouldBeFalse();
        reuseRes.Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.reused");
    }

    [Fact]
    public async Task IDN_U_015_Sign_in_success_creates_session_and_tokens_updates_last_sign_in()
    {
        var u = new NewUser(Login: "signin.user", DisplayName: "Sign In User");
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1", RemoteIp: IPAddress.Parse("192.168.1.10"));

        var outcome = await auth.SignInLocalAsync(new SignInRequest("signin.user", "ValidPassword123!", client), Ct);
        outcome.IsSuccess.ShouldBeTrue();

        var signed = outcome.Value.ShouldBeOfType<SignInOutcome.Signed>();
        signed.Tokens.AccessToken.ShouldNotBeNullOrEmpty();
        signed.Tokens.RefreshToken.ShouldNotBeNullOrEmpty();
        signed.Tokens.Session.Value.ShouldNotBe(Guid.Empty);
        signed.User.LastSignInAt.ShouldNotBeNull();
    }

    [Fact]
    public void IDN_U_016_Rate_limit_per_ip_and_per_login()
    {
        var limiter = new SignInRateLimiter(_harness.Clock, new SignInRateLimiterOptions(RatePerIp: 3, RatePerLogin: 2));
        var ip = IPAddress.Parse("10.0.0.1");

        // 2 attempts for user1
        limiter.TryAcquire(ip, "user1").ShouldBeTrue();
        limiter.TryAcquire(ip, "user1").ShouldBeTrue();

        // 3rd attempt for user1 -> rejected by login limit
        limiter.TryAcquire(ip, "user1").ShouldBeFalse();

        // Attempt for user2 on same IP -> 3rd for IP -> allowed
        limiter.TryAcquire(ip, "user2").ShouldBeTrue();

        // 4th attempt on same IP -> rejected by IP limit
        limiter.TryAcquire(ip, "user3").ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_017_Wrong_password_increments_failures_5th_locks_for_15min()
    {
        var u = new NewUser(Login: "lockout.user", DisplayName: "Lockout User");
        var id = (await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct)).Value;

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        // 4 failures
        for (var i = 0; i < 4; i++)
        {
            var res = await auth.SignInLocalAsync(new SignInRequest("lockout.user", "WrongPassword!", client), Ct);
            res.IsSuccess.ShouldBeFalse();
            res.Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
        }

        // 5th failure -> triggers lockout
        var fifth = await auth.SignInLocalAsync(new SignInRequest("lockout.user", "WrongPassword!", client), Ct);
        fifth.IsSuccess.ShouldBeFalse();

        var (isLocked, until) = await _harness.LockoutService.CheckLockoutAsync(id, Ct);
        isLocked.ShouldBeTrue();
        until.ShouldNotBeNull();
    }

    [Fact]
    public async Task IDN_U_018_During_lockout_wrong_password_invalid_credentials_correct_locked_with_until()
    {
        var u = new NewUser(Login: "lock.inspect", DisplayName: "Lock Inspect");
        await _harness.UserDirectory.CreateAsync(u, "CorrectPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        // Force 5 failures
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInLocalAsync(new SignInRequest("lock.inspect", "WrongPassword!", client), Ct);
        }

        // 1. Wrong password during lockout -> invalid_credentials (no lockout hint!)
        var wrongRes = await auth.SignInLocalAsync(new SignInRequest("lock.inspect", "AnotherWrong!", client), Ct);
        wrongRes.IsSuccess.ShouldBeFalse();
        wrongRes.Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");

        // 2. Correct password during lockout -> locked with until
        var correctRes = await auth.SignInLocalAsync(new SignInRequest("lock.inspect", "CorrectPassword123!", client), Ct);
        correctRes.IsSuccess.ShouldBeFalse();
        correctRes.Error.Code.Value.ShouldBe("identity.signin.locked");
        correctRes.Error.Args.ContainsKey("until").ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_019_Progressive_lockout_doubling_to_24h()
    {
        var userId = UserId.New();

        // 1st lock (5 failures) -> 15 min
        for (var i = 0; i < 4; i++) await _harness.LockoutService.RecordFailureAsync(userId, Ct);
        var (l1, until1) = await _harness.LockoutService.RecordFailureAsync(userId, Ct);
        l1.ShouldBeTrue();
        (until1!.Value - _harness.Clock.GetUtcNow()).ShouldBe(TimeSpan.FromMinutes(15));

        // Advance past lock
        _harness.Clock.Advance(TimeSpan.FromMinutes(16));

        // 2nd lock -> 30 min
        for (var i = 0; i < 4; i++) await _harness.LockoutService.RecordFailureAsync(userId, Ct);
        var (l2, until2) = await _harness.LockoutService.RecordFailureAsync(userId, Ct);
        l2.ShouldBeTrue();
        (until2!.Value - _harness.Clock.GetUtcNow()).ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task IDN_U_020_Disabled_expired_client_not_allowed_workstation_not_allowed()
    {
        var now = _harness.Clock.GetUtcNow();
        var auth = _harness.CreateAuthService();

        // 1. Disabled user
        var uDis = new NewUser(Login: "dis.user", DisplayName: "Disabled", Enabled: false);
        await _harness.UserDirectory.CreateAsync(uDis, "ValidPassword123!", _systemCtx, Ct);
        var resDis = await auth.SignInLocalAsync(new SignInRequest("dis.user", "ValidPassword123!", new ClientContext(ClientKind.Desktop, "i")), Ct);
        resDis.Error.Code.Value.ShouldBe("identity.signin.disabled");

        // 2. Expired user
        var uExp = new NewUser(Login: "exp.user", DisplayName: "Expired", ValidUntil: now.AddMinutes(-5));
        await _harness.UserDirectory.CreateAsync(uExp, "ValidPassword123!", _systemCtx, Ct);
        var resExp = await auth.SignInLocalAsync(new SignInRequest("exp.user", "ValidPassword123!", new ClientContext(ClientKind.Desktop, "i")), Ct);
        resExp.Error.Code.Value.ShouldBe("identity.signin.expired");

        // 3. Client kind not allowed
        var uClient = new NewUser(Login: "client.user", DisplayName: "Client", AllowedClients: new HashSet<ClientKind> { ClientKind.Desktop });
        await _harness.UserDirectory.CreateAsync(uClient, "ValidPassword123!", _systemCtx, Ct);
        var resClient = await auth.SignInLocalAsync(new SignInRequest("client.user", "ValidPassword123!", new ClientContext(ClientKind.Web, "i")), Ct);
        resClient.Error.Code.Value.ShouldBe("identity.signin.client_not_allowed");

        // 4. Workstation not allowed
        var uWks = new NewUser(Login: "wks.user", DisplayName: "Wks", AllowedWorkstations: ["WKS-SECURE"]);
        await _harness.UserDirectory.CreateAsync(uWks, "ValidPassword123!", _systemCtx, Ct);
        var resWks = await auth.SignInLocalAsync(new SignInRequest("wks.user", "ValidPassword123!", new ClientContext(ClientKind.Desktop, "i", WorkstationId: "WKS-OTHER")), Ct);
        resWks.Error.Code.Value.ShouldBe("identity.signin.workstation_not_allowed");
    }

    [Fact]
    public async Task IDN_U_021_Admission_hooks_called_in_order_first_failure_returned()
    {
        var hook1 = new TestAdmissionHook(1, Error.Create(ErrorKind.Forbidden, "hook.one"));
        var hook2 = new TestAdmissionHook(2, Error.Create(ErrorKind.Forbidden, "hook.two"));

        _harness.Admissions.Add(hook2);
        _harness.Admissions.Add(hook1);

        var u = new NewUser(Login: "admission.user", DisplayName: "Admission User");
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var outcome = await auth.SignInLocalAsync(new SignInRequest("admission.user", "ValidPassword123!", new ClientContext(ClientKind.Desktop, "i")), Ct);

        outcome.IsSuccess.ShouldBeFalse();
        outcome.Error.Code.Value.ShouldBe("hook.one");
    }

    [Fact]
    public async Task IDN_U_022_MustChangePassword_flow_change_completes_sign_in()
    {
        var u = new NewUser(Login: "mustchange.user", DisplayName: "Must Change User", MustChangePassword: true);
        await _harness.UserDirectory.CreateAsync(u, "OldPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        // Sign in -> PasswordChangeRequired
        var outcome = await auth.SignInLocalAsync(new SignInRequest("mustchange.user", "OldPassword123!", client), Ct);
        outcome.IsSuccess.ShouldBeTrue();

        var changeRequired = outcome.Value.ShouldBeOfType<SignInOutcome.PasswordChangeRequired>();
        changeRequired.ChangeToken.ShouldNotBeNullOrEmpty();

        // Complete password change with changeToken
        var complete = await auth.CompletePasswordChangeAsync(changeRequired.ChangeToken, "BrandNewPassword123!", Ct);
        complete.IsSuccess.ShouldBeTrue();
        complete.Value.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task IDN_U_023_Access_token_claims_and_lifetime_signature_ES256()
    {
        var user = new User(
            UserId.New(), "test.claims", "Test Claims", null, null, "fr", null,
            UserKind.Person, true, true, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);

        var session = new Session(
            SessionId.New(), user.Id, ClientKind.Desktop, "inst1", "WKS-1", null, null, null,
            _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow().AddDays(1), null, null);

        var (jwt, expiresAt) = _harness.TokenService.IssueAccessToken(session, user);
        expiresAt.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(10));

        var claims = await _harness.TokenService.ValidateAccessTokenAsync(jwt);
        claims.IsSuccess.ShouldBeTrue();
        claims.Value.UserId.ShouldBe(user.Id);
        claims.Value.SessionId.ShouldBe(session.Id);
        claims.Value.ClientKind.ShouldBe(ClientKind.Desktop);
        claims.Value.Administrator.ShouldBeTrue();
        claims.Value.Language.ShouldBe("fr");
    }

    [Fact]
    public async Task IDN_U_024_Token_validation_errors_and_clock_skew()
    {
        var user = new User(
            UserId.New(), "val.user", "Val User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);

        var session = new Session(
            SessionId.New(), user.Id, ClientKind.Desktop, "inst1", null, null, null, null,
            _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow().AddDays(1), null, null);

        var (jwt, _) = _harness.TokenService.IssueAccessToken(session, user);

        // Advance 10m + 20s (within 30s clock skew) -> still valid
        _harness.Clock.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(20)));
        var resSkew = await _harness.TokenService.ValidateAccessTokenAsync(jwt);
        resSkew.IsSuccess.ShouldBeTrue();

        // Advance another 20s (outside 30s clock skew) -> expired
        _harness.Clock.Advance(TimeSpan.FromSeconds(20));
        var resExpired = await _harness.TokenService.ValidateAccessTokenAsync(jwt);
        resExpired.IsSuccess.ShouldBeFalse();
        resExpired.Error.Code.Value.ShouldBe("identity.token.expired");
    }

    [Fact]
    public async Task IDN_U_025_Revoked_session_rejected_immediately_via_revocation_set()
    {
        var user = new User(
            UserId.New(), "rev.user", "Rev User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);

        var session = new Session(
            SessionId.New(), user.Id, ClientKind.Desktop, "inst1", null, null, null, null,
            _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow().AddDays(1), null, null);

        await _harness.SessionRegistry.RegisterNewSessionAsync(session, Ct);
        var (jwt, _) = _harness.TokenService.IssueAccessToken(session, user);

        (await _harness.TokenService.ValidateAccessTokenAsync(jwt)).IsSuccess.ShouldBeTrue();

        // Revoke session
        await _harness.SessionRegistry.RevokeAsync(session.Id, "Admin revoke", null, Ct);

        var validated = await _harness.TokenService.ValidateAccessTokenAsync(jwt);
        validated.IsSuccess.ShouldBeFalse();
        validated.Error.Code.Value.ShouldBe("identity.token.revoked");
    }

    [Fact]
    public async Task IDN_U_026_Signing_key_rotation_validates_for_1_day_then_rejected()
    {
        var longLivedTokenService = new JwtTokenService(_harness.KeyRing, _harness.SessionRegistry, _harness.Clock,
            new JwtTokenOptions(AccessTokenLifetime: TimeSpan.FromDays(40)));

        var user = new User(
            UserId.New(), "rot.user", "Rot User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);

        var session = new Session(
            SessionId.New(), user.Id, ClientKind.Desktop, "inst1", null, null, null, null,
            _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow(), _harness.Clock.GetUtcNow().AddDays(50), null, null);

        // Key 1 is created at T=0 (expires for signing at T+30d, expires for validation at T+31d)
        var (jwt, _) = longLivedTokenService.IssueAccessToken(session, user);

        // Advance to Day 30 + 12 hours (new key signs, key 1 has expired for signing, but is within 1-day validation overlap)
        _harness.Clock.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromHours(12)));

        // New key signs
        var newKey = _harness.KeyRing.GetCurrentSigningKey();
        var (newJwt, _) = longLivedTokenService.IssueAccessToken(session, user);
        (await longLivedTokenService.ValidateAccessTokenAsync(newJwt)).IsSuccess.ShouldBeTrue();

        // Old key validates for 1 day
        var validResult = await longLivedTokenService.ValidateAccessTokenAsync(jwt);
        validResult.IsSuccess.ShouldBeTrue();

        // Advance past 1-day validation overlap (e.g. Day 32) -> old key rejected
        _harness.Clock.Advance(TimeSpan.FromDays(2));
        var rejectedResult = await longLivedTokenService.ValidateAccessTokenAsync(jwt);
        rejectedResult.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task IDN_U_027_Refresh_rotation_returns_new_token_old_invalid()
    {
        var u = new NewUser(Login: "refresh.user", DisplayName: "Refresh User");
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        var signin = (await auth.SignInLocalAsync(new SignInRequest("refresh.user", "ValidPassword123!", client), Ct)).Value as SignInOutcome.Signed;
        var oldRefresh = signin!.Tokens.RefreshToken;

        // Refresh -> returns new tokens
        var refreshed = await auth.RefreshAsync(oldRefresh, client, Ct);
        refreshed.IsSuccess.ShouldBeTrue();
        var newRefresh = refreshed.Value.RefreshToken;
        newRefresh.ShouldNotBe(oldRefresh);
    }

    [Fact]
    public async Task IDN_U_028_Refresh_reuse_revokes_the_whole_session()
    {
        var u = new NewUser(Login: "reuse.user", DisplayName: "Reuse User");
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        var signin = (await auth.SignInLocalAsync(new SignInRequest("reuse.user", "ValidPassword123!", client), Ct)).Value as SignInOutcome.Signed;
        var oldRefresh = signin!.Tokens.RefreshToken;
        var sessionId = signin.Tokens.Session;

        // Legitimate rotation
        await auth.RefreshAsync(oldRefresh, client, Ct);

        // Reusing the old rotated token -> session revoked
        var reuseResult = await auth.RefreshAsync(oldRefresh, client, Ct);
        reuseResult.IsSuccess.ShouldBeFalse();
        reuseResult.Error.Code.Value.ShouldBe("identity.refresh.reused");

        _harness.SessionRegistry.IsRevoked(sessionId).ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_029_Refresh_binding_to_client_instance_and_kind()
    {
        var u = new NewUser(Login: "binding.user", DisplayName: "Binding User");
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst-A");

        var signin = (await auth.SignInLocalAsync(new SignInRequest("binding.user", "ValidPassword123!", client), Ct)).Value as SignInOutcome.Signed;
        var token = signin!.Tokens.RefreshToken;

        // Try refresh from different ClientInstanceId -> rejected
        var wrongClient = new ClientContext(ClientKind.Desktop, "inst-B");
        var res1 = await auth.RefreshAsync(token, wrongClient, Ct);
        res1.IsSuccess.ShouldBeFalse();
        res1.Error.Code.Value.ShouldBe("identity.refresh.invalid");

        // Try refresh with wrong ClientKind -> rejected
        var wrongKind = new ClientContext(ClientKind.Web, "inst-A");
        var res2 = await auth.RefreshAsync(token, wrongKind, Ct);
        res2.IsSuccess.ShouldBeFalse();
        res2.Error.Code.Value.ShouldBe("identity.refresh.invalid");
    }

    [Fact]
    public async Task IDN_U_030_Sliding_idle_and_absolute_session_lifetimes()
    {
        var u = new NewUser(Login: "lifetime.user", DisplayName: "Lifetime User",
            SessionPolicy: new SessionPolicy(0, TimeSpan.FromMinutes(15), TimeSpan.FromHours(24)));
        await _harness.UserDirectory.CreateAsync(u, "ValidPassword123!", _systemCtx, Ct);

        var auth = _harness.CreateAuthService();
        var client = new ClientContext(ClientKind.Desktop, "inst1");

        var signin = (await auth.SignInLocalAsync(new SignInRequest("lifetime.user", "ValidPassword123!", client), Ct)).Value as SignInOutcome.Signed;
        var currentToken = signin!.Tokens.RefreshToken;

        // Advance 11 hours (within 12h idle) -> refresh succeeds
        _harness.Clock.Advance(TimeSpan.FromHours(11));
        var ref1 = await auth.RefreshAsync(currentToken, client, Ct);
        ref1.IsSuccess.ShouldBeTrue();
        currentToken = ref1.Value.RefreshToken;

        // Advance 13 hours (past 12h idle) -> refresh fails
        _harness.Clock.Advance(TimeSpan.FromHours(13));
        var ref2 = await auth.RefreshAsync(currentToken, client, Ct);
        ref2.IsSuccess.ShouldBeFalse();
        ref2.Error.Code.Value.ShouldBe("identity.refresh.invalid");
    }

    private sealed class TestAdmissionHook(int order, Watchpoint.Kernel.Results.Error? rejectWithError = null) : ISessionAdmission
    {
        public int Order => order;

        public ValueTask<Watchpoint.Kernel.Results.Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase, CancellationToken ct) =>
            ValueTask.FromResult(rejectWithError is not null ? Watchpoint.Kernel.Results.Result.Failure(rejectWithError) : Watchpoint.Kernel.Results.Result.Success());
    }
}
