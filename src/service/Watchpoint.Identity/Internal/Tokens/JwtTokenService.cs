using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal.Tokens;

/// <summary>Token service implementing ES256 JWT access token issuance and validation (spec IDN §6.2).</summary>
internal sealed class JwtTokenService : ITokenService
{
    private readonly SigningKeyRing _keyRing;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly TimeProvider _clock;
    private readonly JwtTokenOptions _options;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenService(
        SigningKeyRing keyRing,
        ISessionRegistry sessionRegistry,
        TimeProvider clock,
        JwtTokenOptions? options = null)
    {
        _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new JwtTokenOptions(TimeSpan.FromMinutes(10));
    }

    public string Issuer => $"urn:watchpoint:site:{_options.SiteId}";

    public (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Session session, User user)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(user);

        var key = _keyRing.GetCurrentSigningKey();
        var now = _clock.GetUtcNow();
        var expiresAt = now + _options.AccessTokenLifetime;
        if (session.ExpiresAt > now && session.ExpiresAt < expiresAt)
        {
            // An access token never outlives its session (spec IDN §6.2).
            expiresAt = session.ExpiresAt;
        }

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            ["sid"] = session.Id.ToString(),
            ["ck"] = session.ClientKind.ToString().ToLowerInvariant(),
            ["lang"] = user.Language,
            [JwtRegisteredClaimNames.Jti] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
        };

        if (user.Administrator)
        {
            claims["adm"] = true;
        }

        if (!string.IsNullOrEmpty(session.WorkstationId))
        {
            claims["wks"] = session.WorkstationId;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = _options.Audience,
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.EcdsaSha256),
        };

        var token = _handler.CreateToken(descriptor);
        return (token, expiresAt);
    }

    public async Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(string jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return IdentityErrors.TokenInvalid();
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var expired = false;

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = _options.Audience,
            ValidateAudience = true,

            // Algorithm pinned: no "none", no HMAC with the public key, nothing but ES256 (spec IDN §6.2).
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            TryAllIssuerSigningKeys = false,

            // Only the ring key named by the kid header validates; a token without kid has no key.
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                _keyRing.FindValidationKey(kid) is { } key ? [key.SecurityKey] : [],
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidateLifetime = true,
            LifetimeValidator = (notBefore, expires, _, validationParameters) =>
            {
                // The handler's own check uses the system clock; this one uses the TimeProvider.
                if (!expires.HasValue)
                {
                    return false;
                }

                if (notBefore.HasValue && now < notBefore.Value - validationParameters.ClockSkew)
                {
                    return false;
                }

                if (now > expires.Value + validationParameters.ClockSkew)
                {
                    expired = true;
                    return false;
                }

                return true;
            },
        };

        // The reason of a refusal is never returned: it could echo token content (spec IDN §14).
        TokenValidationResult validationResult;
        try
        {
            validationResult = await _handler.ValidateTokenAsync(jwt, parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return IdentityErrors.TokenInvalid();
        }

        if (!validationResult.IsValid)
        {
            return expired ? IdentityErrors.TokenExpired() : IdentityErrors.TokenInvalid();
        }

        var jsonToken = (JsonWebToken)validationResult.SecurityToken;
        if (!UserId.TryParse(jsonToken.Subject, out var userId))
        {
            return IdentityErrors.TokenInvalid();
        }

        string? sidClaim = null;
        if (jsonToken.TryGetClaim("sid", out var sidClaimObj))
        {
            sidClaim = sidClaimObj.Value;
        }

        if (string.IsNullOrEmpty(sidClaim) || !SessionId.TryParse(sidClaim, out var sessionId))
        {
            return IdentityErrors.TokenInvalid();
        }

        // Check if session is revoked
        if (_sessionRegistry.IsRevoked(sessionId))
        {
            return IdentityErrors.TokenRevoked();
        }

        // No default client kind: a token without a known one is refused.
        if (!jsonToken.TryGetClaim("ck", out var ckClaimObj) ||
            !Enum.TryParse<ClientKind>(ckClaimObj.Value, ignoreCase: true, out var clientKind) ||
            !Enum.IsDefined(clientKind))
        {
            return IdentityErrors.TokenInvalid();
        }

        string? wks = null;
        if (jsonToken.TryGetClaim("wks", out var wksObj))
        {
            wks = wksObj.Value;
        }

        var adm = false;
        if (jsonToken.TryGetClaim("adm", out var admObj))
        {
            adm = admObj.Value is "true" or "True";
        }

        var lang = "en";
        if (jsonToken.TryGetClaim("lang", out var langObj))
        {
            lang = langObj.Value;
        }

        var jti = jsonToken.Id ?? string.Empty;

        return new AccessTokenClaims(
            userId,
            sessionId,
            clientKind,
            wks,
            adm,
            lang,
            jsonToken.Issuer,
            _options.Audience,
            jsonToken.IssuedAt,
            jsonToken.ValidTo,
            jti);
    }
}
