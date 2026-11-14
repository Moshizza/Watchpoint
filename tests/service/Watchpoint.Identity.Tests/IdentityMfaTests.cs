using System.Net;
using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Mfa;
using Watchpoint.Kernel.Ids;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Identity.Tests;

/// <summary>T3.08: MFA (spec IDN §10) — IDN-U-051…IDN-U-061 and regression tests IDN-U-130…IDN-U-133.</summary>
[Trait("layer", "U")]
[Trait("spec", "IDN")]
public sealed class IdentityMfaTests : IDisposable
{
    private const string Password = "ValidPassword123!";
    private const string RpId = "watchpoint.example.com";
    private const string Origin = "https://watchpoint.example.com";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ChangeContext _systemCtx = new(null, "test");
    private static readonly ClientContext _desktop = new(ClientKind.Desktop, "inst-1", RemoteIp: IPAddress.Loopback);
    private readonly TestIdentityHarness _harness = new(webAuthnOptions: new WebAuthnOptions(RpId, [Origin]));

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task IDN_U_051_Mfa_policy_resolution()
    {
        var admin = await UserAsync("mfa.admin", administrator: true);
        var person = await UserAsync("mfa.person");
        var configUser = await UserAsync("mfa.config");
        _harness.Permissions.Granted[configUser.Id] = ["config.devices.edit", "operate.alarms.view"];
        var breakGlass = (await _harness.UserDirectory.CreateAsync(new NewUser("breakglass", "Break Glass", Kind: UserKind.BreakGlass), Password, _systemCtx, Ct)).Value;
        var breakGlassUser = _harness.UserDirectory.Get(breakGlass).Value;

        // The user's own policy wins over the site policy.
        (await Mfa(MfaRequirement.None).IsRequiredAsync(person with { Mfa = MfaPolicy.Required }, Ct)).ShouldBeTrue();
        (await Mfa(MfaRequirement.Everyone).IsRequiredAsync(admin with { Mfa = MfaPolicy.NotRequired }, Ct)).ShouldBeFalse();

        // Inherit: site policy.
        (await Mfa(MfaRequirement.None).IsRequiredAsync(admin, Ct)).ShouldBeFalse();
        (await Mfa(MfaRequirement.Administrators).IsRequiredAsync(admin, Ct)).ShouldBeTrue();
        (await Mfa(MfaRequirement.Administrators).IsRequiredAsync(person, Ct)).ShouldBeFalse();
        (await Mfa(MfaRequirement.Administrators).IsRequiredAsync(configUser, Ct)).ShouldBeFalse();
        (await Mfa(MfaRequirement.ConfigurationUsers).IsRequiredAsync(configUser, Ct)).ShouldBeTrue();
        (await Mfa(MfaRequirement.ConfigurationUsers).IsRequiredAsync(admin, Ct)).ShouldBeTrue();
        (await Mfa(MfaRequirement.ConfigurationUsers).IsRequiredAsync(person, Ct)).ShouldBeFalse();
        (await Mfa(MfaRequirement.Everyone).IsRequiredAsync(person, Ct)).ShouldBeTrue();

        // Break-glass never (spec IDN §11), whatever the site says; its own policy is forced to NotRequired at creation.
        breakGlassUser.Mfa.ShouldBe(MfaPolicy.NotRequired);
        (await Mfa(MfaRequirement.Everyone).IsRequiredAsync(breakGlassUser with { Mfa = MfaPolicy.Required }, Ct)).ShouldBeFalse();

        MfaService Mfa(MfaRequirement requireFor) =>
            new(_harness.DbFactory, _harness.UserDirectory, _harness.FastHasher, _harness.Clock, _harness.WebAuthn, _harness.Auditor, new MfaOptions(requireFor), _harness.Permissions, _harness.Vault);
    }

