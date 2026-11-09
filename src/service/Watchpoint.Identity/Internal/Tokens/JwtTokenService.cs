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

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            ["sid"] = session.Id.ToString(),
            ["ck"] = session.ClientKind.ToString().ToLowerInvariant(),
            ["lang"] = user.Language,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
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
            return IdentityErrors.TokenInvalid("Token is empty.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var validationKeys = _keyRing.GetValidationKeys().Select(k => (SecurityKey)k.SecurityKey).ToList();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidateIssuer = true,
            ValidAudience = _options.Audience,
            ValidateAudience = true,
            IssuerSigningKeys = validationKeys,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, securityToken, validationParameters) =>
            {
                if (notBefore.HasValue && now < notBefore.Value - validationParameters.ClockSkew)
                {
                    return false;
                }
                if (expires.HasValue && now > expires.Value + validationParameters.ClockSkew)
                {
                    return false;
                }
                return true;
            },
        };

        TokenValidationResult validationResult;
        try
        {
            validationResult = await _handler.ValidateTokenAsync(jwt, parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return IdentityErrors.TokenInvalid(ex.Message);
        }

        if (!validationResult.IsValid)
        {
            if (validationResult.Exception is SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException ||
                (validationResult.Exception?.Message.Contains("expired", StringComparison.OrdinalIgnoreCase) == true))
            {
                return IdentityErrors.TokenExpired();
            }
            return IdentityErrors.TokenInvalid(validationResult.Exception?.Message ?? "Validation failed.");
        }

        var jsonToken = (JsonWebToken)validationResult.SecurityToken;
        if (!UserId.TryParse(jsonToken.Subject, out var userId))
        {
            return IdentityErrors.TokenInvalid("Invalid subject claim.");
        }

        string? sidClaim = null;
        if (jsonToken.TryGetClaim("sid", out var sidClaimObj))
        {
            sidClaim = sidClaimObj.Value;
        }

        if (string.IsNullOrEmpty(sidClaim) || !SessionId.TryParse(sidClaim, out var sessionId))
        {
            return IdentityErrors.TokenInvalid("Invalid or missing session id claim.");
        }

        // Check if session is revoked
        if (_sessionRegistry.IsRevoked(sessionId))
        {
            return IdentityErrors.TokenRevoked();
        }

        var ckClaim = "desktop";
        if (jsonToken.TryGetClaim("ck", out var ckClaimObj))
        {
            ckClaim = ckClaimObj.Value;
        }

        if (!Enum.TryParse<ClientKind>(ckClaim, ignoreCase: true, out var clientKind))
        {
            clientKind = ClientKind.Desktop;
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
