using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Identity.Internal;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Contracts;

/// <summary>Service collection extension methods for identity module (spec IDN §2, §3).</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity module with validated settings (spec IDN §7.1): the host binds them with
    /// <see cref="IdentitySettings.Bind"/> and refuses to start when they are invalid.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="settings">The bound settings.</param>
    /// <param name="configure">Further options (test parameters, for example).</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IdentitySettings settings, Action<IdentityOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        return services.AddIdentityModule(b =>
        {
            b.UseJwtOptions(settings.Jwt)
                .UseRefreshTokenOptions(settings.RefreshTokens)
                .UseRateLimiterOptions(settings.RateLimiter)
                .UseLockoutOptions(settings.Lockout)
                .UsePasswordPolicy(settings.Password)
                .UseBootstrapAdminOptions(settings.BootstrapAdmin)
                .UseMfaOptions(settings.Mfa)
                .UseWebAuthnOptions(settings.WebAuthn)
                .UseClientBindingOptions(settings.ClientBinding);
            configure?.Invoke(b);
        });
    }

    /// <summary>Registers identity module services, password hasher, session registry, token service and storage.</summary>
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, Action<IdentityOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new IdentityOptionsBuilder();
        configure?.Invoke(builder);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(builder.Argon2Params ?? Argon2PasswordHasher.DefaultParameters);
        services.AddSingleton<Argon2PasswordHasher>();

        services.AddSingleton(builder.PasswordPolicy ?? new PasswordPolicyOptions());
        services.AddSingleton<IPasswordPolicy, PasswordPolicy>();

        services.AddSingleton<SigningKeyRing>();
        services.AddSingleton(builder.JwtOptions ?? new JwtTokenOptions(TimeSpan.FromMinutes(10)));
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.AddSingleton(builder.RefreshTokenOptions ?? new RefreshTokenOptions(
            TimeSpan.FromHours(12), TimeSpan.FromHours(12), TimeSpan.FromDays(7), TimeSpan.FromDays(30)));
        services.AddSingleton<RefreshTokenStore>();

        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());

        services.AddSingleton(builder.LockoutOptions ?? new LockoutOptions());
        services.AddSingleton<LockoutService>();

        services.AddSingleton(builder.RateLimiterOptions ?? new SignInRateLimiterOptions());
        services.AddSingleton<SignInRateLimiter>();

        services.AddSingleton<IUserDirectory, UserDirectory>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();

        services.AddSingleton(builder.BootstrapAdminOptions ?? new BootstrapAdminOptions());
        services.AddSingleton<IBootstrapAdminService, BootstrapAdminService>();

        services.AddSingleton(builder.MfaOptions ?? new MfaOptions());
        services.AddSingleton(builder.WebAuthnOptions ?? new WebAuthnOptions());
        services.AddSingleton(builder.ClientBindingOptions ?? new ClientBindingOptions());

        // Register module storage (schema wp_identity)
        services.AddModuleStorage<IdentityDbContext>(IdentityDbContext.ModuleName, p => p.Add<IdentityPostgresDbContext>("postgres"));

        return services;
    }
}

/// <summary>Builder for configuring identity module options.</summary>
public sealed class IdentityOptionsBuilder
{
    internal Argon2Parameters? Argon2Params { get; private set; }
    internal PasswordPolicyOptions? PasswordPolicy { get; private set; }
    internal JwtTokenOptions? JwtOptions { get; private set; }
    internal RefreshTokenOptions? RefreshTokenOptions { get; private set; }
    internal LockoutOptions? LockoutOptions { get; private set; }
    internal SignInRateLimiterOptions? RateLimiterOptions { get; private set; }
    internal BootstrapAdminOptions? BootstrapAdminOptions { get; private set; }
    internal MfaOptions? MfaOptions { get; private set; }
    internal WebAuthnOptions? WebAuthnOptions { get; private set; }
    internal ClientBindingOptions? ClientBindingOptions { get; private set; }

    /// <summary>Configures Argon2id parameters.</summary>
    public IdentityOptionsBuilder UseArgon2Parameters(Argon2Parameters parameters)
    {
        Argon2Params = parameters;
        return this;
    }

    /// <summary>Configures password policy options.</summary>
    public IdentityOptionsBuilder UsePasswordPolicy(PasswordPolicyOptions options)
    {
        PasswordPolicy = options;
        return this;
    }

    /// <summary>Configures JWT token options.</summary>
    public IdentityOptionsBuilder UseJwtOptions(JwtTokenOptions options)
    {
        JwtOptions = options;
        return this;
    }

    /// <summary>Configures refresh token options.</summary>
    public IdentityOptionsBuilder UseRefreshTokenOptions(RefreshTokenOptions options)
    {
        RefreshTokenOptions = options;
        return this;
    }

    /// <summary>Configures lockout options.</summary>
    public IdentityOptionsBuilder UseLockoutOptions(LockoutOptions options)
    {
        LockoutOptions = options;
        return this;
    }

    /// <summary>Configures rate limiter options.</summary>
    public IdentityOptionsBuilder UseRateLimiterOptions(SignInRateLimiterOptions options)
    {
        RateLimiterOptions = options;
        return this;
    }

    /// <summary>Configures bootstrap admin options.</summary>
    public IdentityOptionsBuilder UseBootstrapAdminOptions(BootstrapAdminOptions options)
    {
        BootstrapAdminOptions = options;
        return this;
    }

    /// <summary>Configures the multi-factor policy.</summary>
    public IdentityOptionsBuilder UseMfaOptions(MfaOptions options)
    {
        MfaOptions = options;
        return this;
    }

    /// <summary>Configures WebAuthn.</summary>
    public IdentityOptionsBuilder UseWebAuthnOptions(WebAuthnOptions options)
    {
        WebAuthnOptions = options;
        return this;
    }

    /// <summary>Configures client kind binding.</summary>
    public IdentityOptionsBuilder UseClientBindingOptions(ClientBindingOptions options)
    {
        ClientBindingOptions = options;
        return this;
    }
}
