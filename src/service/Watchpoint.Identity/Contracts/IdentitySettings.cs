using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>
/// The <c>Identity:*</c> settings of spec IDN §7.1, bound from configuration and validated. The host binds them at
/// start-up and refuses to start on any problem (fail fast, HST §4), so a misspelt key or an out-of-range value can
/// never silently weaken the policy: unknown keys under <c>Identity</c> are errors too.
/// </summary>
/// <param name="Jwt">Access token lifetime, issuer (site) and audience.</param>
/// <param name="RefreshTokens">Refresh token idle lifetime and absolute session lengths per client kind.</param>
/// <param name="RateLimiter">Sign-in rate limits per IP and per login.</param>
/// <param name="Lockout">Lockout threshold, window and durations.</param>
/// <param name="Password">Password policy.</param>
/// <param name="BootstrapAdmin">Bootstrap administrator login and password variable.</param>
/// <param name="Mfa">Multi-factor policy.</param>
/// <param name="WebAuthn">WebAuthn relying party and origins.</param>
/// <param name="ClientBinding">Client kind binding options.</param>
public sealed partial record IdentitySettings(
    JwtTokenOptions Jwt,
    RefreshTokenOptions RefreshTokens,
    SignInRateLimiterOptions RateLimiter,
    LockoutOptions Lockout,
    PasswordPolicyOptions Password,
    BootstrapAdminOptions BootstrapAdmin,
    MfaOptions Mfa,
    WebAuthnOptions WebAuthn,
    ClientBindingOptions ClientBinding)
{
    /// <summary>The configuration section holding the settings.</summary>
    public const string SectionName = "Identity";

    private static readonly string[] _knownKeys =
    [
        "AccessTokenLifetime", "RefreshIdleLifetime", "WebMaxSessionLength", "DesktopMaxSessionLength", "MobileMaxSessionLength",
        "SignInRatePerIp", "SignInRatePerLogin",
        "Lockout:Threshold", "Lockout:Window", "Lockout:BaseDuration", "Lockout:MaxDuration",
        "Password:MinLength", "Password:History", "Password:ExpiryDays",
        "BootstrapAdmin:Login", "BootstrapAdmin:PasswordEnv",
        "Mfa:RequireFor",
        "WebAuthn:RpId", "WebAuthn:Origins",
        "RequireEnrolledWorkstationForConfiguration",
    ];

    /// <summary>The defaults of spec IDN §7.1 for the given site.</summary>
    /// <param name="siteId">The site whose id forms the token issuer.</param>
    /// <returns>The settings.</returns>
    public static IdentitySettings Defaults(SiteId siteId) => new(
        new JwtTokenOptions(TimeSpan.FromMinutes(10), siteId.ToString()),
        new RefreshTokenOptions(TimeSpan.FromHours(12), TimeSpan.FromHours(12), TimeSpan.FromDays(7), TimeSpan.FromDays(30)),
        new SignInRateLimiterOptions(20, 10),
        new LockoutOptions(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)),
        new PasswordPolicyOptions(12, 5, 0),
        new BootstrapAdminOptions("admin", "WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD"),
        new MfaOptions(MfaRequirement.Administrators),
        new WebAuthnOptions(),
        new ClientBindingOptions(false));

    /// <summary>
    /// Binds and validates the <c>Identity</c> section of <paramref name="configuration"/>. Durations are ISO 8601
    /// (<c>PT10M</c>, <c>P7D</c>). Every problem is reported (field errors with the configuration key as path), none
    /// of the values is applied when there is one.
    /// </summary>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="siteId">The site whose id forms the token issuer (<c>urn:watchpoint:site:&lt;siteId&gt;</c>).</param>
    /// <returns>The settings, or <c>identity.settings.invalid</c> with one field error per problem.</returns>
    public static Result<IdentitySettings> Bind(IConfiguration configuration, SiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = Defaults(siteId);
        var section = configuration.GetSection(SectionName);
        var problems = new List<FieldError>();
        var reader = new SettingsReader(section, problems);

        foreach (var key in UnknownKeys(section))
        {
            problems.Add(FieldError.Create(Path(key), ErrorCode.FromTrusted("identity.settings.unknown_key")));
        }

        var accessLifetime = reader.Duration("AccessTokenLifetime", defaults.Jwt.AccessTokenLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        var refreshIdle = reader.Duration("RefreshIdleLifetime", defaults.RefreshTokens.IdleLifetime, TimeSpan.FromMinutes(5), TimeSpan.FromDays(30));
        var webMax = reader.Duration("WebMaxSessionLength", defaults.RefreshTokens.WebMaxSessionLength, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365));
        var desktopMax = reader.Duration("DesktopMaxSessionLength", defaults.RefreshTokens.DesktopMaxSessionLength, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365));
        var mobileMax = reader.Duration("MobileMaxSessionLength", defaults.RefreshTokens.MobileMaxSessionLength, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365));
        var ratePerIp = reader.Int("SignInRatePerIp", defaults.RateLimiter.RatePerIp, 1, 100_000);
        var ratePerLogin = reader.Int("SignInRatePerLogin", defaults.RateLimiter.RatePerLogin, 1, 100_000);
        var threshold = reader.Int("Lockout:Threshold", defaults.Lockout.Threshold, 1, 100);
        var window = reader.Duration("Lockout:Window", defaults.Lockout.Window, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        var baseDuration = reader.Duration("Lockout:BaseDuration", defaults.Lockout.BaseDuration, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        var maxDuration = reader.Duration("Lockout:MaxDuration", defaults.Lockout.MaxDuration, TimeSpan.FromMinutes(1), TimeSpan.FromDays(30));
        if (maxDuration < baseDuration)
        {
            problems.Add(FieldError.Create(Path("Lockout:MaxDuration"), CommonErrorCodes.ValueOutOfRange, ("min", XmlConvert.ToString(baseDuration))));
        }

        var minLength = reader.Int("Password:MinLength", defaults.Password.MinLength, 8, 256);
        var history = reader.Int("Password:History", defaults.Password.HistoryCount, 0, 24);
        var expiryDays = reader.Int("Password:ExpiryDays", defaults.Password.ExpiryDays, 0, 3650);
        var login = reader.Text("BootstrapAdmin:Login", defaults.BootstrapAdmin.Login, LoginPattern(), 2, 64);
        var passwordEnv = reader.Text("BootstrapAdmin:PasswordEnv", defaults.BootstrapAdmin.PasswordEnv, EnvironmentVariablePattern(), 1, 128);
        var requireFor = reader.Enum("Mfa:RequireFor", defaults.Mfa.RequireFor);
        var rpId = reader.Text("WebAuthn:RpId", defaults.WebAuthn.RpId, HostNamePattern(), 0, 253);
        if (rpId.Length > 0 && System.Net.IPAddress.TryParse(rpId, out _))
        {
            // WebAuthn RP ids are registrable domain suffixes; an IP address is never one.
            problems.Add(FieldError.Create(Path("WebAuthn:RpId"), CommonErrorCodes.ValueInvalidFormat, ("expected", "host name")));
            rpId = defaults.WebAuthn.RpId;
        }

        var origins = reader.List("WebAuthn:Origins");
        ValidateOrigins(rpId, origins, problems);
        var requireEnrolled = reader.Bool("RequireEnrolledWorkstationForConfiguration", defaults.ClientBinding.RequireEnrolledWorkstationForConfiguration);

        if (problems.Count > 0)
        {
            return Error.Validation("identity.settings.invalid", problems);
        }

        return new IdentitySettings(
            new JwtTokenOptions(accessLifetime, siteId.ToString()),
            new RefreshTokenOptions(refreshIdle, webMax, desktopMax, mobileMax),
            new SignInRateLimiterOptions(ratePerIp, ratePerLogin),
            new LockoutOptions(threshold, window, baseDuration, maxDuration),
            new PasswordPolicyOptions(minLength, history, expiryDays),
            new BootstrapAdminOptions(login, passwordEnv),
            new MfaOptions(requireFor),
            new WebAuthnOptions(rpId, origins),
            new ClientBindingOptions(requireEnrolled));
    }

    /// <summary>Formats the problems of a failed <see cref="Bind"/> for the start-up log (keys and codes, never values).</summary>
    /// <param name="error">The error returned by <see cref="Bind"/>.</param>
    /// <returns>One line per problem.</returns>
    public static string Describe(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Join("; ", error.FieldErrors.Select(f =>
            f.Path.TrimStart('/').Replace('/', ':') + ": " + f.Code.Value +
            (f.Args.Count > 0 ? " (" + string.Join(", ", f.Args.Select(a => a.Key + "=" + a.Value)) + ")" : string.Empty)));
    }

    private static IEnumerable<string> UnknownKeys(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            foreach (var key in Leaves(child, child.Key))
            {
                var known = _knownKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) ||
                    (k == "WebAuthn:Origins" && key.StartsWith("WebAuthn:Origins:", StringComparison.OrdinalIgnoreCase)));
                if (!known)
                {
                    yield return key;
                }
            }
        }
    }

    private static IEnumerable<string> Leaves(IConfigurationSection section, string path)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            yield return path;
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var leaf in Leaves(child, path + ":" + child.Key))
            {
                yield return leaf;
            }
        }
    }

    private static void ValidateOrigins(string rpId, List<string> origins, List<FieldError> problems)
    {
        if (string.IsNullOrEmpty(rpId))
        {
            if (origins.Count > 0)
            {
                problems.Add(FieldError.Create(Path("WebAuthn:Origins"), CommonErrorCodes.ValueNotAllowed, ("reason", "WebAuthn:RpId is not set")));
            }

            return;
        }

        if (origins.Count == 0)
        {
            problems.Add(FieldError.Create(Path("WebAuthn:Origins"), CommonErrorCodes.ValueRequired));
            return;
        }

        for (var i = 0; i < origins.Count; i++)
        {
            var origin = origins[i];
            var path = Path("WebAuthn:Origins:" + i.ToString(CultureInfo.InvariantCulture));
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.PathAndQuery.TrimEnd('/')) || !string.IsNullOrEmpty(uri.Fragment))
            {
                problems.Add(FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat, ("expected", "https://host[:port]")));
                continue;
            }

            var host = uri.Host;
            if (!string.Equals(host, rpId, StringComparison.OrdinalIgnoreCase) && !host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(FieldError.Create(path, CommonErrorCodes.ValueNotAllowed, ("reason", "origin is not on WebAuthn:RpId")));
            }
        }
    }

    private static string Path(string key) => "/" + SectionName + "/" + key.Replace(':', '/');

    [GeneratedRegex("^[A-Za-z0-9._@-]+$")]
    private static partial Regex LoginPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariablePattern();

    // A host name (RFC 1123 labels); an IP address is not a valid WebAuthn RP id.
    [GeneratedRegex("^(?=.{1,253}$)(?!-)([A-Za-z0-9-]{1,63}(?<!-)\\.)*[A-Za-z0-9-]{1,63}(?<!-)$")]
    private static partial Regex HostNamePattern();

    private sealed class SettingsReader(IConfigurationSection section, List<FieldError> problems)
    {
        public TimeSpan Duration(string key, TimeSpan fallback, TimeSpan min, TimeSpan max)
        {
            var text = section[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            TimeSpan value;
            try
            {
                value = XmlConvert.ToTimeSpan(text.Trim());
            }
            catch (FormatException)
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueInvalidFormat, ("expected", "ISO 8601 duration, e.g. PT10M")));
                return fallback;
            }

            if (value < min || value > max)
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueOutOfRange, ("min", XmlConvert.ToString(min)), ("max", XmlConvert.ToString(max))));
                return fallback;
            }

            return value;
        }

        public int Int(string key, int fallback, int min, int max)
        {
            var text = section[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueInvalidFormat, ("expected", "integer")));
                return fallback;
            }

            if (value < min || value > max)
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueOutOfRange, ("min", min), ("max", max)));
                return fallback;
            }

            return value;
        }

        public bool Bool(string key, bool fallback)
        {
            var text = section[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (!bool.TryParse(text.Trim(), out var value))
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueInvalidFormat, ("expected", "true or false")));
                return fallback;
            }

            return value;
        }

        public string Text(string key, string fallback, Regex pattern, int minLength, int maxLength)
        {
            var text = section[key];
            if (text is null)
            {
                return fallback;
            }

            text = text.Trim();
            if (text.Length < minLength)
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueTooShort, ("min", minLength)));
                return fallback;
            }

            if (text.Length > maxLength)
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueTooLong, ("max", maxLength)));
                return fallback;
            }

            if (text.Length > 0 && !pattern.IsMatch(text))
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueInvalidFormat));
                return fallback;
            }

            return text;
        }

        public TEnum Enum<TEnum>(string key, TEnum fallback)
            where TEnum : struct, Enum
        {
            var text = section[key];
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (!System.Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var value) || !System.Enum.IsDefined(value))
            {
                problems.Add(FieldError.Create(Path(key), CommonErrorCodes.ValueNotAllowed,
                    ("allowed", string.Join("|", System.Enum.GetNames<TEnum>().Select(n => char.ToLowerInvariant(n[0]) + n[1..])))));
                return fallback;
            }

            return value;
        }

        public List<string> List(string key)
        {
            var sub = section.GetSection(key);
            if (sub.Value is { } single)
            {
                return string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()];
            }

            return sub.GetChildren().Select(c => c.Value?.Trim()).OfType<string>().Where(v => v.Length > 0).ToList();
        }
    }
}
