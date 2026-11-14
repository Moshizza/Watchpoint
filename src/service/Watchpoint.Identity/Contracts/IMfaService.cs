using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>A TOTP enrolment waiting for confirmation (spec IDN §10): the secret is shown to the user once, as a QR code.</summary>
/// <param name="SecretBase32">The 20-byte secret, base32 (RFC 4648) for the authenticator app.</param>
/// <param name="OtpAuthUri">The <c>otpauth://totp/…</c> URI (issuer Watchpoint, SHA-1, 6 digits, 30 s).</param>
public sealed record TotpEnrolment(string SecretBase32, Uri OtpAuthUri);

/// <summary>Recovery codes shown once; only their Argon2 hashes are kept (spec IDN §10).</summary>
/// <param name="Codes">Ten single-use codes, <c>XXXXX-XXXXX</c>.</param>
public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

/// <summary>A WebAuthn ceremony (registration or assertion) started for a user: options for the authenticator, single use.</summary>
/// <param name="OptionsJson">The <c>PublicKeyCredentialCreationOptions</c> or <c>PublicKeyCredentialRequestOptions</c> as JSON.</param>
/// <param name="ExpiresAt">When the challenge expires (5 min).</param>
public sealed record WebAuthnCeremony(string OptionsJson, DateTimeOffset ExpiresAt);

/// <summary>A registered WebAuthn credential (spec IDN §10, table <c>webauthn_credentials</c>); never the private key.</summary>
/// <param name="Id">The row id.</param>
/// <param name="Name">The name given by the user.</param>
/// <param name="Transports">The transports reported at registration.</param>
/// <param name="CreatedAt">Registration instant.</param>
/// <param name="LastUsedAt">Last successful assertion.</param>
public sealed record WebAuthnCredentialInfo(Guid Id, string Name, IReadOnlyList<string> Transports, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>The MFA enrolment state of a user.</summary>
/// <param name="TotpEnrolled">TOTP is confirmed.</param>
/// <param name="WebAuthnCredentials">The registered WebAuthn credentials.</param>
/// <param name="RecoveryCodesRemaining">Unused recovery codes.</param>
public sealed record MfaStatus(bool TotpEnrolled, IReadOnlyList<WebAuthnCredentialInfo> WebAuthnCredentials, int RecoveryCodesRemaining)
{
    /// <summary>Gets a value indicating whether any method can complete a sign-in.</summary>
    public bool HasMethod => TotpEnrolled || WebAuthnCredentials.Count > 0;

    /// <summary>Gets the method names offered at sign-in (<c>totp</c>, <c>webauthn</c>, <c>recovery</c>).</summary>
    public IReadOnlyList<string> Methods
    {
        get
        {
            var methods = new List<string>(3);
            if (TotpEnrolled)
            {
                methods.Add("totp");
            }

            if (WebAuthnCredentials.Count > 0)
            {
                methods.Add("webauthn");
            }

            if (RecoveryCodesRemaining > 0 && TotpEnrolled)
            {
                methods.Add("recovery");
            }

            return methods;
        }
    }
}

/// <summary>
/// Multi-factor authentication of users (spec IDN §10): policy, TOTP and recovery codes, WebAuthn credentials, reset.
/// The verification of a factor during sign-in or step-up goes through <see cref="IAuthenticationService"/>.
/// </summary>
public interface IMfaService
{
    /// <summary>
    /// Whether the user must present a second factor: the user's own policy when set, else the site policy
    /// (<c>Identity:Mfa:RequireFor</c>). The break-glass account never does (spec IDN §11).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether MFA is required.</returns>
    ValueTask<bool> IsRequiredAsync(User user, CancellationToken ct);

    /// <summary>Gets the enrolment state of a user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state.</returns>
    Task<MfaStatus> GetStatusAsync(UserId user, CancellationToken ct);

    /// <summary>
    /// Starts a TOTP enrolment: a new 20-byte secret from the CSPRNG, stored in the secrets vault (owner Identity, entity
    /// = user) and returned once. A pending (unconfirmed) enrolment is replaced; a confirmed one must be reset first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The enrolment, <c>identity.mfa.already_enrolled</c> or <c>identity.mfa.vault_unavailable</c>.</returns>
    Task<Result<TotpEnrolment>> EnrollTotpAsync(UserId user, CancellationToken ct);

    /// <summary>Confirms the pending TOTP enrolment with a valid code; generates the recovery codes.</summary>
    /// <param name="user">The user.</param>
    /// <param name="code">The code from the authenticator app.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recovery codes, <c>identity.mfa.code_invalid</c> or <c>identity.mfa.not_enrolled</c>.</returns>
    Task<Result<RecoveryCodes>> ConfirmTotpAsync(UserId user, string code, CancellationToken ct);

    /// <summary>Replaces the recovery codes: the old ones are invalidated.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new codes, or <c>identity.mfa.not_enrolled</c> when TOTP is not confirmed.</returns>
    Task<Result<RecoveryCodes>> RegenerateRecoveryCodesAsync(UserId user, CancellationToken ct);

    /// <summary>Starts a WebAuthn registration ceremony (RP id = site host name, user verification preferred).</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ceremony, or <c>identity.webauthn.not_configured</c>.</returns>
    Task<Result<WebAuthnCeremony>> BeginWebAuthnRegistrationAsync(UserId user, CancellationToken ct);

    /// <summary>Completes a registration: challenge, origin and RP id verified, credential stored.</summary>
    /// <param name="user">The user.</param>
    /// <param name="attestationResponseJson">The authenticator's attestation response as JSON.</param>
    /// <param name="name">A name for the credential (1–64 chars).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored credential, or <c>identity.webauthn.invalid</c> (no detail).</returns>
    Task<Result<WebAuthnCredentialInfo>> CompleteWebAuthnRegistrationAsync(UserId user, string attestationResponseJson, string name, CancellationToken ct);

    /// <summary>Removes a WebAuthn credential of the user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="credentialId">The row id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <c>common.not_found</c>.</returns>
    Task<Result> RemoveWebAuthnCredentialAsync(UserId user, Guid credentialId, CancellationToken ct);

    /// <summary>
    /// Administrator reset (spec IDN §10): removes the TOTP secret (vault), the recovery codes and the WebAuthn
    /// credentials of the user. Audited (<c>identity.mfaReset</c>); the actor must be an enabled administrator.
    /// </summary>
    /// <param name="user">The user whose MFA is reset.</param>
    /// <param name="ctx">The actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, <c>identity.user.not_found</c> or <c>identity.user.admin_required</c>.</returns>
    Task<Result> ResetAsync(UserId user, ChangeContext ctx, CancellationToken ct);
}
