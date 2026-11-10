using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Claims extracted from or encoded into a validated access token (spec IDN §6.2).</summary>
/// <param name="UserId">User id claim (<c>sub</c>).</param>
/// <param name="SessionId">Session id claim (<c>sid</c>).</param>
/// <param name="ClientKind">Client kind (<c>ck</c>).</param>
/// <param name="WorkstationId">Workstation id (<c>wks</c>), if any.</param>
/// <param name="Administrator">Administrator flag (<c>adm</c>).</param>
/// <param name="Language">User language preference (<c>lang</c>).</param>
/// <param name="Issuer">Issuer claim (<c>iss</c>).</param>
/// <param name="Audience">Audience claim (<c>aud</c>).</param>
/// <param name="IssuedAt">Issued at instant (<c>iat</c>).</param>
/// <param name="ExpiresAt">Expiration instant (<c>exp</c>).</param>
/// <param name="TokenId">Token identifier (<c>jti</c>).</param>
public sealed record AccessTokenClaims(
    UserId UserId,
    SessionId SessionId,
    ClientKind ClientKind,
    string? WorkstationId,
    bool Administrator,
    string Language,
    string Issuer,
    string Audience,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string TokenId);

/// <summary>Token issuance and validation service (spec IDN §6.2).</summary>
public interface ITokenService
{
    /// <summary>Issues a signed JWT access token for the given session and user.</summary>
    /// <param name="session">The session.</param>
    /// <param name="user">The user.</param>
    /// <returns>The JWT string and expiration instant.</returns>
    (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Session session, User user);

    /// <summary>Validates a JWT access token and checks session revocation.</summary>
    /// <param name="jwt">The JWT token string.</param>
    /// <returns>Claims if valid, or an error.</returns>
    Task<Result<AccessTokenClaims>> ValidateAccessTokenAsync(string jwt);
}
