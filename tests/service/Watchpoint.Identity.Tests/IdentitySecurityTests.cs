using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Identity.Tests;

/// <summary>Regression tests of the T1.18r security review (spec IDN §15, IDN-U-101…IDN-U-118).</summary>
[Trait("layer", "U")]
[Trait("spec", "IDN")]
public sealed class IdentitySecurityTests : IDisposable
{
    private const string Password = "ValidPassword123!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TestIdentityHarness _harness = new();
    private static readonly ChangeContext _systemCtx = new(null, "test");
    private static readonly ClientContext _desktop = new(ClientKind.Desktop, "inst-1");

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IDN_U_101_Password_is_NFKC_normalised_and_invalid_unicode_is_refused()
    {
        var hasher = _harness.FastHasher;

        // "é" precomposed vs "e" + combining acute; "ﬁ" ligature vs "fi": the same password for a person.
        var hash = await hasher.HashPasswordAsync("Café-ﬁnal-Secret-1", Ct);
        (await hasher.VerifyPasswordAsync("Café-final-Secret-1", hash, Ct)).Verified.ShouldBeTrue();
        (await hasher.VerifyPasswordAsync("Cafe-final-Secret-1", hash, Ct)).Verified.ShouldBeFalse();

        // A lone surrogate is not Unicode: never verified, refused by the policy, never an exception.
        const string broken = "Broken\ud800Password-123";
        (await hasher.VerifyPasswordAsync(broken, hash, Ct)).Verified.ShouldBeFalse();
        var policy = await _harness.PasswordPolicy.ValidateAsync(broken, "login1", "Display", null, Ct);
        policy.IsSuccess.ShouldBeFalse();
        policy.Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.invalid_characters");
    }