    [Fact]
    public async Task IDN_U_052_Totp_enrolment_uri_format_and_confirm()
    {
        var user = await UserAsync("totp.user");
        var enrolment = (await _harness.Mfa.EnrollTotpAsync(user.Id, Ct)).Value;

        // 20-byte secret, base32 (32 characters, no padding), otpauth URI for the authenticator app.
        enrolment.SecretBase32.Length.ShouldBe(32);
        enrolment.SecretBase32.ShouldAllBe(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(c));
        enrolment.OtpAuthUri.ToString().ShouldBe($"otpauth://totp/Watchpoint%3Atotp.user?secret={enrolment.SecretBase32}&issuer=Watchpoint&algorithm=SHA1&digits=6&period=30");
        Totp.ToBase32(SecretOf(user.Id)).ShouldBe(enrolment.SecretBase32);

        // Not confirmed yet: no method, not usable at sign-in, and a wrong code does not confirm.
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).HasMethod.ShouldBeFalse();
        var wrong = await _harness.Mfa.ConfirmTotpAsync(user.Id, "000000", Ct);
        wrong.IsSuccess.ShouldBeFalse();
        wrong.Error.FieldErrors[0].Code.Value.ShouldBe("identity.mfa.code_invalid");
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).TotpEnrolled.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, CodeNow(user.Id), null, Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");

        // A second enrolment replaces the pending one (the old secret dies); a valid code confirms and yields 10 codes.
        var second = (await _harness.Mfa.EnrollTotpAsync(user.Id, Ct)).Value;
        second.SecretBase32.ShouldNotBe(enrolment.SecretBase32);
        _harness.Vault.Count.ShouldBe(1);
        var codes = (await _harness.Mfa.ConfirmTotpAsync(user.Id, CodeNow(user.Id), Ct)).Value;
        codes.Codes.Count.ShouldBe(10);
        codes.Codes.ShouldAllBe(c => c.Length == 11 && c[5] == '-');
        codes.Codes.Distinct().Count().ShouldBe(10);
        var status = await _harness.Mfa.GetStatusAsync(user.Id, Ct);
        status.TotpEnrolled.ShouldBeTrue();
        status.RecoveryCodesRemaining.ShouldBe(10);
        status.Methods.ShouldBe(["totp", "recovery"]);

        // Confirmed: no new enrolment without a reset.
        (await _harness.Mfa.EnrollTotpAsync(user.Id, Ct)).Error.Code.Value.ShouldBe("identity.mfa.already_enrolled");
        _harness.Audit.Of("identity.mfaEnrolled").ShouldHaveSingleItem().Details["method"].ShouldBe("totp");
    }

    [Fact]
    public async Task IDN_U_053_Totp_window_is_one_step_and_a_step_is_accepted_once()
    {
        var user = await UserAsync("window.user");
        await EnrolTotpAsync(user.Id);
        var secret = SecretOf(user.Id);
        _harness.Clock.Advance(TimeSpan.FromMinutes(2));
        var step = Totp.StepOf(_harness.Clock.GetUtcNow());

        // ±1 step accepted, ±2 refused.
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step - 2), null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step + 2), null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step - 1), null, Ct)).IsSuccess.ShouldBeTrue();

        // Replay: the same code again, and any code of a step at or before the accepted one, is refused.
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step - 1), null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step), null, Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step), null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step + 1), null, Ct)).IsSuccess.ShouldBeTrue();

        // Thirty seconds later the clock caught up with the step already used: still refused; the next one works.
        _harness.Clock.Advance(TimeSpan.FromSeconds(30));
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step + 1), null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, Totp.Compute(secret, step + 2), null, Ct)).IsSuccess.ShouldBeTrue();

        // Malformed codes never match and reveal nothing.
        foreach (var bad in new[] { "", "12345", "1234567", "12345a", "123 456" })
        {
            var refused = await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, bad, null, Ct);
            refused.Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
            refused.Error.Args.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task IDN_U_054_Totp_secret_lives_in_the_vault_not_in_the_database()
    {
        var user = await UserAsync("vault.user");
        var enrolment = (await _harness.Mfa.EnrollTotpAsync(user.Id, Ct)).Value;
        var secret = SecretOf(user.Id);

        // The row holds the secret id only; neither the bytes nor their base32 form are anywhere in the identity tables.
        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var row = db.Totp.Single(t => t.UserId == user.Id);
        row.SecretId.ShouldBe(_harness.Vault.All.Single().Id.Value);
        var dump = string.Join("|", db.Totp.Select(t => t.UserId + ":" + t.SecretId + ":" + t.LastUsedStep).ToList());
        dump.ShouldNotContain(enrolment.SecretBase32);
        dump.ShouldNotContain(Convert.ToBase64String(secret));
        dump.ShouldNotContain(Convert.ToHexString(secret));

        // The vault entry is owned by Identity for this user: another module or user cannot read it.
        var stored = _harness.Vault.All.Single();
        stored.Owner.ShouldBe(new SecretOwner("Identity", user.Id.Value));
        stored.Name.ShouldBe("identity.totp." + user.Id);
        (await _harness.Vault.ReadAsync(stored.Id, new SecretOwner("Integrations", user.Id.Value), "x", Ct)).Error.Code.Value.ShouldBe("secrets.access.denied");
        (await _harness.Vault.ReadAsync(stored.Id, new SecretOwner("Identity", Guid.CreateVersion7()), "x", Ct)).Error.Code.Value.ShouldBe("secrets.access.denied");

        // Verification reads the vault as Identity/user, for the purpose "totp"; the secret never shows in the enrolment record.
        await _harness.Mfa.ConfirmTotpAsync(user.Id, CodeNow(user.Id), Ct);
        _harness.Vault.Reads.ShouldContain(r => r.Purpose == "totp" && r.Caller == new SecretOwner("Identity", user.Id.Value));

        // Without a vault, enrolment is refused rather than falling back to the database.
        using var noVault = new MfaService(_harness.DbFactory, _harness.UserDirectory, _harness.FastHasher, _harness.Clock, _harness.WebAuthn, _harness.Auditor, vault: null);
        var other = await UserAsync("novault.user");
        (await noVault.EnrollTotpAsync(other.Id, Ct)).Error.Code.Value.ShouldBe("identity.mfa.vault_unavailable");
        db.Totp.Count(t => t.UserId == other.Id).ShouldBe(0);

        // A reset deletes the vault secret.
        var admin = await UserAsync("vault.admin", administrator: true);
        (await _harness.Mfa.ResetAsync(user.Id, new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.Vault.Count.ShouldBe(0);
    }

    [Fact]
    public async Task IDN_U_055_Recovery_codes_are_hashed_and_single_use()
    {
        var user = await UserAsync("recovery.user");
        var codes = await EnrolTotpAsync(user.Id);

        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var rows = db.RecoveryCodes.Where(r => r.UserId == user.Id).ToList();
        rows.Count.ShouldBe(10);
        rows.ShouldAllBe(r => r.Hash.StartsWith("$argon2id$") && r.UsedAt == null);
        foreach (var code in codes)
        {
            rows.ShouldAllBe(r => !r.Hash.Contains(code) && !r.Hash.Contains(code.Replace("-", "")));
        }

        // One use each; separators and case do not matter; a second use is refused.
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, codes[0].ToLowerInvariant().Replace("-", " "), null, Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, codes[0], null, Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).RecoveryCodesRemaining.ShouldBe(9);
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, "AAAAA-AAAAA", null, Ct)).IsSuccess.ShouldBeFalse();

        // Regeneration invalidates the remaining old codes.
        var fresh = (await _harness.Mfa.RegenerateRecoveryCodesAsync(user.Id, Ct)).Value.Codes;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, codes[1], null, Ct)).IsSuccess.ShouldBeFalse();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, fresh[0], null, Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).RecoveryCodesRemaining.ShouldBe(9);

        // Recovery codes need a confirmed TOTP enrolment.
        var other = await UserAsync("norecovery.user");
        (await _harness.Mfa.RegenerateRecoveryCodesAsync(other.Id, Ct)).Error.Code.Value.ShouldBe("identity.mfa.not_enrolled");
    }

    [Fact]
    public async Task IDN_U_056_WebAuthn_registration_and_assertion_with_a_software_authenticator()
    {
        var user = await UserAsync("webauthn.user");
        using var authenticator = new SoftwareAuthenticator();

        // Registration: options carry the RP id and the user; the attestation (format none) is verified and stored.
        var registration = (await _harness.Mfa.BeginWebAuthnRegistrationAsync(user.Id, Ct)).Value;
        registration.OptionsJson.ShouldContain("\"" + RpId + "\"");
        registration.ExpiresAt.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(5));
        var credential = (await _harness.Mfa.CompleteWebAuthnRegistrationAsync(user.Id, authenticator.Register(registration.OptionsJson, Origin), "YubiKey", Ct)).Value;
        credential.Name.ShouldBe("YubiKey");
        credential.Transports.ShouldBe(["internal"]);
        var status = await _harness.Mfa.GetStatusAsync(user.Id, Ct);
        status.WebAuthnCredentials.ShouldHaveSingleItem();
        status.Methods.ShouldBe(["webauthn"]);

        // The challenge is single use: the same attestation again is refused.
        (await _harness.Mfa.CompleteWebAuthnRegistrationAsync(user.Id, authenticator.Register(registration.OptionsJson, Origin), "Again", Ct)).Error.Code.Value.ShouldBe("identity.webauthn.invalid");

        // Assertion: good; then wrong origin, wrong RP id, wrong challenge, another user's ceremony, expired ceremony.
        var handle = user.Id.Value.ToByteArray();
        var assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle), Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle), Ct)).IsSuccess.ShouldBeFalse("challenge is single use");

        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, "https://evil.example.org", handle), Ct)).IsSuccess.ShouldBeFalse("origin");
        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle, rpIdOverride: "evil.example.org"), Ct)).IsSuccess.ShouldBeFalse("rp id");
        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle, challengeOverride: new byte[16]), Ct)).IsSuccess.ShouldBeFalse("challenge");

        var other = await UserAsync("webauthn.other");
        (await _harness.Mfa.BeginAssertionAsync(other.Id, Ct)).Error.Code.Value.ShouldBe("identity.mfa.not_enrolled");
        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(other.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle), Ct)).IsSuccess.ShouldBeFalse("another user's ceremony");

        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        _harness.Clock.Advance(TimeSpan.FromMinutes(5));
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(assertion.OptionsJson, Origin, handle), Ct)).IsSuccess.ShouldBeFalse("expired");

        // A malformed response is refused without an exception; a signature by another key is refused.
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, "{not json", Ct)).IsSuccess.ShouldBeFalse();
        using var impostor = new SoftwareAuthenticator();
        assertion = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, impostor.Assert(assertion.OptionsJson, Origin, handle), Ct)).IsSuccess.ShouldBeFalse("unknown credential");

        // Not configured: no ceremony at all.
        using var disabled = new TestIdentityHarness();
        var u2 = (await disabled.UserDirectory.CreateAsync(new NewUser("nowebauthn", "No WebAuthn"), Password, _systemCtx, Ct)).Value;
        (await disabled.Mfa.BeginWebAuthnRegistrationAsync(u2, Ct)).Error.Code.Value.ShouldBe("identity.webauthn.not_configured");
    }

    [Fact]
    public async Task IDN_U_057_WebAuthn_sign_counter_regression_is_rejected()
    {
        var user = await UserAsync("counter.user");
        using var authenticator = new SoftwareAuthenticator { Counter = 10 };
        var handle = user.Id.Value.ToByteArray();
        var registration = (await _harness.Mfa.BeginWebAuthnRegistrationAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.CompleteWebAuthnRegistrationAsync(user.Id, authenticator.Register(registration.OptionsJson, Origin), "Key", Ct)).IsSuccess.ShouldBeTrue();

        async Task<bool> AssertWithAsync(uint counter)
        {
            var ceremony = (await _harness.Mfa.BeginAssertionAsync(user.Id, Ct)).Value;
            return (await _harness.Mfa.VerifyAsync(user.Id, MfaMethod.WebAuthn, null, authenticator.Assert(ceremony.OptionsJson, Origin, handle, counter), Ct)).IsSuccess;
        }

        (await AssertWithAsync(11)).ShouldBeTrue();
        (await AssertWithAsync(11)).ShouldBeFalse("same counter");
        (await AssertWithAsync(5)).ShouldBeFalse("lower counter: cloned authenticator");
        (await AssertWithAsync(12)).ShouldBeTrue();

        await using var db = await _harness.DbFactory.CreateDbContextAsync(Ct);
        var row = db.WebAuthnCredentials.Single(c => c.UserId == user.Id);
        row.SignCount.ShouldBe(12);
        row.LastUsedAt.ShouldBe(_harness.Clock.GetUtcNow());
    }

    [Fact]
    public async Task IDN_U_058_Mfa_token_is_single_use_bound_to_the_client_and_lives_five_minutes()
    {
        var user = await UserAsync("mfa.signin", administrator: true);
        await EnrolTotpAsync(user.Id);
        var auth = _harness.CreateAuthService();

        var outcome = (await auth.SignInLocalAsync(new SignInRequest("mfa.signin", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        outcome.Methods.ShouldBe(["totp", "recovery"]);
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldBeEmpty("no session before the second factor");

        // Another client cannot use the token, even with the right code.
        var otherClient = new ClientContext(ClientKind.Desktop, "other-instance");
        (await auth.VerifyMfaAsync(new MfaVerification(outcome.MfaToken, MfaMethod.Totp, otherClient, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
        (await auth.VerifyMfaAsync(new MfaVerification(outcome.MfaToken, MfaMethod.Totp, _desktop with { Kind = ClientKind.Web }, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");

        // The right client completes the sign-in once.
        var tokens = (await auth.VerifyMfaAsync(new MfaVerification(outcome.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).Value;
        tokens.AccessToken.ShouldNotBeNullOrEmpty();
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldHaveSingleItem();
        _harness.Clock.Advance(TimeSpan.FromSeconds(30));
        (await auth.VerifyMfaAsync(new MfaVerification(outcome.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
        _harness.Audit.Of("identity.signedIn").ShouldHaveSingleItem().Details["method"].ShouldBe("mfa:totp");

        // Expiry: five minutes.
        var second = (await auth.SignInLocalAsync(new SignInRequest("mfa.signin", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        _harness.Clock.Advance(TimeSpan.FromMinutes(5));
        (await auth.VerifyMfaAsync(new MfaVerification(second.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");

        // A new sign-in replaces the previous token; the token is random (43 base64url characters of 32 bytes).
        var third = (await auth.SignInLocalAsync(new SignInRequest("mfa.signin", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        var fourth = (await auth.SignInLocalAsync(new SignInRequest("mfa.signin", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        fourth.MfaToken.Length.ShouldBe(43);
        fourth.MfaToken.ShouldNotBe(third.MfaToken);
        (await auth.VerifyMfaAsync(new MfaVerification(third.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).IsSuccess.ShouldBeFalse();
        (await auth.VerifyMfaAsync(new MfaVerification(fourth.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task IDN_U_059_Five_wrong_codes_invalidate_the_token_and_count_as_sign_in_failures()
    {
        var user = await UserAsync("mfa.fail", administrator: true);
        await EnrolTotpAsync(user.Id);
        var auth = _harness.CreateAuthService();
        var pending = (await auth.SignInLocalAsync(new SignInRequest("mfa.fail", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();

        for (var i = 0; i < 4; i++)
        {
            (await auth.VerifyMfaAsync(new MfaVerification(pending.MfaToken, MfaMethod.Totp, _desktop, "000000"), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
        }

        (await _harness.LockoutService.GetStatusAsync(user.Id, Ct))!.RecentFailures.ShouldBe(4);
        (await _harness.LockoutService.CheckLockoutAsync(user.Id, Ct)).IsLocked.ShouldBeFalse();

        // Fifth wrong code: the token dies (a correct code no longer works) and the account is locked (5 failures, §6.4).
        (await auth.VerifyMfaAsync(new MfaVerification(pending.MfaToken, MfaMethod.Totp, _desktop, "000000"), Ct)).IsSuccess.ShouldBeFalse();
        (await auth.VerifyMfaAsync(new MfaVerification(pending.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");
        var (locked, until) = await _harness.LockoutService.CheckLockoutAsync(user.Id, Ct);
        locked.ShouldBeTrue();
        until.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(15));
        var lockedSignIn = await auth.SignInLocalAsync(new SignInRequest("mfa.fail", Password, _desktop), Ct);
        lockedSignIn.Error.Code.Value.ShouldBe("identity.signin.locked");
        _harness.Audit.Of("identity.mfaVerifyFailed").Count().ShouldBe(5);
        _harness.Events.Of("identity.accountLocked").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task IDN_U_060_Step_up_proof_is_bound_to_the_session_and_lives_five_minutes()
    {
        var user = await UserAsync("stepup.user");
        var auth = _harness.CreateAuthService();
        var tokens = (await auth.SignInLocalAsync(new SignInRequest("stepup.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;
        var otherTokens = (await auth.SignInLocalAsync(new SignInRequest("stepup.user", Password, _desktop with { ClientInstanceId = "inst-2" }), Ct)).Value.ShouldBeOfType<SignInOutcome.Signed>().Tokens;

        // Exactly one factor; a wrong password counts for the lockout.
        (await auth.StepUpAsync(tokens.Session, new StepUpRequest(), Ct)).Error.Code.Value.ShouldBe("identity.stepup.invalid");
        (await auth.StepUpAsync(tokens.Session, new StepUpRequest(Password, Totp: "123456"), Ct)).Error.Code.Value.ShouldBe("identity.stepup.invalid");
        (await auth.StepUpAsync(tokens.Session, new StepUpRequest("WrongPassword123!"), Ct)).Error.Code.Value.ShouldBe("identity.stepup.invalid");
        (await _harness.LockoutService.GetStatusAsync(user.Id, Ct))!.RecentFailures.ShouldBe(1);

        var proof = (await auth.StepUpAsync(tokens.Session, new StepUpRequest(Password), Ct)).Value;
        proof.ExpiresAt.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(5));
        auth.ValidateStepUp(proof.StepUpToken, tokens.Session).IsSuccess.ShouldBeTrue();
        auth.ValidateStepUp(proof.StepUpToken, otherTokens.Session).Error.Code.Value.ShouldBe("identity.stepup.required");
        auth.ValidateStepUp("not-a-token", tokens.Session).Error.Code.Value.ShouldBe("identity.stepup.required");

        // With TOTP once enrolled; the proof dies with the session and after five minutes.
        await EnrolTotpAsync(user.Id);
        var totpProof = (await auth.StepUpAsync(otherTokens.Session, new StepUpRequest(Totp: CodeNow(user.Id)), Ct)).Value;
        auth.ValidateStepUp(totpProof.StepUpToken, otherTokens.Session).IsSuccess.ShouldBeTrue();
        await auth.SignOutAsync(otherTokens.Session, Ct);
        auth.ValidateStepUp(totpProof.StepUpToken, otherTokens.Session).IsSuccess.ShouldBeFalse();
        (await auth.StepUpAsync(otherTokens.Session, new StepUpRequest(Password), Ct)).Error.Code.Value.ShouldBe("identity.stepup.invalid");

        _harness.Clock.Advance(TimeSpan.FromMinutes(5));
        auth.ValidateStepUp(proof.StepUpToken, tokens.Session).IsSuccess.ShouldBeFalse();
        _harness.Audit.Of("identity.stepUp").Count(r => r.Outcome == IdentityAuditOutcome.Success).ShouldBe(2);
    }

    [Fact]
    public async Task IDN_U_061_Administrator_mfa_reset_is_audited_and_restricted()
    {
        var admin = await UserAsync("reset.admin", administrator: true);
        var other = await UserAsync("reset.other");
        var user = await UserAsync("reset.user");
        await EnrolTotpAsync(user.Id);
        using var authenticator = new SoftwareAuthenticator();
        var registration = (await _harness.Mfa.BeginWebAuthnRegistrationAsync(user.Id, Ct)).Value;
        (await _harness.Mfa.CompleteWebAuthnRegistrationAsync(user.Id, authenticator.Register(registration.OptionsJson, Origin), "Key", Ct)).IsSuccess.ShouldBeTrue();

        // A non-administrator cannot reset; the refusal is audited as denied.
        (await _harness.Mfa.ResetAsync(user.Id, new ChangeContext(other.Id, "api"), Ct)).Error.Code.Value.ShouldBe("identity.user.admin_required");
        _harness.Audit.Of("identity.mfaReset").ShouldHaveSingleItem().Outcome.ShouldBe(IdentityAuditOutcome.Denied);
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).HasMethod.ShouldBeTrue();

        // The administrator resets everything: TOTP (vault secret), recovery codes, WebAuthn credentials.
        (await _harness.Mfa.ResetAsync(user.Id, new ChangeContext(admin.Id, "api", "lost phone"), Ct)).IsSuccess.ShouldBeTrue();
        var status = await _harness.Mfa.GetStatusAsync(user.Id, Ct);
        status.TotpEnrolled.ShouldBeFalse();
        status.WebAuthnCredentials.ShouldBeEmpty();
        status.RecoveryCodesRemaining.ShouldBe(0);
        _harness.Vault.Count.ShouldBe(0);
        var record = _harness.Audit.Of("identity.mfaReset").Single(r => r.Outcome == IdentityAuditOutcome.Success);
        record.Actor.ShouldBe(admin.Id);
        record.Subject.ShouldBe(user.Id);
        record.Severity.ShouldBe(IdentityAuditSeverity.Sensitive);
        record.Reason.ShouldBe("lost phone");

        // After the reset the user enrols again; an unknown user is not found.
        (await _harness.Mfa.EnrollTotpAsync(user.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await _harness.Mfa.ResetAsync(UserId.New(), new ChangeContext(admin.Id, "api"), Ct)).Error.Code.Value.ShouldBe("identity.user.not_found");
    }

    [Fact]
    public void IDN_U_130_Totp_matches_the_RFC_6238_test_vectors()
    {
        // RFC 6238 appendix B, SHA-1, secret "12345678901234567890", 8 digits; the 6-digit codes are their last six digits.
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var vectors = new (long Time, string Code)[]
        {
            (59, "94287082"), (1111111109, "07081804"), (1111111111, "14050471"), (1234567890, "89005924"), (2000000000, "69279037"), (20000000000, "65353130"),
        };
        foreach (var (time, code) in vectors)
        {
            var step = Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(time));
            Totp.Compute(secret, step, 8).ShouldBe(code);
            Totp.Compute(secret, step).ShouldBe(code[2..]);
            Totp.Verify(secret, code[2..], DateTimeOffset.FromUnixTimeSeconds(time), lastUsedStep: -1).ShouldBe(step);
            Totp.Verify(secret, code[2..], DateTimeOffset.FromUnixTimeSeconds(time), lastUsedStep: step).ShouldBeNull();
        }

        // Base32 of RFC 4648 §10.
        Totp.ToBase32("foobar"u8).ShouldBe("MZXW6YTBOI");
        Totp.ToBase32("f"u8).ShouldBe("MY");
    }

    [Fact]
    public async Task IDN_U_131_Partial_sign_in_cannot_refresh_or_skip_the_second_factor()
    {
        var user = await UserAsync("skip.user", administrator: true);
        await EnrolTotpAsync(user.Id);
        var auth = _harness.CreateAuthService();
        var pending = (await auth.SignInLocalAsync(new SignInRequest("skip.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();

        // The mfaToken is neither a refresh token, nor an access token, nor a change token, nor a step-up proof.
        (await auth.RefreshAsync(pending.MfaToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.refresh.invalid");
        (await _harness.TokenService.ValidateAccessTokenAsync(pending.MfaToken)).Error.Code.Value.ShouldBe("identity.token.invalid");
        (await auth.CompletePasswordChangeAsync(pending.MfaToken, "AnotherPassword123!", Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");
        (await auth.BeginMfaEnrolmentAsync(pending.MfaToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldBeEmpty();

        // A forced password change does not skip MFA: the outcome after the change still asks for the factor.
        await _harness.UserDirectory.SetPasswordAsync(user.Id, "ResetPassword123!", mustChange: true, _systemCtx, Ct);
        var change = (await auth.SignInLocalAsync(new SignInRequest("skip.user", "ResetPassword123!", _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.PasswordChangeRequired>();
        var afterChange = (await auth.CompletePasswordChangeAsync(change.ChangeToken, "BrandNewPassword123!", Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldBeEmpty();
        (await auth.VerifyMfaAsync(new MfaVerification(afterChange.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).IsSuccess.ShouldBeTrue();

        // The account may change between the password and the factor: a disabled user is refused at the factor.
        var pending2 = (await auth.SignInLocalAsync(new SignInRequest("skip.user", "BrandNewPassword123!", _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();
        var admin = await UserAsync("skip.admin", administrator: true);
        var version = _harness.UserDirectory.Get(user.Id).Value.Version;
        (await _harness.UserDirectory.UpdateAsync(user.Id, new UserUpdate(Enabled: false), version, new ChangeContext(admin.Id, "api"), Ct)).IsSuccess.ShouldBeTrue();
        _harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await auth.VerifyMfaAsync(new MfaVerification(pending2.MfaToken, MfaMethod.Totp, _desktop, CodeNow(user.Id)), Ct)).Error.Code.Value.ShouldBe("identity.signin.disabled");
    }

    [Fact]
    public async Task IDN_U_132_Mfa_required_without_enrolment_allows_nothing_but_the_enrolment()
    {
        var user = await UserAsync("enrol.user", administrator: true);
        var auth = _harness.CreateAuthService();
        var required = (await auth.SignInLocalAsync(new SignInRequest("enrol.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaEnrolmentRequired>();
        _harness.SessionRegistry.ListActive(new SessionQuery(user.Id)).ShouldBeEmpty();

        // The enrolment token is bound to the client and is not an MFA token.
        (await auth.BeginMfaEnrolmentAsync(required.EnrolmentToken, _desktop with { ClientInstanceId = "x" }, Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");
        (await auth.VerifyMfaAsync(new MfaVerification(required.EnrolmentToken, MfaMethod.Totp, _desktop, "000000"), Ct)).Error.Code.Value.ShouldBe("identity.signin.mfa_invalid");

        var enrolment = (await auth.BeginMfaEnrolmentAsync(required.EnrolmentToken, _desktop, Ct)).Value;
        enrolment.SecretBase32.Length.ShouldBe(32);

        // A wrong code is a failure (lockout counter) and keeps the token; the right one confirms and signs in.
        var wrong = await auth.CompleteMfaEnrolmentAsync(required.EnrolmentToken, "000000", _desktop, Ct);
        wrong.Error.FieldErrors[0].Code.Value.ShouldBe("identity.mfa.code_invalid");
        (await _harness.LockoutService.GetStatusAsync(user.Id, Ct))!.RecentFailures.ShouldBe(1);
        var completed = (await auth.CompleteMfaEnrolmentAsync(required.EnrolmentToken, CodeNow(user.Id), _desktop, Ct)).Value;
        completed.RecoveryCodes.Codes.Count.ShouldBe(10);
        completed.Tokens.AccessToken.ShouldNotBeNullOrEmpty();
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).TotpEnrolled.ShouldBeTrue();
        (await auth.CompleteMfaEnrolmentAsync(required.EnrolmentToken, CodeNow(user.Id), _desktop, Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");

        // The next sign-in asks for the factor.
        _harness.Clock.Advance(TimeSpan.FromSeconds(31));
        (await auth.SignInLocalAsync(new SignInRequest("enrol.user", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaRequired>();

        // The enrolment token expires after ten minutes.
        var user2 = await UserAsync("enrol.late", administrator: true);
        var late = (await auth.SignInLocalAsync(new SignInRequest("enrol.late", Password, _desktop), Ct)).Value.ShouldBeOfType<SignInOutcome.MfaEnrolmentRequired>();
        _harness.Clock.Advance(TimeSpan.FromMinutes(10));
        (await auth.BeginMfaEnrolmentAsync(late.EnrolmentToken, _desktop, Ct)).Error.Code.Value.ShouldBe("identity.token.invalid");
        _ = user2;
    }

    [Fact]
    public async Task IDN_U_133_Concurrent_verifications_of_one_code_accept_it_once()
    {
        var user = await UserAsync("race.user");
        var codes = await EnrolTotpAsync(user.Id);
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var code = CodeNow(user.Id);

        var totp = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Totp, code, null, Ct), Ct)));
        totp.Count(r => r.IsSuccess).ShouldBe(1);

        var recovery = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _harness.Mfa.VerifyAsync(user.Id, MfaMethod.Recovery, codes[0], null, Ct), Ct)));
        recovery.Count(r => r.IsSuccess).ShouldBe(1);
        (await _harness.Mfa.GetStatusAsync(user.Id, Ct)).RecoveryCodesRemaining.ShouldBe(9);
    }

    private async Task<User> UserAsync(string login, bool administrator = false)
    {
        var id = (await _harness.UserDirectory.CreateAsync(new NewUser(login, "User " + login, Administrator: administrator), Password, _systemCtx, Ct)).Value;
        return _harness.UserDirectory.Get(id).Value;
    }

    private async Task<IReadOnlyList<string>> EnrolTotpAsync(UserId user)
    {
        (await _harness.Mfa.EnrollTotpAsync(user, Ct)).IsSuccess.ShouldBeTrue();
        var codes = await _harness.Mfa.ConfirmTotpAsync(user, CodeNow(user), Ct);
        codes.IsSuccess.ShouldBeTrue();
        // The confirmation consumed the current step: move to the next one so tests can present a fresh code.
        _harness.Clock.Advance(TimeSpan.FromSeconds(30));
        return codes.Value.Codes;
    }

    private byte[] SecretOf(UserId user) => _harness.Vault.All.Single(s => s.Owner.EntityId == user.Value).Value;

    private string CodeNow(UserId user) => Totp.Compute(SecretOf(user), Totp.StepOf(_harness.Clock.GetUtcNow()));
}
