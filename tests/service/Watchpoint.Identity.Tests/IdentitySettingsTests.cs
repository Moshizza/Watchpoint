using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Tests;

/// <summary>T1.18s: settings binding of spec IDN §7.1 and the per-site token issuer (§6.2).</summary>
[Trait("layer", "U")]
[Trait("spec", "IDN")]
public sealed class IdentitySettingsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SiteId _site = SiteId.From(Guid.CreateVersion7());
    private readonly TestIdentityHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IDN_U_125_Defaults_of_the_settings_table_apply_when_no_key_is_set()
    {
        var bound = IdentitySettings.Bind(Configuration([]), _site);
        bound.IsSuccess.ShouldBeTrue(bound.IsSuccess ? "" : IdentitySettings.Describe(bound.Error));
        var s = bound.Value;

        s.ShouldBe(IdentitySettings.Defaults(_site));
        s.Jwt.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        s.Jwt.SiteId.ShouldBe(_site.ToString());
        s.Jwt.Audience.ShouldBe("watchpoint");
        s.RefreshTokens.IdleLifetime.ShouldBe(TimeSpan.FromHours(12));
        s.RefreshTokens.WebMaxSessionLength.ShouldBe(TimeSpan.FromHours(12));
        s.RefreshTokens.DesktopMaxSessionLength.ShouldBe(TimeSpan.FromDays(7));
        s.RefreshTokens.MobileMaxSessionLength.ShouldBe(TimeSpan.FromDays(30));
        s.RateLimiter.RatePerIp.ShouldBe(20);
        s.RateLimiter.RatePerLogin.ShouldBe(10);
        s.RateLimiter.Window.ShouldBe(TimeSpan.FromMinutes(1));
        s.Lockout.Threshold.ShouldBe(5);
        s.Lockout.Window.ShouldBe(TimeSpan.FromMinutes(15));
        s.Lockout.BaseDuration.ShouldBe(TimeSpan.FromMinutes(15));
        s.Lockout.MaxDuration.ShouldBe(TimeSpan.FromDays(1));
        s.Password.MinLength.ShouldBe(12);
        s.Password.HistoryCount.ShouldBe(5);
        s.Password.ExpiryDays.ShouldBe(0);
        s.BootstrapAdmin.Login.ShouldBe("admin");
        s.BootstrapAdmin.PasswordEnv.ShouldBe("WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD");
        s.Mfa.RequireFor.ShouldBe(MfaRequirement.Administrators);
        s.WebAuthn.IsEnabled.ShouldBeFalse();
        s.ClientBinding.RequireEnrolledWorkstationForConfiguration.ShouldBeFalse();

        // An unrelated section is not the Identity section.
        IdentitySettings.Bind(Configuration([("Other:AccessTokenLifetime", "PT1S")]), _site).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void IDN_U_126_Every_key_of_the_settings_table_is_bound()
    {
        var bound = IdentitySettings.Bind(Configuration(
        [
            ("Identity:AccessTokenLifetime", "PT5M"),
            ("Identity:RefreshIdleLifetime", "PT2H"),
            ("Identity:WebMaxSessionLength", "PT8H"),
            ("Identity:DesktopMaxSessionLength", "P3D"),
            ("Identity:MobileMaxSessionLength", "P14D"),
            ("Identity:SignInRatePerIp", "50"),
            ("Identity:SignInRatePerLogin", "7"),
            ("Identity:Lockout:Threshold", "3"),
            ("Identity:Lockout:Window", "PT10M"),
            ("Identity:Lockout:BaseDuration", "PT20M"),
            ("Identity:Lockout:MaxDuration", "PT12H"),
            ("Identity:Password:MinLength", "14"),
            ("Identity:Password:History", "8"),
            ("Identity:Password:ExpiryDays", "90"),
            ("Identity:BootstrapAdmin:Login", "root.admin"),
            ("Identity:BootstrapAdmin:PasswordEnv", "WP_ADMIN_PW"),
            ("Identity:Mfa:RequireFor", "everyone"),
            ("Identity:WebAuthn:RpId", "watchpoint.example.com"),
            ("Identity:WebAuthn:Origins:0", "https://watchpoint.example.com"),
            ("Identity:WebAuthn:Origins:1", "https://ops.watchpoint.example.com:8443"),
            ("Identity:RequireEnrolledWorkstationForConfiguration", "true"),
        ]), _site);
        bound.IsSuccess.ShouldBeTrue(bound.IsSuccess ? "" : IdentitySettings.Describe(bound.Error));
        var s = bound.Value;

        s.Jwt.ShouldBe(new JwtTokenOptions(TimeSpan.FromMinutes(5), _site.ToString()));
        s.RefreshTokens.ShouldBe(new RefreshTokenOptions(TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromDays(3), TimeSpan.FromDays(14)));
        s.RateLimiter.ShouldBe(new SignInRateLimiterOptions(50, 7));
        s.Lockout.ShouldBe(new LockoutOptions(3, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromHours(12)));
        s.Password.ShouldBe(new PasswordPolicyOptions(14, 8, 90));
        s.BootstrapAdmin.ShouldBe(new BootstrapAdminOptions("root.admin", "WP_ADMIN_PW"));
        s.Mfa.RequireFor.ShouldBe(MfaRequirement.Everyone);
        s.WebAuthn.RpId.ShouldBe("watchpoint.example.com");
        s.WebAuthn.Origins.ShouldBe(["https://watchpoint.example.com", "https://ops.watchpoint.example.com:8443"]);
        s.ClientBinding.RequireEnrolledWorkstationForConfiguration.ShouldBeTrue();
    }

    [Fact]
    public void IDN_U_127_Invalid_unknown_or_out_of_range_settings_are_all_reported_and_none_applied()
    {
        var bound = IdentitySettings.Bind(Configuration(
        [
            ("Identity:AccessTokenLifetime", "PT1S"),                 // below PT1M
            ("Identity:RefreshIdleLifetime", "12 hours"),             // not ISO 8601
            ("Identity:SignInRatePerIp", "0"),                        // below 1
            ("Identity:SignInRatePerLogin", "ten"),                   // not an integer
            ("Identity:Lockout:Treshold", "1"),                       // misspelt key: must not be ignored
            ("Identity:Lockout:BaseDuration", "PT2H"),
            ("Identity:Lockout:MaxDuration", "PT1H"),                 // below BaseDuration
            ("Identity:Password:MinLength", "4"),                     // below 8
            ("Identity:BootstrapAdmin:Login", "a"),                   // U1: 2–64
            ("Identity:BootstrapAdmin:PasswordEnv", "1PW"),           // not a variable name
            ("Identity:Mfa:RequireFor", "sometimes"),                 // unknown value
            ("Identity:WebAuthn:Origins:0", "https://a.example.com"), // origins without RpId
            ("Identity:RequireEnrolledWorkstationForConfiguration", "yes"),
        ]), _site);

        bound.IsSuccess.ShouldBeFalse();
        bound.Error.Code.Value.ShouldBe("identity.settings.invalid");
        var byPath = bound.Error.FieldErrors.Select(f => (f.Path, f.Code.Value)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        byPath.ShouldBe(new List<(string, string)>
        {
            ("/Identity/AccessTokenLifetime", "common.value.out_of_range"),
            ("/Identity/RefreshIdleLifetime", "common.value.invalid_format"),
            ("/Identity/SignInRatePerIp", "common.value.out_of_range"),
            ("/Identity/SignInRatePerLogin", "common.value.invalid_format"),
            ("/Identity/Lockout/Treshold", "identity.settings.unknown_key"),
            ("/Identity/Lockout/MaxDuration", "common.value.out_of_range"),
            ("/Identity/Password/MinLength", "common.value.out_of_range"),
            ("/Identity/BootstrapAdmin/Login", "common.value.too_short"),
            ("/Identity/BootstrapAdmin/PasswordEnv", "common.value.invalid_format"),
            ("/Identity/Mfa/RequireFor", "common.value.not_allowed"),
            ("/Identity/WebAuthn/Origins", "common.value.not_allowed"),
            ("/Identity/RequireEnrolledWorkstationForConfiguration", "common.value.invalid_format"),
        }.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList());

        // The log line names keys and codes only.
        var described = IdentitySettings.Describe(bound.Error);
        described.ShouldContain("Identity:Lockout:Treshold: identity.settings.unknown_key");
        described.ShouldContain("Identity:AccessTokenLifetime: common.value.out_of_range");

        // WebAuthn: RP id without origins, origin on another host, non-https origin, origin with a path, IP address as RP id.
        var webAuthn = IdentitySettings.Bind(Configuration(
        [
            ("Identity:WebAuthn:RpId", "watchpoint.example.com"),
            ("Identity:WebAuthn:Origins:0", "https://evil.example.org"),
            ("Identity:WebAuthn:Origins:1", "http://watchpoint.example.com"),
            ("Identity:WebAuthn:Origins:2", "https://watchpoint.example.com/path"),
            ("Identity:WebAuthn:Origins:3", "https://login.watchpoint.example.com"),
        ]), _site);
        webAuthn.IsSuccess.ShouldBeFalse();
        webAuthn.Error.FieldErrors.Select(f => f.Path).ShouldBe(["/Identity/WebAuthn/Origins/0", "/Identity/WebAuthn/Origins/1", "/Identity/WebAuthn/Origins/2"]);
        IdentitySettings.Bind(Configuration([("Identity:WebAuthn:RpId", "watchpoint.example.com")]), _site).Error.FieldErrors[0].Path.ShouldBe("/Identity/WebAuthn/Origins");
        IdentitySettings.Bind(Configuration([("Identity:WebAuthn:RpId", "10.0.0.1"), ("Identity:WebAuthn:Origins:0", "https://10.0.0.1")]), _site)
            .Error.FieldErrors.ShouldContain(f => f.Path == "/Identity/WebAuthn/RpId");
    }

    [Fact]
    public async Task IDN_U_128_Token_issuer_is_the_site_id_and_another_site_refuses_the_token()
    {
        var siteA = SiteId.From(Guid.CreateVersion7());
        var siteB = SiteId.From(Guid.CreateVersion7());
        var nodeA = new JwtTokenService(_harness.KeyRing, _harness.SessionRegistry, _harness.Clock, IdentitySettings.Defaults(siteA).Jwt);
        var nodeB = new JwtTokenService(_harness.KeyRing, _harness.SessionRegistry, _harness.Clock, IdentitySettings.Defaults(siteB).Jwt);
        nodeA.Issuer.ShouldBe("urn:watchpoint:site:" + siteA);
        siteA.ToString().ShouldBe(siteA.Value.ToString("D").ToLowerInvariant());

        var user = new User(
            UserId.New(), "issuer.user", "Issuer User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);
        var now = _harness.Clock.GetUtcNow();
        var session = new Session(SessionId.New(), user.Id, ClientKind.Desktop, "inst1", null, null, null, null, now, now, now.AddDays(1), null, null);

        var (jwt, _) = nodeA.IssueAccessToken(session, user);
        var ownSite = await nodeA.ValidateAccessTokenAsync(jwt);
        ownSite.IsSuccess.ShouldBeTrue();
        ownSite.Value.Issuer.ShouldBe("urn:watchpoint:site:" + siteA);

        // Same signing keys (a copied key ring), another site: the issuer check alone refuses the token, without detail.
        var otherSite = await nodeB.ValidateAccessTokenAsync(jwt);
        otherSite.IsSuccess.ShouldBeFalse();
        otherSite.Error.Code.Value.ShouldBe("identity.token.invalid");
        otherSite.Error.Args.ShouldBeEmpty();
    }

    [Fact]
    public async Task IDN_U_129_Bound_settings_reach_the_services()
    {
        var settings = IdentitySettings.Bind(Configuration(
        [
            ("Identity:Lockout:Threshold", "2"),
            ("Identity:Lockout:BaseDuration", "PT3M"),
            ("Identity:AccessTokenLifetime", "PT2M"),
            ("Identity:Password:MinLength", "20"),
        ]), _site).Value;

        // Registration: the singletons the services take are the bound ones.
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_harness.Clock);
        services.AddIdentityModule(settings);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<LockoutOptions>().ShouldBe(settings.Lockout);
        provider.GetRequiredService<JwtTokenOptions>().ShouldBe(settings.Jwt);
        provider.GetRequiredService<PasswordPolicyOptions>().ShouldBe(settings.Password);
        provider.GetRequiredService<RefreshTokenOptions>().ShouldBe(settings.RefreshTokens);
        provider.GetRequiredService<SignInRateLimiterOptions>().ShouldBe(settings.RateLimiter);
        provider.GetRequiredService<BootstrapAdminOptions>().ShouldBe(settings.BootstrapAdmin);
        provider.GetRequiredService<MfaOptions>().ShouldBe(settings.Mfa);
        provider.GetRequiredService<WebAuthnOptions>().ShouldBe(settings.WebAuthn);
        provider.GetRequiredService<ClientBindingOptions>().ShouldBe(settings.ClientBinding);

        // Effect: a threshold of 2 locks at the second failure, for the configured base duration.
        using var lockout = new LockoutService(_harness.DbFactory, _harness.Clock, settings.Lockout);
        var uid = UserId.New();
        (await lockout.RecordFailureAsync(uid, Ct)).NewlyLocked.ShouldBeFalse();
        var (newlyLocked, until) = await lockout.RecordFailureAsync(uid, Ct);
        newlyLocked.ShouldBeTrue();
        until.ShouldBe(_harness.Clock.GetUtcNow().AddMinutes(3));

        // Effect: the access token lives 2 minutes.
        var tokens = new JwtTokenService(_harness.KeyRing, _harness.SessionRegistry, _harness.Clock, settings.Jwt);
        var user = new User(
            uid, "bound.user", "Bound User", null, null, "en", null,
            UserKind.Person, true, false, AuthSource.Local, null, null, null, null,
            new HashSet<ClientKind>(), [], SessionPolicy.Default, MfaPolicy.Inherit, null, false, null, null, 1);
        var now = _harness.Clock.GetUtcNow();
        var session = new Session(SessionId.New(), uid, ClientKind.Desktop, "inst1", null, null, null, null, now, now, now.AddDays(1), null, null);
        tokens.IssueAccessToken(session, user).ExpiresAt.ShouldBe(now.AddMinutes(2));
    }

    private static IConfiguration Configuration(IEnumerable<(string Key, string Value)> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}