    [Fact]
    public async Task IDN_U_102_Overlong_password_is_a_validation_error_and_sign_in_treats_known_and_unknown_logins_alike()
    {
        var tooLong = new string('a', 250) + "Bb1!xyz";
        tooLong.Length.ShouldBe(257);

        var policy = await _harness.PasswordPolicy.ValidateAsync(tooLong, "login1", "Display", null, Ct);
        policy.IsSuccess.ShouldBeFalse();
        policy.Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.too_long");

        var created = await _harness.UserDirectory.CreateAsync(new NewUser("long.user", "Long User"), tooLong, _systemCtx, Ct);
        created.IsSuccess.ShouldBeFalse();
        created.Error.Kind.ShouldBe(ErrorKind.Validation);

        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("known.user", "Known User"), Password, _systemCtx, Ct)).Value;

        // A hasher whose pool is held: a sign-in that reaches the hasher answers rate_limited, one that returns
        // early without hashing answers invalid_credentials. Known and unknown logins must both hash (the dummy
        // hash), otherwise the response time tells whether the login exists.
        using var pool = new SemaphoreSlim(0, 1);
        var busy = new Argon2PasswordHasher(Argon2PasswordHasher.TestParameters, pool, TimeSpan.FromMilliseconds(20));
        var auth = AuthWith(busy);

        foreach (var badPassword in new[] { tooLong, string.Empty, "Broken\ud800Password-123" })
        {
            var known = await auth.SignInLocalAsync(new SignInRequest("known.user", badPassword, _desktop), Ct);
            var unknown = await auth.SignInLocalAsync(new SignInRequest("nobody.here", badPassword, _desktop), Ct);
            known.Error.Code.Value.ShouldBe("common.rate_limited");
            unknown.Error.Code.Value.ShouldBe(known.Error.Code.Value);
        }

        // With a free pool both get the generic error, and the impossible password is not a lockout failure.
        var free = _harness.CreateAuthService();
        (await free.SignInLocalAsync(new SignInRequest("known.user", tooLong, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
        (await free.SignInLocalAsync(new SignInRequest("nobody.here", tooLong, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
        (await free.SignInLocalAsync(new SignInRequest(new string('x', 65), Password, _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.invalid_credentials");
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        (await db.Lockouts.FindAsync([uid], Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task IDN_U_103_Busy_hash_pool_is_rate_limited_not_an_exception()
    {
        await _harness.UserDirectory.CreateAsync(new NewUser("pool.user", "Pool User"), Password, _systemCtx, Ct);
        var uid = _harness.UserDirectory.FindByLogin("pool.user").Value.Id;

        using var pool = new SemaphoreSlim(0, 1);
        var busy = new Argon2PasswordHasher(Argon2PasswordHasher.TestParameters, pool, TimeSpan.FromMilliseconds(20));

        var signIn = await AuthWith(busy).SignInLocalAsync(new SignInRequest("pool.user", Password, _desktop), Ct);
        signIn.Error.Code.Value.ShouldBe("common.rate_limited");
        signIn.Error.Kind.ShouldBe(ErrorKind.TooManyRequests);

        var directory = new UserDirectory(_harness.DbFactory, _harness.SessionRegistry, new PasswordPolicy(new PasswordPolicyOptions(HistoryCount: 0)), busy, _harness.Clock, _harness.LockoutService);
        (await directory.SetPasswordAsync(uid, "AnotherPassword123!", false, _systemCtx, Ct)).Error.Code.Value.ShouldBe("common.rate_limited");
        (await directory.ChangeOwnPasswordAsync(uid, Password, "AnotherPassword123!", null, Ct)).Error.Code.Value.ShouldBe("common.rate_limited");
        (await directory.CreateAsync(new NewUser("pool.user2", "Pool User 2"), Password, _systemCtx, Ct)).Error.Code.Value.ShouldBe("common.rate_limited");

        // The pool bounds concurrency: with one slot, hashes run one at a time and all complete.
        using var single = new SemaphoreSlim(1, 1);
        var bounded = new Argon2PasswordHasher(Argon2PasswordHasher.TestParameters, single, TimeSpan.FromSeconds(30));
        var hashes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => bounded.HashPasswordAsync($"PasswordNumber{i}!", Ct)));
        hashes.Distinct().Count().ShouldBe(6);
        single.CurrentCount.ShouldBe(1);
    }

    [Fact]
    public async Task IDN_U_104_Stored_hash_with_out_of_range_parameters_or_truncated_hash_is_refused()
    {
        var hasher = _harness.FastHasher;
        var good = await hasher.HashPasswordAsync(Password, Ct);
        (await hasher.VerifyPasswordAsync(Password, good, Ct)).Verified.ShouldBeTrue();

        var parts = good.Split('$');   // "", argon2id, v=19, m=..,t=..,p=.., salt, hash
        string With(string parameters, string? hash = null) => string.Join('$', "", parts[1], parts[2], parameters, parts[4], hash ?? parts[5]);

        // 64 GiB of memory, absurd iterations or lanes: refused without computing anything.
        (await hasher.VerifyPasswordAsync(Password, With("m=67108864,t=1,p=1"), Ct)).Verified.ShouldBeFalse();
        (await hasher.VerifyPasswordAsync(Password, With("m=1024,t=1000000,p=1"), Ct)).Verified.ShouldBeFalse();
        (await hasher.VerifyPasswordAsync(Password, With("m=1024,t=1,p=4096"), Ct)).Verified.ShouldBeFalse();
        (await hasher.VerifyPasswordAsync(Password, With("m=99999999999999999999,t=1,p=1"), Ct)).Verified.ShouldBeFalse();

        // A hash truncated to 8 bytes would make guessing 2^192 times easier: refused even when it matches.
        var shortHasher = new Argon2PasswordHasher(new Argon2Parameters(1024, 1, 1, 16, 8));
        var truncated = await shortHasher.HashPasswordAsync(Password, Ct);
        (await hasher.VerifyPasswordAsync(Password, truncated, Ct)).Verified.ShouldBeFalse();

        // Salts are random per hash.
        (await hasher.HashPasswordAsync(Password, Ct)).ShouldNotBe(good);
    }

    [Fact]
    public async Task IDN_U_105_Access_token_algorithm_is_pinned_and_refusals_carry_no_detail()
    {
        var (user, session) = UserAndSession();
        var (jwt, _) = _harness.TokenService.IssueAccessToken(session, user);
        (await _harness.TokenService.ValidateAccessTokenAsync(jwt)).IsSuccess.ShouldBeTrue();

        var key = _harness.KeyRing.GetCurrentSigningKey();
        var segments = jwt.Split('.');
        var payload = segments[1];
        string Header(string alg, bool withKid = true) => Base64UrlEncoder.Encode(
            withKid ? $$"""{"alg":"{{alg}}","typ":"JWT","kid":"{{key.KeyId}}"}""" : $$"""{"alg":"{{alg}}","typ":"JWT"}""");

        // alg "none", with and without the original signature
        await ShouldBeInvalidAsync($"{Header("none")}.{payload}.");
        await ShouldBeInvalidAsync($"{Header("none")}.{payload}.{segments[2]}");

        // Algorithm confusion: HMAC keyed with the public key
        foreach (var hmacKey in new[] { key.PrivateKey.ExportSubjectPublicKeyInfo(), Encoding.UTF8.GetBytes(key.PrivateKey.ExportSubjectPublicKeyInfoPem()) })
        {
            var signingInput = $"{Header("HS256")}.{payload}";
            var mac = HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(signingInput));
            await ShouldBeInvalidAsync($"{signingInput}.{Base64UrlEncoder.Encode(mac)}");
        }

        // Right algorithm, foreign key under the ring's kid
        using var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ShouldBeInvalidAsync(Sign(new ECDsaSecurityKey(foreign) { KeyId = key.KeyId }, SecurityAlgorithms.EcdsaSha256, user, session));

        // Ring key but no kid header, another ES algorithm, no expiry
        await ShouldBeInvalidAsync(Sign(new ECDsaSecurityKey(key.PrivateKey), SecurityAlgorithms.EcdsaSha256, user, session));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        await ShouldBeInvalidAsync(Sign(new ECDsaSecurityKey(p384) { KeyId = key.KeyId }, SecurityAlgorithms.EcdsaSha384, user, session));
        await ShouldBeInvalidAsync(Sign(key.SecurityKey, SecurityAlgorithms.EcdsaSha256, user, session, withExpiry: false));

        // Tampered payload, garbage
        await ShouldBeInvalidAsync($"{segments[0]}.{Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(payload).Replace("\"lang\":\"en\"", "\"lang\":\"en\",\"adm\":true", StringComparison.Ordinal))}.{segments[2]}");
        await ShouldBeInvalidAsync("not-a-token");
        await ShouldBeInvalidAsync("a.b.c");
    }

    [Fact]
    public async Task IDN_U_106_Signing_keys_are_stored_in_bootstrap_secrets_and_loaded_by_another_ring()
    {
        var store = new FakeBootstrapStore();
        var (user, session) = UserAndSession();

        using var ring1 = new SigningKeyRing(_harness.Clock, null, store);
        var service1 = new JwtTokenService(ring1, _harness.SessionRegistry, _harness.Clock);
        var (jwt, _) = service1.IssueAccessToken(session, user);
        var kid = ring1.GetCurrentSigningKey().KeyId;
        store.Exists($"identity.jwt.{kid}").ShouldBeTrue();
        store.Exists(SigningKeyRing.IndexSecretName).ShouldBeTrue();

        // "Restart" (or a second node): a new ring over the same store validates and signs with the same key.
        using var ring2 = new SigningKeyRing(_harness.Clock, null, store);
        var service2 = new JwtTokenService(ring2, _harness.SessionRegistry, _harness.Clock);
        (await service2.ValidateAccessTokenAsync(jwt)).IsSuccess.ShouldBeTrue();
        ring2.GetCurrentSigningKey().KeyId.ShouldBe(kid);

        // A key created by the other node later is found by its kid (reload at most every 30 s).
        var rotated = ring1.RotateNow();
        var (jwt2, _) = service1.IssueAccessToken(session, user);
        _harness.Clock.Advance(TimeSpan.FromSeconds(31));
        var validated = await service2.ValidateAccessTokenAsync(jwt2);
        validated.IsSuccess.ShouldBeTrue();
        ring2.GetCurrentSigningKey().KeyId.ShouldBe(rotated.KeyId);

        // A key that cannot be stored is never used: fail closed.
        using var ring3 = new SigningKeyRing(_harness.Clock, null, new FakeBootstrapStore { FailWrites = true });
        Should.Throw<InvalidOperationException>(() => ring3.GetCurrentSigningKey());
        ring3.GetValidationKeys().ShouldBeEmpty();

        // An index that cannot be read does not silently start a new ring.
        store.FailReads = true;
        using var ring4 = new SigningKeyRing(_harness.Clock, null, store);
        Should.Throw<InvalidOperationException>(() => ring4.GetCurrentSigningKey());
    }

    [Fact]
    public async Task IDN_U_107_Explicit_rotation_retires_the_previous_key_after_one_day()
    {
        var service = new JwtTokenService(_harness.KeyRing, _harness.SessionRegistry, _harness.Clock, new JwtTokenOptions(TimeSpan.FromDays(40)));
        var (user, session) = UserAndSession(TimeSpan.FromDays(50));
        var (oldJwt, _) = service.IssueAccessToken(session, user);
        var oldKid = _harness.KeyRing.GetCurrentSigningKey().KeyId;

        var newKey = _harness.KeyRing.RotateNow();
        newKey.KeyId.ShouldNotBe(oldKid);
        _harness.KeyRing.GetCurrentSigningKey().KeyId.ShouldBe(newKey.KeyId);
        (await service.ValidateAccessTokenAsync(oldJwt)).IsSuccess.ShouldBeTrue();

        _harness.Clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
        (await service.ValidateAccessTokenAsync(oldJwt)).IsSuccess.ShouldBeFalse();
        var (newJwt, _) = service.IssueAccessToken(session, user);
        (await service.ValidateAccessTokenAsync(newJwt)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_108_Concurrent_refresh_with_one_token_rotates_once_and_revokes_the_session()
    {
        await _harness.UserDirectory.CreateAsync(new NewUser("race.user", "Race User"), Password, _systemCtx, Ct);
        var signed = (SignInOutcome.Signed)(await _harness.CreateAuthService().SignInLocalAsync(new SignInRequest("race.user", Password, _desktop), Ct)).Value;
        var token = signed.Tokens.RefreshToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => _harness.RefreshStore.RotateRefreshTokenAsync(token, _desktop, Ct), Ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error.Code.Value == "identity.refresh.reused");
        _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeTrue();

        // The one new token died with the session.
        var winner = results.Single(r => r.IsSuccess).Value.NewToken;
        (await _harness.RefreshStore.RotateRefreshTokenAsync(winner, _desktop, Ct)).IsSuccess.ShouldBeFalse();

        // Tokens are stored hashed only.
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var stored = await db.RefreshTokens.Select(r => r.TokenHash).ToListAsync(Ct);
        stored.ShouldNotContain(token);
        stored.ShouldContain(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    }

    [Fact]
    public async Task IDN_U_108_Rotation_by_another_node_is_detected_through_the_concurrency_token()
    {
        await _harness.UserDirectory.CreateAsync(new NewUser("node.user", "Node User"), Password, _systemCtx, Ct);
        var signed = (SignInOutcome.Signed)(await _harness.CreateAuthService().SignInLocalAsync(new SignInRequest("node.user", Password, _desktop), Ct)).Value;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed.Tokens.RefreshToken)));

        // "Node B" read the token row before "node A" rotated it; its write must fail, not fork the family.
        await using var nodeB = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var row = await nodeB.RefreshTokens.SingleAsync(r => r.TokenHash == hash, Ct);

        (await _harness.RefreshStore.RotateRefreshTokenAsync(signed.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();

        row.RotatedAt = _harness.Clock.GetUtcNow();
        row.ReplacedByHash = new string('0', 64);
        var conflict = await Should.ThrowAsync<StorageException>(() => nodeB.SaveChangesAsync(Ct));
        conflict.Error.Code.Value.ShouldBe("storage.concurrency.conflict");
    }

    [Fact]
    public async Task IDN_U_109_Revocations_and_active_sessions_survive_a_restart()
    {
        var (user, revokedSession) = UserAndSession();
        var activeSession = revokedSession with { Id = SessionId.New(), ClientInstanceId = "inst-2" };
        var endedSession = revokedSession with { Id = SessionId.New(), ExpiresAt = _harness.Clock.GetUtcNow().AddMinutes(1) };
        await _harness.SessionRegistry.RegisterNewSessionAsync(revokedSession, Ct);
        await _harness.SessionRegistry.RegisterNewSessionAsync(activeSession, Ct);
        await _harness.SessionRegistry.RegisterNewSessionAsync(endedSession, Ct);
        var (jwt, _) = _harness.TokenService.IssueAccessToken(revokedSession, user);
        await _harness.SessionRegistry.RevokeAsync(revokedSession.Id, "Explicit sign-out", null, Ct);
        _harness.Clock.Advance(TimeSpan.FromMinutes(2));

        // A new registry over the same database: what a restarted node has.
        using var restarted = new SessionRegistry(_harness.DbFactory, _harness.Clock);
        restarted.IsRevoked(revokedSession.Id).ShouldBeTrue();
        restarted.IsRevoked(activeSession.Id).ShouldBeFalse();
        restarted.ListActive(new SessionQuery(UserId: user.Id)).Select(s => s.Id).ShouldBe([activeSession.Id]);

        var service = new JwtTokenService(_harness.KeyRing, restarted, _harness.Clock);
        (await service.ValidateAccessTokenAsync(jwt)).Error.Code.Value.ShouldBe("identity.token.revoked");

        // RevokeWhere (maintenance mode, rights changes) reaches sessions created before the restart.
        (await restarted.RevokeWhereAsync(s => s.UserId == user.Id, "Maintenance", Ct)).ShouldBe(1);
        restarted.IsRevoked(activeSession.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_110_Revocation_is_not_lost_under_concurrent_touch_and_is_published_once()
    {
        var sink = new CountingSink();
        using var registry = new SessionRegistry(_harness.DbFactory, _harness.Clock, sink);
        var (_, session) = UserAndSession();
        await registry.RegisterNewSessionAsync(session, Ct);

        // First touch persists in the background; let it finish so that the loop below stays in memory.
        _harness.Clock.Advance(TimeSpan.FromSeconds(1));
        registry.Touch(session.Id);
        var touchedAt = _harness.Clock.GetUtcNow();
        SpinWait.SpinUntil(
            () =>
            {
                using var check = _harness.DbFactory.CreateDbContext();
                return check.Sessions.Find(session.Id)!.LastActivityAt == touchedAt;
            },
            TimeSpan.FromSeconds(10)).ShouldBeTrue();

        using var stop = new CancellationTokenSource();
        var touching = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    registry.Touch(session.Id);
                }
            },
            Ct)).ToArray();

        var revocations = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(
            () => registry.RevokeAsync(session.Id, $"Reason {i}", null, Ct), Ct)));
        for (var i = 0; i < 10_000; i++)
        {
            registry.Touch(session.Id);
        }

        await stop.CancelAsync();
        await Task.WhenAll(touching);

        revocations.ShouldAllBe(r => r.IsSuccess);
        registry.IsRevoked(session.Id).ShouldBeTrue();
        registry.Get(session.Id).Value.RevokedAt.ShouldNotBeNull();
        registry.ListActive(new SessionQuery()).ShouldBeEmpty();
        sink.Count.ShouldBe(1);

        // A later revocation keeps the first reason.
        await registry.RevokeAsync(session.Id, "Later", null, Ct);
        sink.Count.ShouldBe(1);
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        (await db.Sessions.FindAsync([session.Id], Ct))!.RevokedReason.ShouldBe(sink.Reasons.Single());
    }

    [Fact]
    public async Task IDN_U_111_Change_token_is_random_single_use_and_dies_with_the_password_or_the_account()
    {
        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("change.user", "Change User", MustChangePassword: true), "OldPassword123!", _systemCtx, Ct)).Value;
        var auth = _harness.CreateAuthService();

        async Task<string> TokenAsync() =>
            ((SignInOutcome.PasswordChangeRequired)(await auth.SignInLocalAsync(new SignInRequest("change.user", "OldPassword123!", _desktop), Ct)).Value).ChangeToken;

        var first = await TokenAsync();
        var second = await TokenAsync();
        first.ShouldNotBe(second);
        Base64UrlEncoder.DecodeBytes(first).Length.ShouldBe(32);

        // One live token per user: the first one is dead.
        (await auth.CompletePasswordChangeAsync(first, "BrandNewPassword123!", Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");

        // A refused new password does not cost the token ...
        var weak = await auth.CompletePasswordChangeAsync(second, "weak", Ct);
        weak.Error.Kind.ShouldBe(ErrorKind.Validation);

        // ... a successful change does, and it signs in.
        var done = await auth.CompletePasswordChangeAsync(second, "BrandNewPassword123!", Ct);
        done.IsSuccess.ShouldBeTrue();
        _harness.UserDirectory.Get(uid).Value.MustChangePassword.ShouldBeFalse();
        _harness.UserDirectory.Get(uid).Value.LastSignInAt.ShouldBe(_harness.Clock.GetUtcNow());
        (await auth.CompletePasswordChangeAsync(second, "YetAnotherPassword123!", Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");

        // Expiry (10 min)
        await _harness.UserDirectory.SetPasswordAsync(uid, "ResetByAdmin123!", mustChange: true, _systemCtx, Ct);
        async Task<string> ResetTokenAsync() =>
            ((SignInOutcome.PasswordChangeRequired)(await auth.SignInLocalAsync(new SignInRequest("change.user", "ResetByAdmin123!", _desktop), Ct)).Value).ChangeToken;
        var expired = await ResetTokenAsync();
        _harness.Clock.Advance(TimeSpan.FromMinutes(11));
        (await auth.CompletePasswordChangeAsync(expired, "AfterExpiryPassword123!", Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");

        // Account disabled after the token was issued: no password change, no session.
        var late = await ResetTokenAsync();
        var version = _harness.UserDirectory.Get(uid).Value.Version;
        (await _harness.UserDirectory.UpdateAsync(uid, new UserUpdate(Enabled: false), version, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        (await auth.CompletePasswordChangeAsync(late, "AfterDisablePassword123!", Ct)).Error.Code.Value.ShouldBe("identity.signin.disabled");
        _harness.SessionRegistry.ListActive(new SessionQuery(UserId: uid)).ShouldBeEmpty();
    }

    [Fact]
    public async Task IDN_U_112_Own_password_change_revokes_other_sessions_and_wrong_current_password_locks()
    {
        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("own.user", "Own User"), Password, _systemCtx, Ct)).Value;
        var auth = _harness.CreateAuthService();
        var web = new ClientContext(ClientKind.Web, "inst-2");
        var current = (SignInOutcome.Signed)(await auth.SignInLocalAsync(new SignInRequest("own.user", Password, _desktop), Ct)).Value;
        var other = (SignInOutcome.Signed)(await auth.SignInLocalAsync(new SignInRequest("own.user", Password, web), Ct)).Value;

        (await _harness.UserDirectory.ChangeOwnPasswordAsync(uid, Password, "ChangedPassword123!", current.Tokens.Session, Ct)).IsSuccess.ShouldBeTrue();

        _harness.SessionRegistry.IsRevoked(current.Tokens.Session).ShouldBeFalse();
        _harness.SessionRegistry.IsRevoked(other.Tokens.Session).ShouldBeTrue();
        (await auth.RefreshAsync(other.Tokens.RefreshToken, web, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.TokenService.ValidateAccessTokenAsync(other.Tokens.AccessToken)).Error.Code.Value.ShouldBe("identity.token.revoked");
        (await auth.RefreshAsync(current.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();

        // Without a current session every session goes.
        (await _harness.UserDirectory.ChangeOwnPasswordAsync(uid, "ChangedPassword123!", "ChangedAgain123!", null, Ct)).IsSuccess.ShouldBeTrue();
        _harness.SessionRegistry.IsRevoked(current.Tokens.Session).ShouldBeTrue();

        // Guessing the current password is bound by the lockout, like sign-in.
        for (var i = 0; i < 5; i++)
        {
            var wrong = await _harness.UserDirectory.ChangeOwnPasswordAsync(uid, $"WrongGuess{i}-123!", "NeverApplied123!", null, Ct);
            wrong.Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.incorrect");
        }

        (await _harness.UserDirectory.ChangeOwnPasswordAsync(uid, "ChangedAgain123!", "NeverApplied123!", null, Ct)).Error.Code.Value.ShouldBe("identity.signin.locked");
        (await auth.SignInLocalAsync(new SignInRequest("own.user", "ChangedAgain123!", _desktop), Ct)).Error.Code.Value.ShouldBe("identity.signin.locked");
    }

    [Fact]
    public async Task IDN_U_113_Only_administrators_promote_to_administrator()
    {
        var admin = (await _harness.UserDirectory.CreateAsync(new NewUser("admin.one", "Admin One", Administrator: true), Password, _systemCtx, Ct)).Value;
        var editor = (await _harness.UserDirectory.CreateAsync(new NewUser("editor.one", "Editor One"), Password, _systemCtx, Ct)).Value;
        var target = (await _harness.UserDirectory.CreateAsync(new NewUser("target.one", "Target One"), Password, _systemCtx, Ct)).Value;

        var byEditor = await _harness.UserDirectory.UpdateAsync(target, new UserUpdate(Administrator: true), 1, new ChangeContext(editor, "api"), Ct);
        byEditor.Error.Code.Value.ShouldBe("identity.user.admin_required");
        _harness.UserDirectory.Get(target).Value.Administrator.ShouldBeFalse();

        // Self-promotion is refused too (U4 or U5).
        (await _harness.UserDirectory.UpdateAsync(editor, new UserUpdate(Administrator: true), 1, new ChangeContext(editor, "api"), Ct)).IsSuccess.ShouldBeFalse();

        (await _harness.UserDirectory.UpdateAsync(target, new UserUpdate(Administrator: true), 1, new ChangeContext(admin, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.UserDirectory.Get(target).Value.Administrator.ShouldBeTrue();
    }

    [Fact]
    public void IDN_U_114_Rate_limiter_memory_is_bounded()
    {
        var limiter = new SignInRateLimiter(_harness.Clock, new SignInRateLimiterOptions(RatePerIp: 1_000_000, RatePerLogin: 10));

        // Logins longer than a login can be share one bucket: the 11th is refused whatever its text.
        for (var i = 0; i < 10; i++)
        {
            limiter.TryAcquire(null, new string('x', 100_000) + i).ShouldBeTrue();
        }

        limiter.TryAcquire(null, new string('y', 65)).ShouldBeFalse();
        limiter.TrackedKeys.ShouldBe(1);

        // Keys with no attempt left in the window are dropped.
        for (var i = 0; i < 2000; i++)
        {
            limiter.TryAcquire(System.Net.IPAddress.Parse($"10.0.{i / 250}.{i % 250}"), $"user{i}").ShouldBeTrue();
        }

        limiter.TrackedKeys.ShouldBeGreaterThan(2000);
        _harness.Clock.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 256; i++)
        {
            limiter.TryAcquire(null, "steady.user");
        }

        limiter.TrackedKeys.ShouldBeLessThan(10);
    }

    [Fact]
    public async Task IDN_U_115_Concurrent_failures_are_all_counted_and_the_lock_count_decays_after_24_hours()
    {
        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("burst.user", "Burst User"), Password, _systemCtx, Ct)).Value;
        var lockout = _harness.LockoutService;

        // Five wrong passwords at once: the fifth locks (a lost update would leave the account open).
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => lockout.RecordFailureAsync(uid, Ct), Ct)));
        var (locked, until) = await lockout.CheckLockoutAsync(uid, Ct);
        locked.ShouldBeTrue();
        until.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(15));

        // Second lock within 24 h: doubled.
        _harness.Clock.Advance(TimeSpan.FromMinutes(16));
        (await FailAsync(uid, 5)).ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(30));

        // More than 24 h after the last lock ended: back to the base duration.
        _harness.Clock.Advance(TimeSpan.FromHours(25));
        (await FailAsync(uid, 5)).ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(15));
    }

    [Fact]
    public async Task IDN_U_116_Bootstrap_admin_failure_is_logged_and_the_admin_is_created_once()
    {
        using var logs = new LogCollector();
        using var loggers = new LoggerFactory([logs]);
        var variable = "WP_TEST_BOOTSTRAP_" + Guid.NewGuid().ToString("N");
        var service = new BootstrapAdminService(_harness.DbFactory, _harness.UserDirectory, new BootstrapAdminOptions("admin", variable), loggers.CreateLogger<BootstrapAdminService>());
        var leaks = new SecretLeakDetector();
        try
        {
            // A password that does not meet the policy: no administrator, and it is said (never the password).
            Environment.SetEnvironmentVariable(variable, "weak-pw");
            leaks.Register("weak-pw");
            await service.ExecuteAsync(isProduction: true, Ct);
            _harness.UserDirectory.FindByLogin("admin").IsSuccess.ShouldBeFalse();
            logs.WithEventId(2702).ShouldHaveSingleItem().RenderedMessage.ShouldContain("identity.password.too_short");
            logs.WithEventId(2701).ShouldHaveSingleItem();

            Environment.SetEnvironmentVariable(variable, "Zebra-Quartz-Lamp-42!");
            leaks.Register("Zebra-Quartz-Lamp-42!");
            await service.ExecuteAsync(isProduction: true, Ct);
            var admin = _harness.UserDirectory.FindByLogin("admin").Value;
            admin.Administrator.ShouldBeTrue();
            admin.MustChangePassword.ShouldBeTrue();

            // Later starts change nothing: not the password, not a second account.
            Environment.SetEnvironmentVariable(variable, "Other-Quartz-Lamp-43!");
            await service.ExecuteAsync(isProduction: true, Ct);
            await service.ExecuteAsync(isProduction: false, Ct);
            _harness.UserDirectory.Query(new UserQuery(Administrator: true), new Kernel.Paging.PageRequest(100)).Items.Count.ShouldBe(1);
            var auth = _harness.CreateAuthService();
            (await auth.SignInLocalAsync(new SignInRequest("admin", "Other-Quartz-Lamp-43!", _desktop), Ct)).IsSuccess.ShouldBeFalse();
            var firstPassword = await auth.SignInLocalAsync(new SignInRequest("admin", "Zebra-Quartz-Lamp-42!", _desktop), Ct);
            firstPassword.IsSuccess.ShouldBeTrue(firstPassword.IsSuccess ? null : firstPassword.Error.Code.Value);
            firstPassword.Value.ShouldBeOfType<SignInOutcome.PasswordChangeRequired>();

            leaks.AssertNoLeak(logs);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task IDN_U_117_Access_token_never_outlives_its_session_and_needs_a_known_client_kind()
    {
        var (user, session) = UserAndSession(TimeSpan.FromMinutes(2));
        var (jwt, expiresAt) = _harness.TokenService.IssueAccessToken(session, user);
        expiresAt.ShouldBe(session.ExpiresAt);
        (await _harness.TokenService.ValidateAccessTokenAsync(jwt)).Value.ExpiresAt.ShouldBe(session.ExpiresAt);

        var key = _harness.KeyRing.GetCurrentSigningKey().SecurityKey;
        await ShouldBeInvalidAsync(Sign(key, SecurityAlgorithms.EcdsaSha256, user, session, clientKind: null));
        await ShouldBeInvalidAsync(Sign(key, SecurityAlgorithms.EcdsaSha256, user, session, clientKind: "root"));
        await ShouldBeInvalidAsync(Sign(key, SecurityAlgorithms.EcdsaSha256, user, session, clientKind: "7"));
        (await _harness.TokenService.ValidateAccessTokenAsync(Sign(key, SecurityAlgorithms.EcdsaSha256, user, session, clientKind: "web"))).Value.ClientKind.ShouldBe(ClientKind.Web);
    }

    [Fact]
    public async Task IDN_U_118_Active_login_stays_unique_when_an_older_deleted_user_had_it()
    {
        var first = (await _harness.UserDirectory.CreateAsync(new NewUser("alice", "Alice One"), Password, _systemCtx, Ct)).Value;
        (await _harness.UserDirectory.DeleteAsync(first, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        _harness.Clock.Advance(TimeSpan.FromDays(31));

        (await _harness.UserDirectory.CreateAsync(new NewUser("alice", "Alice Two"), Password, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        var third = await _harness.UserDirectory.CreateAsync(new NewUser("ALICE", "Alice Three"), Password, _systemCtx, Ct);
        third.Error.Code.Value.ShouldBe("identity.user.login_taken");
    }

    [Fact]
    public async Task IDN_U_119_Refresh_refused_for_a_disabled_or_deleted_user_revokes_the_session()
    {
        var auth = _harness.CreateAuthService();

        // Disabled directly in the database (UpdateAsync would already revoke): the refresh itself must notice.
        var disabled = await SignedAsync(auth, "refresh.disabled");
        await SetUserAsync(disabled.User.Id, u => u.Enabled = false);
        (await auth.RefreshAsync(disabled.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.signin.disabled");
        _harness.SessionRegistry.IsRevoked(disabled.Tokens.Session).ShouldBeTrue();

        var deleted = await SignedAsync(auth, "refresh.deleted");
        await SetUserAsync(deleted.User.Id, u => u.DeletedAt = _harness.Clock.GetUtcNow());
        (await auth.RefreshAsync(deleted.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.refresh.invalid");
        _harness.SessionRegistry.IsRevoked(deleted.Tokens.Session).ShouldBeTrue();

        var expired = await SignedAsync(auth, "refresh.expired");
        await SetUserAsync(expired.User.Id, u => u.ValidUntil = _harness.Clock.GetUtcNow().AddMinutes(-1));
        (await auth.RefreshAsync(expired.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.signin.expired");
        _harness.SessionRegistry.IsRevoked(expired.Tokens.Session).ShouldBeTrue();

        // Nothing was rotated and the session is dead: the token stays refused, as a revoked session, not as a reuse.
        (await auth.RefreshAsync(disabled.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.token.revoked");
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        (await db.RefreshTokens.CountAsync(Ct)).ShouldBe(3);
        (await db.RefreshTokens.CountAsync(r => r.RotatedAt != null, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task IDN_U_120_Refresh_refused_by_an_admission_hook_leaves_the_token_usable()
    {
        var auth = _harness.CreateAuthService();
        var signed = await SignedAsync(auth, "refresh.maintenance");

        var maintenance = new SwitchableAdmission { Refuse = true };
        _harness.Admissions.Add(maintenance);
        var during = _harness.CreateAuthService();

        for (var i = 0; i < 3; i++)
        {
            (await during.RefreshAsync(signed.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("maintenance.active");
        }

        _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeFalse();
        await using (var db = await _harness.DbFactory.CreateDbContextAsync(Ct))
        {
            (await db.RefreshTokens.CountAsync(Ct)).ShouldBe(1);
        }

        // Maintenance over: the same token refreshes, once.
        maintenance.Refuse = false;
        var after = await during.RefreshAsync(signed.Tokens.RefreshToken, _desktop, Ct);
        after.IsSuccess.ShouldBeTrue();
        after.Value.RefreshToken.ShouldNotBe(signed.Tokens.RefreshToken);
        (await _harness.TokenService.ValidateAccessTokenAsync(after.Value.AccessToken)).IsSuccess.ShouldBeTrue();

        (await during.RefreshAsync(signed.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.refresh.reused");
        _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_121_Concurrent_refresh_through_the_service_has_one_winner_and_a_reused_token_never_reaches_the_checks()
    {
        var hook = new SwitchableAdmission();
        _harness.Admissions.Add(hook);
        var auth = _harness.CreateAuthService();
        var signed = await SignedAsync(auth, "refresh.race");
        var token = signed.Tokens.RefreshToken;

        // The store level is used for the race itself (RefreshAsync also touches the session in the background).
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => _harness.RefreshStore.RotateRefreshTokenAsync(
                token,
                _desktop,
                (_, _) =>
                {
                    Interlocked.Increment(ref hook.Calls);
                    return Task.FromResult(Result.Success());
                },
                Ct),
            Ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Where(r => !r.IsSuccess).ShouldAllBe(r => r.Error.Code.Value == "identity.refresh.reused");
        hook.Calls.ShouldBe(1);
        _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeTrue();

        // Reuse is detected before the user and admission checks, whatever they would say.
        hook.Refuse = true;
        var again = await SignedAsync(auth, "refresh.race2");
        (await _harness.RefreshStore.RotateRefreshTokenAsync(again.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeTrue();
        var before = hook.Calls;
        (await auth.RefreshAsync(again.Tokens.RefreshToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.refresh.reused");
        hook.Calls.ShouldBe(before);
        _harness.SessionRegistry.IsRevoked(again.Tokens.Session).ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_122_Password_history_keeps_the_last_five_hashes_only()
    {
        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("prune.user", "Prune User"), "StartPassword-0!", _systemCtx, Ct)).Value;

        for (var i = 1; i <= 4; i++)
        {
            _harness.Clock.Advance(TimeSpan.FromMinutes(1));
            (await _harness.UserDirectory.SetPasswordAsync(uid, $"NextPassword-{i}!", false, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        }

        for (var i = 5; i <= 7; i++)
        {
            _harness.Clock.Advance(TimeSpan.FromMinutes(1));
            (await _harness.UserDirectory.ChangeOwnPasswordAsync(uid, $"NextPassword-{i - 1}!", $"NextPassword-{i}!", null, Ct)).IsSuccess.ShouldBeTrue();
        }

        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var history = await db.PasswordHistory.Where(h => h.UserId == uid).Select(h => h.Hash).ToListAsync(Ct);
        history.Count.ShouldBe(5);

        // The five kept are the five newest (passwords 3 to 7); the hashes of 0, 1 and 2 are gone.
        async Task<bool> KeptAsync(string password)
        {
            foreach (var hash in history)
            {
                if ((await _harness.FastHasher.VerifyPasswordAsync(password, hash, Ct)).Verified)
                {
                    return true;
                }
            }

            return false;
        }

        (await KeptAsync("StartPassword-0!")).ShouldBeFalse();
        (await KeptAsync("NextPassword-2!")).ShouldBeFalse();
        (await KeptAsync("NextPassword-3!")).ShouldBeTrue();
        (await KeptAsync("NextPassword-7!")).ShouldBeTrue();

        // The policy still rejects the last five and accepts the sixth back.
        (await _harness.UserDirectory.SetPasswordAsync(uid, "NextPassword-3!", false, _systemCtx, Ct)).Error.FieldErrors[0].Code.Value.ShouldBe("identity.password.reused");
        (await _harness.UserDirectory.SetPasswordAsync(uid, "NextPassword-2!", false, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_123_Sign_in_succeeds_when_the_user_row_changes_during_it()
    {
        var uid = (await _harness.UserDirectory.CreateAsync(new NewUser("busy.user", "Busy User"), Password, _systemCtx, Ct)).Value;

        // An administrator edits the user between the password check and the LastSignInAt write (the hook runs
        // there): the row version moved, and that write must not turn a successful sign-in into an error.
        _harness.Admissions.Add(new CallbackAdmission(async () =>
        {
            var version = _harness.UserDirectory.Get(uid).Value.Version;
            (await _harness.UserDirectory.UpdateAsync(uid, new UserUpdate(Notes: "edited meanwhile"), version, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        }));

        var outcome = await _harness.CreateAuthService().SignInLocalAsync(new SignInRequest("busy.user", Password, _desktop), Ct);
        outcome.IsSuccess.ShouldBeTrue();
        var signed = outcome.Value.ShouldBeOfType<SignInOutcome.Signed>();
        _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeFalse();
        _harness.UserDirectory.Get(uid).Value.Notes.ShouldBe("edited meanwhile");
    }

    [Fact]
    public async Task IDN_U_124_Kind_changed_to_template_or_script_revokes_the_sessions()
    {
        var auth = _harness.CreateAuthService();
        foreach (var kind in new[] { UserKind.Template, UserKind.Script })
        {
            var signed = await SignedAsync(auth, $"kind.{kind}".ToLowerInvariant());
            var version = _harness.UserDirectory.Get(signed.User.Id).Value.Version;
            (await _harness.UserDirectory.UpdateAsync(signed.User.Id, new UserUpdate(Kind: kind), version, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();

            _harness.SessionRegistry.IsRevoked(signed.Tokens.Session).ShouldBeTrue();
            (await _harness.TokenService.ValidateAccessTokenAsync(signed.Tokens.AccessToken)).Error.Code.Value.ShouldBe("identity.token.revoked");
            (await auth.RefreshAsync(signed.Tokens.RefreshToken, _desktop, Ct)).IsSuccess.ShouldBeFalse();
        }

        // A change that keeps a sign-in kind keeps the session.
        var kept = await SignedAsync(auth, "kind.person");
        (await _harness.UserDirectory.UpdateAsync(kept.User.Id, new UserUpdate(Kind: UserKind.Federation), _harness.UserDirectory.Get(kept.User.Id).Value.Version, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        _harness.SessionRegistry.IsRevoked(kept.Tokens.Session).ShouldBeFalse();
    }

    private async Task<SignInOutcome.Signed> SignedAsync(AuthenticationService auth, string login)
    {
        (await _harness.UserDirectory.CreateAsync(new NewUser(login, "Refresh Person"), Password, _systemCtx, Ct)).IsSuccess.ShouldBeTrue();
        var outcome = await auth.SignInLocalAsync(new SignInRequest(login, Password, _desktop), Ct);
        outcome.IsSuccess.ShouldBeTrue();
        return outcome.Value.ShouldBeOfType<SignInOutcome.Signed>();
    }

    private async Task SetUserAsync(UserId id, Action<Watchpoint.Identity.Internal.Storage.UserEntity> change)
    {
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var entity = await db.Users.SingleAsync(u => u.Id == id, Ct);
        change(entity);
        await db.SaveChangesAsync(Ct);
    }

    private AuthenticationService AuthWith(Argon2PasswordHasher hasher) =>
        new(_harness.DbFactory, _harness.UserDirectory, hasher, _harness.TokenService, _harness.RefreshStore, _harness.SessionRegistry,
            _harness.LockoutService, new SignInRateLimiter(_harness.Clock, new SignInRateLimiterOptions(1000, 1000)), [], _harness.Clock);

    private async Task<DateTimeOffset?> FailAsync(UserId uid, int times)
    {
        DateTimeOffset? until = null;
        for (var i = 0; i < times; i++)
        {
            (_, until) = await _harness.LockoutService.RecordFailureAsync(uid, Ct);
        }

        return until;
    }

    private async Task ShouldBeInvalidAsync(string jwt)
    {
        var result = await _harness.TokenService.ValidateAccessTokenAsync(jwt);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe("identity.token.invalid");
        result.Error.Args.ShouldBeEmpty();
    }

    private (User User, Session Session) UserAndSession(TimeSpan? sessionLength = null)
    {
        var now = _harness.Clock.GetUtcNow();
        var user = new User(
            UserId.New(), "token.user", "Token User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);
        var session = new Session(
            SessionId.New(), user.Id, ClientKind.Desktop, "inst-1", null, null, null, null,
            now, now, now + (sessionLength ?? TimeSpan.FromDays(1)), null, null);
        return (user, session);
    }

    private string Sign(SecurityKey key, string algorithm, User user, Session session, bool withExpiry = true, string? clientKind = "desktop")
    {
        var now = _harness.Clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["sid"] = session.Id.ToString(),
            ["lang"] = "en",
            ["adm"] = true,
        };
        if (clientKind is not null)
        {
            claims["ck"] = clientKind;
        }

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _harness.TokenService.Issuer,
            Audience = "watchpoint",
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = withExpiry ? now.AddMinutes(10) : null,
            SigningCredentials = new SigningCredentials(key, algorithm),
        });
    }

    private sealed class SwitchableAdmission : ISessionAdmission
    {
#pragma warning disable CA1051, SA1401 // test counter incremented with Interlocked
        public int Calls;
#pragma warning restore CA1051, SA1401

        public bool Refuse { get; set; }

        public int Order => 1;

        public ValueTask<Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase, CancellationToken ct)
        {
            if (phase == AdmissionPhase.Refresh)
            {
                Interlocked.Increment(ref Calls);
            }

            return ValueTask.FromResult(Refuse && phase == AdmissionPhase.Refresh
                ? Result.Failure(Error.Unavailable("maintenance.active"))
                : Result.Success());
        }
    }

    private sealed class CallbackAdmission(Func<Task> callback) : ISessionAdmission
    {
        public int Order => 1;

        public async ValueTask<Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase, CancellationToken ct)
        {
            await callback();
            return Result.Success();
        }
    }

    private sealed class CountingSink : ISessionEventSink
    {
        private readonly ConcurrentQueue<string> _reasons = new();

        public int Count => _reasons.Count;

        public IReadOnlyCollection<string> Reasons => _reasons;

        public ValueTask PublishSessionRevokedAsync(SessionId sessionId, UserId userId, string reason, CancellationToken ct)
        {
            _reasons.Enqueue(reason);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBootstrapStore : IBootstrapSecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new();

        public bool FailWrites { get; set; }

        public bool FailReads { get; set; }

        public Result<SecretValue> Read(string name) =>
            FailReads ? Error.Unavailable("secrets.integrity.failed")
            : _values.TryGetValue(name, out var value) ? SecretValue.Copy(value)
            : Error.NotFound("secrets.not_found");

        public Result Write(string name, ReadOnlySpan<byte> value)
        {
            if (FailWrites)
            {
                return Error.Unavailable("secrets.write.failed");
            }

            _values[name] = value.ToArray();
            return Result.Success();
        }

        public bool Exists(string name) => _values.ContainsKey(name);
    }
}
