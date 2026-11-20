# IDN — Identity (Users, Sign-In, Tokens, Sessions, MFA, Directories)

| | |
|---|---|
| Spec id | IDN |
| Projects | `src/service/Watchpoint.Identity` |
| Milestone | M1 (part 1: §4–§7), M3 (part 2: §8–§12) |
| Tasks | T1.18 (§4–§7), T1.18r (security review of §4–§7), T1.18s (settings binding, per-site issuer), T3.07 (§8–§9), T3.08 (§10–§12) |
| Design docs | [Users and rights §1, §4, §5](../../docs/modules/users-and-rights.md) |

## 1. Purpose and scope

User **accounts** and **authentication**: local passwords, directory (LDAP/AD) and OpenID
Connect sign-in, MFA (TOTP, WebAuthn), lockout, **tokens** and **sessions**, client kind binding,
break-glass access.

Not in scope: rights, user groups, templates and the rights-copy tool (RGT — IDN asks it for the
effective permissions summary through `IPermissionSummaryProvider`); audit storage (AUD — IDN emits
audit records through `IIdentityAuditSink`, which `Watchpoint.Audit` implements over `IAuditSink` in
T3.06; until then a logging sink writes them as events 2790–2792); system events (EVT implements
`ISystemEventSink`); maintenance mode policy (MNT implements `ISessionAdmission`).

## 2. Dependencies

| Uses | Used by |
|---|---|
| K, HST, STO, SEC (signing keys, TOTP secrets, LDAP/OIDC client secrets), PIP (`WP_SYS` session records), Konscious.Security.Cryptography.Argon2 (MIT), System.IdentityModel.Tokens.Jwt / Microsoft.IdentityModel.* (MIT), System.DirectoryServices.Protocols (MIT), Fido2NetLib (MIT; TOTP is forty lines of RFC 6238 in `Internal/Mfa/Totp.cs`, verified against the RFC vectors, no package) | API (authentication middleware), RGT, AUD, MNT, all clients |

## 3. Project layout

```
Watchpoint.Identity/
├── Contracts/
│   ├── User.cs, UserKind.cs, UserStatus.cs, ClientKind.cs, AuthSource.cs
│   ├── IUserDirectory.cs              (CRUD of accounts)
│   ├── IAuthenticationService.cs      (sign-in flows)
│   ├── ITokenService.cs, TokenPair.cs, AccessTokenClaims.cs
│   ├── ISessionRegistry.cs, Session.cs
│   ├── ISessionAdmission.cs           (hook: MNT, LIC seats)
│   ├── IPasswordPolicy.cs
│   ├── IMfaService.cs, ISessionManagement.cs, ILockoutManager.cs, ICurrentUserService.cs   (part 2)
│   ├── IIdentityAuditSink.cs, ISystemEventSink.cs, IPermissionSummaryProvider.cs           (hooks: AUD, EVT, RGT)
│   ├── IdentitySettings.cs            (§7.1 binding and validation)
│   └── IdentityErrors.cs
├── Internal/
│   ├── Passwords/Argon2PasswordHasher.cs, PasswordPolicy.cs, BreachedPasswords.cs (embedded list)
│   ├── Tokens/JwtTokenService.cs, SigningKeyRing.cs, RefreshTokenStore.cs
│   ├── Sessions/SessionRegistry.cs
│   ├── Lockout/LockoutService.cs, SignInRateLimiter.cs
│   ├── Directory/LdapAuthenticator.cs, LdapProvisioner.cs          (part 2)
│   ├── Oidc/OidcAuthenticator.cs, OidcStateStore.cs                 (part 2)
│   ├── Mfa/MfaService.cs, Totp.cs, RecoveryCodeGenerator.cs, WebAuthnService.cs   (part 2)
│   ├── PendingTokenStore.cs           (change, MFA, enrolment tokens and step-up proofs)
│   ├── Audit/LoggingIdentityAuditSink.cs, IdentityAuditor.cs
│   ├── Sessions/SessionManagement.cs, CurrentUserService.cs          (part 2; break-glass rules live in UserDirectory and AuthenticationService)
│   └── Storage/IdentityDbContext.cs (+ Postgres subclass, migrations)
├── IdentityModule.cs
└── IdentityLog.cs                     event ids 2700–2799
```

## 4. Users (part 1)

```csharp
public enum UserKind { Person, Template, Script, Federation, BreakGlass }
public enum UserStatus { Active, Disabled, Locked, Expired }      // Locked/Expired are computed views, see below
public enum AuthSource { Local, Directory, Oidc, None }           // None: templates and script identities
public enum ClientKind { Desktop, Web, Mobile, Api }

public sealed record User(UserId Id, string Login, string DisplayName, string? Email, string? Phone,
    string Language /* SupportedCultures */, string? TimeZone, UserKind Kind, bool Enabled, bool Administrator,
    AuthSource AuthSource, string? ExternalId /* directory DN or OIDC subject */, string? ExternalProvider,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil,
    IReadOnlySet<ClientKind> AllowedClients, IReadOnlyList<string> AllowedWorkstations /* workstation ids, empty = any */,
    SessionPolicy SessionPolicy, MfaPolicy Mfa, DateTimeOffset? PasswordChangedAt, bool MustChangePassword,
    DateTimeOffset? LastSignInAt, string? Notes, long Version);

public sealed record SessionPolicy(int MaxConcurrentSessions /* 0 = unlimited */, TimeSpan IdleLock /* client-side lock */,
    TimeSpan MaxSessionLength);
public enum MfaPolicy { Inherit, Required, NotRequired }
```

Rules:

| # | Rule |
|---|---|
| U1 | `Login`: 2–64 chars, `^[A-Za-z0-9._@-]+$`, unique **case-insensitively** |
| U2 | `Template` and `Script` users cannot sign in (sign-in → `identity.signin.invalid_credentials`, no hint) and have `AuthSource.None` |
| U3 | At least one **active administrator** of kind Person must always exist (deleting, disabling, demoting or expiring the last one → `identity.user.last_administrator`) |
| U4 | A user cannot change their own `Administrator`, `Enabled`, `Kind` |
| U5 | Only administrators can create administrators **or promote a user to administrator** (the acting user must be an enabled administrator; a change without acting user is the system: bootstrap, seed) |
| U6 | Effective status: `Disabled` if !Enabled; `Expired` if now ∉ [ValidFrom, ValidUntil]; `Locked` if a lockout is active; else `Active` |
| U7 | Deleting a user revokes all sessions; deletion is a soft delete (`deleted_at`) so history references stay resolvable; logins of deleted users can be reused after 30 days |

```csharp
public interface IUserDirectory
{
    Result<User> Get(UserId id);
    Result<User> FindByLogin(string login);                     // case-insensitive
    Page<User> Query(UserQuery query, PageRequest page);        // text, kind, enabled, authSource, admin
    Task<Result<UserId>> CreateAsync(NewUser user, string? initialPassword, ChangeContext ctx, CancellationToken ct);
    Task<Result> UpdateAsync(UserId id, UserUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct);
    Task<Result> DeleteAsync(UserId id, ChangeContext ctx, CancellationToken ct);
    Task<Result> SetPasswordAsync(UserId id, string newPassword, bool mustChange, ChangeContext ctx, CancellationToken ct); // admin reset
    Task<Result> ChangeOwnPasswordAsync(UserId id, string current, string @new, SessionId? currentSession, CancellationToken ct);
    event EventHandler<UserChangedEventArgs>? Changed;
}
```

`ChangeOwnPasswordAsync` revokes every session of the user except `currentSession` (all of them
when it is null) and treats a wrong `current` like a failed sign-in: it counts for the lockout
(§6.4) and is refused with `identity.signin.locked` while a lockout is active — otherwise a
stolen access token would allow unlimited guesses of the password.

## 5. Passwords

- Hash: **Argon2id**, memory 64 MiB, iterations 3, parallelism 2, salt 16 bytes, output 32 bytes,
  encoded as PHC string `$argon2id$v=19$m=65536,t=3,p=2$<salt>$<hash>`. Parameters stored in the
  hash so they can be raised later; on successful sign-in with old parameters → rehash.
- The salt comes from the system CSPRNG, one per hash; hashes are compared in constant time.
- **Normalisation**: a password is normalised to Unicode **NFKC** before the policy and before
  hashing or verifying, so that the same password typed on another keyboard or platform gives the
  same bytes (NIST SP 800-63B). A password longer than **256** UTF-16 code units (before or after
  normalisation) or that is not valid Unicode (lone surrogate) is refused **before any hashing**:
  `identity.password.too_long` / `identity.password.invalid_characters` when set, generic
  `invalid_credentials` at sign-in.
- Verification runs on a dedicated bounded pool (max 4 concurrent hashes; excess sign-ins wait
  up to 5 s then `common.rate_limited`) to avoid CPU/memory exhaustion. Every operation that
  hashes (sign-in, create, reset, change) returns `common.rate_limited`, never an exception.
- Unknown logins are verified against a dummy hash (constant-time behaviour). Input that cannot be
  a credential (login longer than 64 characters, password refused above) takes the same dummy-hash
  path **before the user is looked up**, for existing and unknown logins alike.
- Parameters read from a stored hash are bounded (memory ≤ 1 GiB, iterations ≤ 16, parallelism
  ≤ 16, salt ≥ 8 bytes, hash ≥ 16 bytes); a stored hash outside these bounds never verifies.

Policy (`IPasswordPolicy`, settings in §7):

| Rule | Default |
|---|---|
| Minimum length | 12 |
| Composition | ≥ 3 of {lower, upper, digit, symbol}, **or** length ≥ 16 (passphrases) |
| Must not contain login or display name parts (≥ 4 chars), case-insensitive | on |
| Breached list | Embedded list of the 100,000 most common breached passwords (compressed resource), case-insensitive exact match |
| History | Last 5 hashes rejected; only these are kept: every password change deletes the older rows of `password_history` |
| Expiry | Off (NIST guidance); configurable in days |

Errors are returned as field errors on `/password` with codes `identity.password.too_short`,
`…too_long`, `…invalid_characters`, `…too_simple`, `…contains_login`, `…breached`, `…reused`.

## 6. Tokens and sessions

### 6.1 Sign-in (local, part 1)

```csharp
public interface IAuthenticationService
{
    Task<Result<SignInOutcome>> SignInLocalAsync(SignInRequest request, CancellationToken ct);
    Task<Result<TokenPair>> RefreshAsync(string refreshToken, ClientContext client, CancellationToken ct);
    Task<Result> SignOutAsync(SessionId session, CancellationToken ct);
    Task<Result<SignInOutcome>> CompletePasswordChangeAsync(string changeToken, string newPassword, CancellationToken ct);  // Signed, or MfaRequired / MfaEnrolmentRequired (step 8 still owed)
    // part 2 (§10): MFA and step-up
    Task<Result<TokenPair>> VerifyMfaAsync(MfaVerification verification, CancellationToken ct);
    Task<Result<WebAuthnCeremony>> BeginMfaWebAuthnAsync(string mfaToken, ClientContext client, CancellationToken ct);
    Task<Result<TotpEnrolment>> BeginMfaEnrolmentAsync(string enrolmentToken, ClientContext client, CancellationToken ct);
    Task<Result<MfaEnrolmentCompleted>> CompleteMfaEnrolmentAsync(string enrolmentToken, string code, ClientContext client, CancellationToken ct);
    Task<Result<StepUpProof>> StepUpAsync(SessionId session, StepUpRequest request, CancellationToken ct);
    Task<Result<WebAuthnCeremony>> BeginStepUpWebAuthnAsync(SessionId session, CancellationToken ct);
    Result ValidateStepUp(string stepUpToken, SessionId session);
    // part 2 (T3.07): SignInDirectoryAsync (through SignInLocalAsync when user AuthSource=Directory), OIDC
}
public sealed record MfaVerification(string MfaToken, MfaMethod Method /* Totp | WebAuthn | Recovery */, ClientContext Client, string? Code = null, string? Assertion = null);
public sealed record StepUpRequest(string? Password = null, string? Totp = null, string? Assertion = null);   // exactly one
public sealed record StepUpProof(string StepUpToken, DateTimeOffset ExpiresAt);
public sealed record MfaEnrolmentCompleted(TokenPair Tokens, RecoveryCodes RecoveryCodes);

public sealed record SignInRequest(string Login, string Password, ClientContext Client, string? Reason = null /* break-glass, §11 */);
public sealed record ClientContext(ClientKind Kind, string ClientInstanceId /* random id generated by the client at install, ≤ 64 chars */,
    string? WorkstationId, IPAddress? RemoteIp, string? UserAgent, string? AppVersion,
    string? BrowserOrigin = null /* Origin header of a browser request; set by the API (§12) */,
    bool WorkstationVerified = false /* mTLS endpoint (§12) */);

public abstract record SignInOutcome
{
    public sealed record Signed(TokenPair Tokens, User User) : SignInOutcome;
    public sealed record MfaRequired(string MfaToken /* 5 min, single use */, IReadOnlyList<string> Methods) : SignInOutcome;   // part 2
    public sealed record MfaEnrolmentRequired(string EnrolmentToken /* 10 min, single use */) : SignInOutcome;                // part 2, §10
    public sealed record PasswordChangeRequired(string ChangeToken /* 10 min */) : SignInOutcome;
}

public sealed record TokenPair(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, SessionId Session);
```

Sign-in algorithm:

1. Rate limit per remote IP (`Identity:SignInRatePerIp`, default 20 per minute) and per login
   (10 per minute) → `common.rate_limited`. The limiter's memory is bounded: logins longer than 64
   characters share one bucket and keys without attempt in the window are dropped.
2. Find user; if not found / template / script → dummy hash verify → `identity.signin.invalid_credentials`.
3. If a lockout is active: verify the password anyway; wrong password →
   `identity.signin.invalid_credentials`; correct password → `identity.signin.locked` (args
   `until`). Lockout status is therefore never revealed to someone who does not know the
   password.
4. Verify password; on failure increment failure count (§6.4), audit `signInFailed`, return
   `invalid_credentials`.
   (part 2) Break-glass account (§11): `Reason` missing, blank or > 512 chars →
   `identity.signin.reason_required`; client kind other than desktop → `client_not_allowed`. Both are
   checked only after the correct password, and both are audited (`breakGlassUsed`, denied).
5. Status checks: disabled → `identity.signin.disabled`; expired → `identity.signin.expired`;
   a browser request (`BrowserOrigin` set) with a kind other than Web, or client kind not allowed →
   `identity.signin.client_not_allowed`; workstation not allowed →
   `identity.signin.workstation_not_allowed`; configuration user without a verified workstation while
   `Identity:RequireEnrolledWorkstationForConfiguration` is on → `identity.signin.workstation_not_verified` (§12).
6. `ISessionAdmission` hooks (all must allow): MNT (maintenance), LIC (seats, M7),
   concurrent session limit → errors from the hook (e.g. `maintenance.active`).
7. If `MustChangePassword` → `PasswordChangeRequired`. The change token is 32 random bytes
   (CSPRNG, base64url), kept hashed, **single use**, valid 10 min; a user has one live token (a new
   sign-in replaces it) and every token of the user dies when the password changes. Completing the
   change repeats steps 5 and 6 (the account may have been disabled meanwhile) and requires
   `MustChangePassword` to be still set; a refused new password does not consume the token.
8. (part 2) If MFA is required (§10, never for break-glass): a method is enrolled → `MfaRequired`
   (token 5 min); nothing enrolled → `MfaEnrolmentRequired` (token 10 min). No session or token of
   any other kind exists until the factor is presented. The tokens of steps 7 and 8 are 32 CSPRNG
   bytes (base64url), kept as SHA-256 only, **single use**, **bound to the client** (kind and
   instance id), one live token per user and kind (a new sign-in replaces it); they are neither
   JWTs nor refresh tokens and no other endpoint accepts them. Completing step 7 or 8 runs steps 5
   and 6 again (the account may have changed meanwhile) and, after a password change, step 8.
9. Create session and tokens; reset failure count; update `LastSignInAt`; audit `signedIn`
   (details: client kind, session, method `password` | `mfa:totp` | `mfa:webauthn` | `mfa:recovery` |
   `mfa:totp-enrolment` | `password-change`).

### 6.2 Access token (JWT, ES256)

| Claim | Value |
|---|---|
| `iss` | `urn:watchpoint:site:<siteId>` (`Site:Id` of the node settings, HST §4; T1.18s) |
| `aud` | `watchpoint` |
| `sub` | user id |
| `sid` | session id |
| `ck` | client kind (`desktop`, `web`, `mobile`, `api`) |
| `wks` | workstation id (if any) |
| `wkv` | `true` when the workstation proved its certificate (§12; omitted otherwise, UI warning) |
| `adm` | `true` if administrator (omitted otherwise) |
| `lang` | user language |
| `iat`, `nbf`, `exp` | lifetime `Identity:AccessTokenLifetime` (default 10 min) |
| `jti` | random id |

- `exp` is never later than the end of the session (`Session.ExpiresAt`).
- Signing keys: ECDSA P-256 keys in a **key ring** (`SigningKeyRing`) stored as bootstrap
  secrets `identity.jwt.<kid>` (PKCS#8; so every node of a cluster can validate with the same
  keys, M9) and listed in the bootstrap secret `identity.jwt.index` (JSON: kid, created, end of
  signing — the bootstrap store cannot enumerate names). `kid` header (16 hex chars, CSPRNG);
  rotation every 30 days; previous key kept 1 day for validation. An explicit rotation (key
  compromise) stops the previous keys signing at once; they validate for 1 more day.
- The ring is **loaded from the store** at first use and when a token carries an unknown `kid`
  (at most one reload per 30 s), so tokens stay valid over a restart and a key created by another
  node is found. **Fail closed**: a key that cannot be stored is not used, and an index or key
  that cannot be read is an error — never a silent new ring. (Two nodes rotating in the same
  instant can still overwrite each other's index entry; CLU serialises rotation with a lease, M9.)
- Validation (API middleware): **algorithm pinned to ES256** (`none`, HMAC and any other
  algorithm are refused whatever the key), the key is the ring key named by `kid` (no `kid` → no
  key), signature, `iss`, `aud`, `exp` required, lifetime (clock skew 30 s), `ck` must be a known
  client kind, and the session is **not revoked** (in-memory revocation set fed by
  `session.revoked` records, checked per request — O(1)). The set is **loaded from the `sessions`
  table at first use** (sessions not yet ended), so a revocation survives a restart.
- A refused token gives `identity.token.invalid` (or `expired` / `revoked`) **without detail**:
  the reason is not returned to the caller.
- Rights are **not** in the token (they can change at any time); RGT evaluates per request.

### 6.3 Refresh tokens and sessions

- Refresh token: 32 random bytes, base64url, given to the client; the server stores
  `SHA-256(token)` only. Lifetime: sliding `Identity:RefreshIdleLifetime` (default 12 h),
  absolute `SessionPolicy.MaxSessionLength` (default 7 days desktop/mobile, 12 h web).
- **Rotation**: each refresh returns a new refresh token and invalidates the previous one. Reuse
  of an already-rotated token → the **whole session is revoked** (theft detection), audit
  `refreshTokenReuse` (warning, log 2713).
- Rotation is **atomic**: of several concurrent refreshes with the same token exactly one
  rotates; the others are a reuse. In one process a per-token gate serialises them; between nodes
  `rotated_at` is an optimistic concurrency token (`UPDATE … WHERE rotated_at IS NULL`), a lost
  race is a reuse.
- A refresh token never expires after the end of its session.
- **Order of a refresh**: (1) the token is validated and claimed (reuse, expiry, session, binding);
  (2) the user and admission checks run; (3) the token is rotated only if they pass. A user who
  can no longer sign in (deleted, kind Template/Script → `identity.refresh.invalid`; disabled →
  `identity.signin.disabled`; expired → `identity.signin.expired`) loses the **session**. A
  refusal by an `ISessionAdmission` hook (maintenance, seats) is temporary: nothing is rotated or
  revoked and the same token works once the hook allows. Reuse is detected in step 1, before any
  hook is called.
- Binding: refresh requires the same `ClientInstanceId` and client kind as the session; a browser
  request (`BrowserOrigin`) can only refresh a Web session (`identity.refresh.invalid`, nothing
  rotated or revoked).
- Web client: refresh token in a cookie `wp_rt` (HttpOnly, Secure, SameSite=Strict, Path
  `/api/v1/auth`); other clients receive it in the response body and store it in OS secure
  storage.

```csharp
public sealed record Session(SessionId Id, UserId UserId, ClientKind ClientKind, string ClientInstanceId,
    string? WorkstationId, string? RemoteIp, string? UserAgent, string? AppVersion, DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, string? RevokedReason,
    bool WorkstationVerified = false /* §12 */);

// Session management for the API (part 2): own sessions always; others' need admin.sessions.view / .manage
// (administrators until RGT, through IPermissionSummaryProvider); revocations by someone else are audited Sensitive.
public interface ISessionManagement
{
    Task<Result<IReadOnlyList<Session>>> ListAsync(UserId actor, UserId? forUser, CancellationToken ct);   // identity.session.forbidden
    Task<Result> RevokeAsync(UserId actor, SessionId session, string reason, CancellationToken ct);         // identity.session.not_found / forbidden
}

public interface ISessionRegistry
{
    IReadOnlyList<Session> ListActive(SessionQuery query);
    Result<Session> Get(SessionId id);
    Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct);
    Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct);
    Task<int> RevokeWhereAsync(Func<Session, bool> predicate, string reason, CancellationToken ct);   // MNT, rights changes
    bool IsRevoked(SessionId id);
    void Touch(SessionId id);       // last activity, persisted at most every 60 s
}
```

Revocation publishes `session.revoked { sessionId, userId, reason }` to `WP_SYS`; the API hub
closes the affected live streams with the reason (API §8.6). Revoking a session twice keeps the
first instant and reason and publishes once. The registry loads the sessions that have not ended
from the database at first use, so `ListActive`, `RevokeWhereAsync` and the concurrent-session
limit also see sessions created before a restart; sessions ended for more than a day leave memory.

Automatic revocations: password changed/reset (all sessions of the user except the current one
on self-change), user disabled/deleted/expired, administrator flag removed, kind changed to
Template or Script, refresh token reuse,
maintenance mode (MNT), explicit sign-out.

### 6.4 Lockout

- Failure counter per user in a sliding window of 15 minutes; concurrent failures are all counted
  (the read-modify-write of the counter is serialised).
- At 5 failures: lock for 15 min; each further lock in 24 h doubles the duration (30 min, 1 h…)
  up to 24 h; when the previous lock ended more than 24 h ago the next one starts again at 15 min.
  Unlock: time, or administrator action (`POST /users/{id}/unlock`).
- Wrong second factors (§10), wrong current passwords (§4) and wrong step-up factors (§10) count as
  failures too.
- Lockouts are audited (`identity.accountLocked`, Sensitive) and produce a system event
  `identity.accountLocked { userId, until }` (alarm-able, ALM) through `ISystemEventSink`.
- `ILockoutManager` (`GetStatusAsync`, `UnlockAsync(user, ctx)`): the unlock needs an enabled
  administrator (or the system) → else `identity.user.admin_required`; it clears the lock, the
  failures and the 24 h lock count, and is audited (`identity.accountUnlocked`, Sensitive).

## 7. Settings, API, data (part 1)

### 7.1 Settings

| Key | Default |
|---|---|
| `Identity:AccessTokenLifetime` | `PT10M` |
| `Identity:RefreshIdleLifetime` | `PT12H` |
| `Identity:WebMaxSessionLength` / `DesktopMaxSessionLength` / `MobileMaxSessionLength` | `PT12H` / `P7D` / `P30D` |
| `Identity:SignInRatePerIp` / `SignInRatePerLogin` | 20 / 10 per minute |
| `Identity:Lockout:Threshold` / `Window` / `BaseDuration` / `MaxDuration` | 5 / PT15M / PT15M / P1D |
| `Identity:Password:MinLength` / `History` / `ExpiryDays` | 12 / 5 / 0 |
| `Identity:BootstrapAdmin:Login` | `admin` |
| `Identity:BootstrapAdmin:PasswordEnv` | `WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD` |
| `Identity:Mfa:RequireFor` (§10) | `administrators` |
| `Identity:WebAuthn:RpId` / `Origins` (§10) | empty (WebAuthn disabled) / empty |
| `Identity:RequireEnrolledWorkstationForConfiguration` (§12) | `false` |

Binding (T1.18s, `IdentitySettings.Bind`): the host binds the `Identity` section at start-up and
**fails fast** (`InvalidSettingsException` → log `HST1101`, exit code 78, HST §4) when a value is
invalid, so a setting can never silently fall back to a default the operator did not choose.
Durations are ISO 8601 (`PT10M`, `P7D`). Every problem is reported at once (field errors
`identity.settings.invalid`, path = key, codes and keys only, never values). Rules:

| Key | Valid |
|---|---|
| `AccessTokenLifetime` | PT1M–PT1H |
| `RefreshIdleLifetime` | PT5M–P30D |
| `*MaxSessionLength` | PT5M–P365D |
| `SignInRatePerIp`, `SignInRatePerLogin` | 1–100000 |
| `Lockout:Threshold` | 1–100 |
| `Lockout:Window`, `Lockout:BaseDuration` | PT1M–P1D |
| `Lockout:MaxDuration` | PT1M–P30D and ≥ `BaseDuration` |
| `Password:MinLength` / `History` / `ExpiryDays` | 8–256 / 0–24 / 0–3650 |
| `BootstrapAdmin:Login` | rule U1 |
| `BootstrapAdmin:PasswordEnv` | `^[A-Za-z_][A-Za-z0-9_]*$`, ≤ 128 |
| `Mfa:RequireFor` | `none`, `administrators`, `configurationUsers`, `everyone` |
| `WebAuthn:RpId` | host name (never an IP address); when set, `Origins` is required: `https://host[:port]` on the RP id or a subdomain of it |
| any other key under `Identity` | refused (`identity.settings.unknown_key`): a misspelt key would otherwise keep the default |

The token issuer is `urn:watchpoint:site:<Site:Id>` (§6.2): `Site:Id` is a node setting (HST §4)
generated at first start and written back to `watchpoint.json`, shared by every node of a site
(copied by `wpctl cluster join`, M9). The module refuses to start without it.

**Bootstrap administrator** (startup phase `Configuration`): if no Person administrator exists,
create `admin` with the password from the environment variable (Production). If the variable
is absent, the node still starts (supervision of connections works) but logs 2701 Critical
("no administrator; run `wpctl reset-admin`"); nobody can sign in until the command is run.
Development: the dev seed creates users (HST §9).
The created admin has `MustChangePassword = true` in Production.
The administrator is created **once**: later starts never change the account or its password. If
the creation fails (e.g. the password of the variable does not meet the policy) the node logs 2702
(error code only, never the password) and 2701.

### 7.2 API

| Method and path | Auth | Description |
|---|---|---|
| `POST /api/v1/auth/sign-in` | anonymous | Body `{ login, password, clientKind, clientInstanceId, workstationId? }` → `200 { accessToken, accessExpiresAt, refreshToken?, refreshExpiresAt, sessionId, user }` or `200 { "next": "mfa", mfaToken, methods }` or `200 { "next": "changePassword", changeToken }`; errors 401 with codes |
| `POST /api/v1/auth/refresh` | refresh token (body or cookie) | → new pair |
| `POST /api/v1/auth/sign-out` | access token | Revokes the current session |
| `POST /api/v1/auth/change-password` | access token or `changeToken` | `{ current?, new }` |
| `GET /api/v1/auth/me` | access token | Current user, session, effective permissions summary (RGT, M3) |
| `GET /api/v1/users`, `GET /api/v1/users/{id}` | `config.users.view` (M1: administrator) | |
| `POST /api/v1/users`, `PATCH /api/v1/users/{id}`, `DELETE /api/v1/users/{id}` | `config.users.edit` (M1: administrator) | |
| `POST /api/v1/users/{id}/password` | `config.users.edit` | Admin reset `{ password, mustChange }` |
| `POST /api/v1/users/{id}/unlock` | `config.users.edit` | |
| `GET /api/v1/sessions?user=` | access token; other users' sessions `admin.sessions.view` | Active sessions (`ISessionManagement`) |
| `DELETE /api/v1/sessions/{id}` | access token; other users' sessions `admin.sessions.manage` | Revoke (audited) |
| `POST /api/v1/auth/mfa/verify` | `mfaToken` | `{ mfaToken, method, clientKind, clientInstanceId, code? \| assertion? }` → tokens (§10) |
| `POST /api/v1/auth/mfa/webauthn/options` | `mfaToken` | Assertion options for the pending sign-in |
| `POST /api/v1/auth/mfa/enrol/totp`, `…/enrol/totp/confirm` | `enrolmentToken` | Enrolment when `next = "mfaEnrolment"` (§10); confirm → tokens + recovery codes |
| `POST /api/v1/auth/mfa/totp/enroll`, `…/totp/confirm`, `…/recovery-codes`, `…/webauthn/register`, `…/webauthn/register/complete`, `DELETE …/webauthn/{id}` | access token | Self-service MFA (`IMfaService`) |
| `POST /api/v1/users/{id}/mfa/reset` | `config.users.edit` (M1: administrator) | Administrator reset (audited) |
| `POST /api/v1/auth/step-up`, `…/step-up/webauthn/options` | access token | Step-up proof for critical commands (§10) |
| `GET /api/v1/users/{id}/lockout` | `config.users.view` | Lockout state (`ILockoutManager`) |

User JSON never contains password hashes, TOTP secrets or recovery codes.

### 7.3 Data (`wp_identity` schema)

| Table | Key columns |
|---|---|
| `users` | id, login, login_norm (lower, unique where deleted_at is null), display_name, email, phone, language, time_zone, kind, enabled, administrator, auth_source, external_id, external_provider, valid_from, valid_until, allowed_clients text (JSON array), allowed_workstations text (JSON array), session_policy jsonb, mfa_policy varchar(32), password_hash null, password_changed_at, must_change_password, last_sign_in_at, notes, deleted_at null, row_version, created_at, updated_at |
| `password_history` | user_id, hash, created_at (keep last N) |
| `sessions` | id, user_id, client_kind, client_instance_id, workstation_id, remote_ip, user_agent, app_version, created_at, last_activity_at, expires_at, revoked_at, revoked_reason, workstation_verified (T3.08) |
| `refresh_tokens` | token_hash (PK), session_id, created_at, expires_at, rotated_at null, replaced_by_hash null |
| `lockouts` | user_id, failures jsonb (timestamps), locked_until null, lock_count_24h |
| `mfa_totp` (T3.08) | user_id (PK), secret_id (vault secret, never the secret), created_at, confirmed_at null, last_used_step (concurrency token) |
| `mfa_recovery_codes` (T3.08) | id, user_id, hash (Argon2id), created_at, used_at null (concurrency token) |
| `webauthn_credentials` (T3.08) | id, user_id, credential_id (unique), public_key, sign_count (concurrency token), transports, name, aaguid, created_at, last_used_at |
| (part 2, T3.07) `external_identities`, `identity_providers` | see §8–§9 |

## 8. Directory sign-in — LDAP / Active Directory (part 2, T3.07)

Provider configuration (`identity_providers`, kind `ldap`; edited in configuration UI):

| Field | Example |
|---|---|
| `servers` | `["ldaps://dc1.corp.local:636", "ldaps://dc2.corp.local:636"]` (tried in order) |
| `tls` | `ldaps` \| `startTls` \| `none` (none only with explicit `allowInsecure`) + optional CA PEM / pin |
| `bindDn` + bind password (secret) | service account for searches |
| `userSearchBase`, `userFilter` | `OU=Users,DC=corp,DC=local`, `(&(objectClass=user)(sAMAccountName={login}))` (login escaped per RFC 4515) |
| `attributes` | `displayName`, `mail`, `telephoneNumber`, `preferredLanguage` |
| `groupMembership` | `memberOf` with AD nested resolution via `1.2.840.113556.1.4.1941` |
| `mappings` | list of `{ directoryGroupDn, templateUserId?, userGroupIds[] }` (first matching template wins by order) |
| `provisioning` | `jit` (create user at first successful sign-in) \| `existingOnly` |

Flow: search user with bind account → bind as the user DN with the provided password (validates
password; never stored) → read attributes and groups → create/update the Watchpoint user
(`AuthSource=Directory`, `ExternalId` = DN or objectGUID) → apply mappings (RGT applies template
rights via the rights-copy engine in `Merge` mode with source tag "directory mapping", user groups
set) → continue §6.1 from step 5. Directory unreachable → `identity.directory.unavailable`
(break-glass remains possible). Timeouts: connect 5 s, operation 10 s.

## 9. OpenID Connect sign-in (part 2, T3.07)

Provider configuration (kind `oidc`): issuer URL (discovery `.well-known/openid-configuration`),
client id, client secret (secret) or none (public client with PKCE), scopes (`openid profile email`
+ optional groups scope), claim mapping (`login` ← `preferred_username`/`upn`/`email`,
`displayName` ← `name`, `groups` ← configurable claim), mappings like §8, provisioning mode.

Flow (authorization code + PKCE S256, `state` and `nonce`):

1. Client calls `GET /api/v1/auth/oidc/{providerId}/start?clientKind=&clientInstanceId=&redirect=`
   → node creates state (10 min, single use; stores PKCE verifier and client context) → `302` to
   the IdP authorize endpoint. Desktop: `redirect` = loopback `http://127.0.0.1:<port>/callback`
   (RFC 8252) handled by the Electron main process; web: same-origin `/auth/callback`; mobile:
   `watchpoint://auth/callback`.
2. IdP redirects to `GET /api/v1/auth/oidc/callback?code=&state=` → node exchanges the code,
   validates the ID token (signature via JWKS, `iss`, `aud`, `nonce`, `exp`), maps claims,
   provisions/updates the user, applies §6.1 steps 5–9, then redirects to the client redirect
   with a one-time `exchangeCode` (60 s).
3. Client calls `POST /api/v1/auth/oidc/exchange { exchangeCode, clientInstanceId }` → tokens.

SAML is supported through an OIDC broker (e.g. Keycloak) — no SAML code in Watchpoint.

## 10. MFA (part 2, T3.08)

- **Policy** (`IMfaService.IsRequiredAsync`): effective MFA requirement = user `Mfa` (`Required`/`NotRequired`),
  else site policy (`Identity:Mfa:RequireFor` = `none` | `administrators` | `configurationUsers` | `everyone`;
  default `administrators`). "configurationUsers" = administrators and users holding any `config.*`
  permission (`IPermissionSummaryProvider`, RGT; until RGT exists administrators hold `*` and nobody
  else holds anything). The break-glass account never requires MFA (§11).
- **Enrolment**: `EnrollTotpAsync` → secret (20 bytes from the CSPRNG, base32) + `otpauth://totp/Watchpoint:<login>?secret=…&issuer=Watchpoint&algorithm=SHA1&digits=6&period=30`
  (QR shown by the client); the secret is **stored in the vault** (`identity.totp.<userId>`, owner
  `Identity` / entity = user; the row `mfa_totp` holds the secret id only) and returned once; a pending
  enrolment is replaced (old secret deleted), a confirmed one needs a reset first
  (`identity.mfa.already_enrolled`); without a vault enrolment is refused (`identity.mfa.vault_unavailable`),
  never stored in the database. `ConfirmTotpAsync { code }` activates only with a valid code
  (`identity.mfa.code_invalid` otherwise) and returns **10 recovery codes** (10 chars from an alphabet
  without look-alikes, shown once as `XXXXX-XXXXX`; stored Argon2id-hashed; single use; `used_at` is a
  concurrency token; `RegenerateRecoveryCodesAsync` invalidates the old ones).
- **MFA required, nothing enrolled**: the sign-in returns `MfaEnrolmentRequired(enrolmentToken)`; the
  token (10 min, single use, bound to the client) allows only `BeginMfaEnrolmentAsync` (secret + URI)
  and `CompleteMfaEnrolmentAsync(code)`, which confirms, runs steps 5–6 again and signs in, returning
  the tokens and the recovery codes. A wrong code counts as a sign-in failure; five invalidate the token.
- **TOTP**: RFC 6238, HMAC-SHA1, 6 digits, 30 s, window ±1 step; every candidate step is computed and
  compared in **constant time**; a code is accepted once: the last accepted step is stored
  (`last_used_step`, concurrency token, so two nodes cannot accept the same code) and any code of that
  step or an earlier one is a replay. Verifications of one user are serialised in the process.
- **WebAuthn / passkeys**: registration and assertion ceremonies with Fido2NetLib; RP id = site host
  name (`Identity:WebAuthn:RpId`, §7.1), allowed origins `Identity:WebAuthn:Origins` (both verified
  by the library together with the challenge and the signature; without `RpId` → `identity.webauthn.not_configured`);
  attestation `none`, user verification preferred; challenges are **single use**, bound to the user and the
  ceremony kind, and expire after 5 min (TimeProvider); the **signature counter must grow** (a lower or
  equal counter = cloned authenticator → refused; the stored counter is a concurrency token); credentials
  stored (`webauthn_credentials`: id, public key, sign count, transports, name, aaguid, created_at,
  last_used_at). Every refusal is `identity.webauthn.invalid` / `identity.signin.mfa_invalid` without detail.
- **Sign-in**: `MfaRequired(mfaToken, methods)` outcome (methods: `totp`, `webauthn`, `recovery`) →
  `VerifyMfaAsync { mfaToken, method: "totp" | "webauthn" | "recovery", client, code? | assertion? }` →
  tokens (`BeginMfaWebAuthnAsync` gives the assertion options first). The token is single use, 5 min,
  bound to the client that started the sign-in (another client → `mfa_invalid`, no consumption).
  A wrong factor → `identity.signin.mfa_invalid`, counted as a sign-in failure for the lockout (§6.4)
  and audited (`mfaVerifyFailed`); **5 wrong codes → mfaToken invalidated** (log 2743). The token is
  not a refresh token, not a JWT and not a change token: refresh, token validation and the password
  change flow refuse it. Steps 5 and 6 run again when the factor is accepted (a user disabled
  meanwhile is refused).
- **Re-authentication** for critical commands (CMD constraint): `StepUpAsync(session, { password? | totp? | assertion? })`
  (exactly one factor; `BeginStepUpWebAuthnAsync` for the options) → `StepUpProof(stepUpToken, 5 min)`
  bound to the session; `ValidateStepUp(token, session)` is passed with the command and fails
  (`identity.stepup.required`) for another session, after 5 min or once the session is revoked. A
  wrong password or factor is `identity.stepup.invalid` and counts for the lockout (a stolen session
  gives no free guesses). Audited `identity.stepUp`.
- **Reset**: `ResetAsync(user, ctx)` by an enabled administrator removes the TOTP secret (vault), the
  recovery codes and the WebAuthn credentials (audited `identity.mfaReset`, Sensitive; log 2741;
  notification to the user by e-mail when NOT exists). A non-administrator is refused
  (`identity.user.admin_required`, audited as denied).
- `ICurrentUserService.GetAsync(claims)` resolves `GET /auth/me`: user, session, permissions summary
  (RGT hook) and MFA status; refused when the session is revoked or expired.

## 11. Break-glass access (part 2, T3.08)

- One user of kind `BreakGlass` (login `breakglass`), created by the setup wizard (DEP) with a
  long random password printed once for sealed storage; local authentication only; MFA not
  required (it must work when everything else fails).
- Rules enforced by `IUserDirectory` (nothing about this account changes silently):
  - the login `breakglass` is reserved for the kind and the kind requires that login
    (`identity.breakglass.invalid`, field `/login` or `/kind`); `AuthSource` must be `Local`;
  - at most one non-deleted break-glass account (`identity.breakglass.exists` / `login_taken`);
  - it is always an **administrator** (U5 applies: the system or an enabled administrator creates it),
    `MustChangePassword` is forced off and `Mfa` to `NotRequired`; updates cannot change its kind,
    administrator flag, authentication source or MFA policy, and no other account becomes break-glass;
  - creating, re-enabling and resetting the password of the account are **critical audit records**
    (`identity.breakGlassAccountChanged`, action `created` | `enabled` | `passwordReset`) and log 2731.
- Sign-in requires a `reason` field (1–512 chars, checked after the password; §6.1 step 4);
  produces audit `breakGlassUsed` (critical, with the reason, client, IP and session), a system event
  `identity.breakGlassUsed` (alarm-able, default rule Critical), log 2730, and a notification to
  administrators when possible. A refused attempt (no reason, wrong client) is audited as denied.
- Sessions limited to 2 hours (access and refresh tokens never outlive it), client kind desktop only.
  Rate limits and the lockout apply as to any account; the enrolled-workstation requirement of §12
  does not (it must work when the certificate infrastructure is what failed).

## 12. Client kind binding and session admission (part 2, T3.08)

- `ck` claim is set from the sign-in `clientKind`. A request made by a **browser** carries an
  `Origin`; the API puts it in `ClientContext.BrowserOrigin`, and a browser can only be the Web
  client: `clientKind` desktop, mobile or api with an origin → `identity.signin.client_not_allowed`,
  and a browser cannot refresh a non-web session (`identity.refresh.invalid`). So the cookie path
  (web refresh token, §6.3) never carries a desktop session.
- The desktop client additionally proves its kind with the **workstation certificate** (issued at
  enrolment by SEC CA, M3): sign-in requests with `clientKind=desktop` from a workstation-enrolled
  client present the certificate (mTLS on a dedicated endpoint `/api/v1/auth/desktop/sign-in`), which
  sets `ClientContext.WorkstationVerified`; the session stores it (`workstation_verified`) and the
  access token carries `wkv=true`. The flag is ignored for any other client kind. Without enrolment,
  `ck=desktop` is accepted but not verified (`wkv` absent, UI warning).
- Sites can require enrolled workstations for configuration sessions
  (`Identity:RequireEnrolledWorkstationForConfiguration`, default false in v1): when on, an
  administrator or a user with any `config.*` permission (`IPermissionSummaryProvider`) signing in
  without a verified desktop workstation gets `identity.signin.workstation_not_verified` (break-glass
  excepted, §11). Operators are not affected.
- `ISessionAdmission` (unchanged): hooks run in order at sign-in (also when a pending token completes
  the sign-in) and at refresh; a refusal at refresh is temporary (§6.3). The concurrent session limit
  (`SessionPolicy.MaxConcurrentSessions`) refuses the new sign-in with `identity.session.limit_reached`
  (args `max`) and never revokes an older session.

```csharp
public interface ISessionAdmission
{
    int Order { get; }
    ValueTask<Result> AdmitAsync(User user, ClientContext client, AdmissionPhase phase /* SignIn | Refresh */, CancellationToken ct);
}
```

## 13. Errors

| Code | Kind | Notes |
|---|---|---|
| `identity.signin.invalid_credentials` | Unauthenticated | Generic, no hint |
| `identity.signin.locked` | Unauthenticated | args `until` (only with correct password) |
| `identity.signin.disabled` / `expired` / `client_not_allowed` / `workstation_not_allowed` | Forbidden | |
| `identity.signin.mfa_invalid` | Unauthenticated | Wrong or expired mfaToken, wrong factor, other client; no detail |
| `identity.signin.reason_required` | Validation | Break-glass without reason (field `/reason`, §11) |
| `identity.signin.workstation_not_verified` | Forbidden | §12 |
| `identity.session.limit_reached` | TooManyRequests | args `max` |
| `identity.session.not_found` / `forbidden` | NotFound / Forbidden | Session management |
| `identity.mfa.not_enrolled` / `already_enrolled` / `vault_unavailable` | Unprocessable / Conflict / Unavailable | |
| `identity.mfa.code_invalid` | Validation | Enrolment confirmation (field `/code`) |
| `identity.webauthn.not_configured` / `invalid` | Unprocessable / Unauthenticated | `invalid` carries no detail |
| `identity.breakglass.exists` / `invalid` | Conflict / Validation | §11 |
| `identity.settings.invalid` / `unknown_key` | Validation | Start-up only (§7.1) |
| `identity.token.invalid` / `expired` / `revoked` | Unauthenticated | No detail; `invalid` also for a bad or used change token |
| `identity.refresh.invalid` / `reused` | Unauthenticated | `reused` revokes the session |
| `identity.user.not_found` | NotFound | |
| `identity.user.login_taken` | Validation | |
| `identity.user.login_invalid` | Validation | U1: login not 2–64 characters of `[A-Za-z0-9._@-]` (field `/login`) |
| `identity.user.last_administrator` | Conflict | |
| `identity.user.self_change_forbidden` | Forbidden | U4 |
| `identity.user.admin_required` | Forbidden | U5 |
| `identity.password.too_short` / `too_long` / `invalid_characters` / `too_simple` / `contains_login` / `breached` / `reused` / `incorrect` | Validation | field errors |
| `identity.directory.unavailable` | Unavailable | |
| `identity.oidc.state_invalid` / `token_invalid` / `provider_error` | Unauthenticated | |
| `identity.stepup.required` / `invalid` | Forbidden | CMD critical commands; `invalid` counts for the lockout |

## 14. Observability and security

| EventId | Level | Message |
|---|---|---|
| 2701 | Critical | No administrator account exists; run `wpctl reset-admin` |
| 2702 | Error | Bootstrap administrator could not be created: {Code} |
| 2710 | Information | User {UserId} signed in ({ClientKind}) session {SessionId} |
| 2711 | Warning | Sign-in failed for login {LoginHash} from {RemoteIp}: {Code} (login hashed in logs) |
| 2712 | Warning | Account {UserId} locked until {Until} |
| 2713 | Warning | Refresh token reuse detected; session {SessionId} revoked |
| 2720 | Information | Session {SessionId} revoked: {Reason} |
| 2730 | Warning | Break-glass sign-in by {RemoteIp}: {Reason} |
| 2731 | Warning | Break-glass account {Action} ({UserId}) by {Actor} |
| 2740 | Information | User {UserId} enrolled MFA method {Method} |
| 2741 | Warning | MFA of user {UserId} reset by {Actor} |
| 2742 | Warning | MFA verification failed for user {UserId} ({Method}) |
| 2743 | Warning | MFA token of user {UserId} invalidated after too many wrong codes |
| 2790 / 2791 / 2792 | Information / Warning / Critical | Audit {Action} {Outcome} actor {Actor} subject {Subject} (normal / sensitive / critical records, until AUD takes over) |

Metrics: `watchpoint.identity.signins` (counter by `outcome`, `clientKind`, `source`),
`watchpoint.identity.sessions.active` (gauge by `clientKind`), `watchpoint.identity.hash.wait`
(histogram ms).

Security: constant-time comparisons (passwords, TOTP codes, recovery codes); no user enumeration;
tokens, secrets, codes and passwords never logged nor audited (audit details hold ids and kinds);
logins hashed in warning logs; passwords limited to 256 chars before hashing; change, MFA,
enrolment and refresh tokens and step-up proofs from the CSPRNG and stored hashed; TOTP secrets in
the vault; JWT algorithm pinned; LDAP filter escaping (RFC 4515) and DN escaping (RFC 4514).

Audit records (`IIdentityAuditSink`): `identity.signedIn`, `signInFailed` (known users only),
`accountLocked`, `accountUnlocked`, `mfaEnrolled`, `mfaVerifyFailed`, `mfaReset`,
`recoveryCodesRegenerated`, `stepUp`, `sessionRevoked`, `breakGlassUsed`, `breakGlassAccountChanged`.
System events (`ISystemEventSink`): `identity.accountLocked`, `identity.breakGlassUsed`.

## 15. Tests

Unit: `tests/service/Watchpoint.Identity.Tests`. Integration: `Watchpoint.IntegrationTests/Identity`
(PostgreSQL; LDAP tests against an OpenLDAP container `bitnami/openldap` or `osixia/openldap`;
OIDC tests against a Keycloak container).

| Id | Layer | Test |
|---|---|---|
| IDN-U-001 | U | Login rules U1 (pattern, length, case-insensitive uniqueness) |
| IDN-U-002 | U | Template/script users cannot sign in (generic error) |
| IDN-U-003 | U | Last administrator protected (delete, disable, demote, expire) |
| IDN-U-004 | U | Self-change of admin/enabled/kind forbidden |
| IDN-U-005 | U | Only administrators create administrators |
| IDN-U-006 | U | Effective status computation (disabled, expired before/after window, locked) |
| IDN-U-007 | U | Soft delete keeps record; login reusable after 30 days |
| IDN-U-008 | U | Argon2id hash format and verification; wrong password fails |
| IDN-U-009 | U | Rehash on parameter upgrade |
| IDN-U-010 | U | Hash pool bound: 10 concurrent sign-ins → max 4 hashing; waits beyond 5 s → rate limited |
| IDN-U-011 | U | Unknown login takes dummy-hash path (timing within ±20 % of known user) |
| IDN-U-012 | U | Password policy: each rule with valid/invalid examples incl. passphrase ≥ 16 |
| IDN-U-013 | U | Breached list match case-insensitive |
| IDN-U-014 | U | Password history rejects last 5 |
| IDN-U-015 | U | Sign-in success creates session and tokens, updates LastSignInAt |
| IDN-U-016 | U | Rate limit per IP and per login |
| IDN-U-017 | U | Wrong password increments failures; 5th locks for 15 min |
| IDN-U-018 | U | During lockout: wrong password → invalid_credentials; correct → locked with `until` |
| IDN-U-019 | U | Progressive lockout doubling to 24 h |
| IDN-U-020 | U | Disabled / expired / client not allowed / workstation not allowed errors |
| IDN-U-021 | U | Admission hooks called in order; first failure returned |
| IDN-U-022 | U | MustChangePassword → change token flow; change completes sign-in |
| IDN-U-023 | U | Access token claims and lifetime; signature ES256 with kid |
| IDN-U-024 | U | Token validation: wrong aud/iss/expired/not-before/bad signature rejected; 30 s skew accepted |
| IDN-U-025 | U | Revoked session rejected immediately via revocation set |
| IDN-U-026 | U | Signing key rotation: new key signs, old key validates for 1 day, then rejected |
| IDN-U-027 | U | Refresh rotation returns new token; old token invalid |
| IDN-U-028 | U | Refresh reuse revokes the whole session |
| IDN-U-029 | U | Refresh binding to client instance and kind |
| IDN-U-030 | U | Sliding idle and absolute session lifetimes |
| IDN-I-001 | I | Users persisted; unique login_norm enforced by DB |
| IDN-I-002 | I | Sign-in → refresh → sign-out through API host; cookie for web, body for desktop |
| IDN-I-003 | I | Session revocation publishes `session.revoked` record |
| IDN-I-004 | I | Bootstrap admin from environment variable; MustChangePassword in Production |
| IDN-I-005 | I | No administrator and no variable → log 2701 |
| IDN-I-006 | I | Password change revokes other sessions |
| IDN-U-101 | U | Password NFKC normalisation (composed/decomposed, ligature); invalid Unicode refused without exception |
| IDN-U-102 | U | Password > 256 chars → `too_long`, never hashed; sign-in with impossible input takes the dummy-hash path for known and unknown logins alike, no lockout failure |
| IDN-U-103 | U | Busy hash pool → `common.rate_limited` on sign-in, create, reset and change (no exception) |
| IDN-U-104 | U | Stored hash with out-of-range parameters or truncated hash never verifies; salts differ per hash |
| IDN-U-105 | U | Token algorithm pinned: `none`, HS256 with the public key, foreign key, missing `kid`, ES384, missing `exp`, tampered payload refused without detail |
| IDN-U-106 | U | Signing keys stored in bootstrap secrets and loaded by another ring (restart, other node); unknown `kid` reloads; store failure fails closed |
| IDN-U-107 | U | Explicit rotation: new key signs at once, previous key validates 1 day then is refused |
| IDN-U-108 | U | Concurrent refresh with one token: exactly one rotation, the others `reused`, session revoked; `rotated_at` concurrency token; token stored hashed |
| IDN-U-109 | U | Revocation set and active sessions loaded from the database after a restart; `RevokeWhereAsync` reaches earlier sessions |
| IDN-U-110 | U | Revocation not lost under concurrent `Touch`; concurrent/double revoke published once, first reason kept |
| IDN-U-111 | U | Change token: random 32 bytes, one live token per user, single use, kept on refused password, expiry, refused when the account was disabled meanwhile |
| IDN-U-112 | U | Own password change revokes the other sessions (all without current session); wrong current password counts for the lockout |
| IDN-U-113 | U | Only administrators promote to administrator (U5 on update) |
| IDN-U-114 | U | Rate limiter memory bounded: overlong logins share a bucket, stale keys dropped |
| IDN-U-115 | U | Concurrent failures all counted (5 at once lock); lock count decays after 24 h |
| IDN-U-116 | U | Bootstrap admin: failure logged 2702 + 2701 without the password; created once, later starts change nothing |
| IDN-U-117 | U | Access token `exp` ≤ session end; unknown or missing `ck` refused |
| IDN-U-118 | U | Active login stays unique when an older deleted user had the same login |
| IDN-U-119 | U | Refresh for a disabled, deleted or expired user is refused and revokes the session; nothing rotated |
| IDN-U-120 | U | Refresh refused by an admission hook leaves the token usable; it refreshes once the hook allows, then the old token is a reuse |
| IDN-U-121 | U | Concurrent refresh with checks: one winner, checks run once; a reused token never reaches the checks |
| IDN-U-122 | U | `password_history` keeps the last 5 hashes only (reset and own change); policy unchanged |
| IDN-U-123 | U | Sign-in succeeds when the user row is changed during it (`LastSignInAt` write is best effort) |
| IDN-U-124 | U | Kind changed to Template or Script revokes the sessions; another kind keeps them |
| IDN-U-125 | U | Settings: the defaults of §7.1 apply when no `Identity:*` key is set; the issuer carries the site id |
| IDN-U-126 | U | Settings: every key of §7.1 is bound (ISO 8601 durations, lists) |
| IDN-U-127 | U | Settings: invalid, out-of-range, unknown keys and WebAuthn origin rules are all reported with their key and code; nothing applied |
| IDN-U-128 | U | Issuer per site: a token of site A is refused by site B with the same keys, without detail |
| IDN-U-129 | U | Bound settings reach the services (lockout threshold and duration, token lifetime) |
| IDN-U-031 | U | LDAP filter and DN escaping (RFC 4515/4514) with injection attempts |
| IDN-U-032 | U | LDAP: user search then user bind; wrong password → invalid_credentials |
| IDN-U-033 | U | LDAP: JIT provisioning creates user with Directory source and attributes |
| IDN-U-034 | U | LDAP: existingOnly refuses unknown users |
| IDN-U-035 | U | LDAP: group mapping applies first matching template and user groups (RGT fake) |
| IDN-U-036 | U | LDAP: nested group resolution query uses matching rule in chain |
| IDN-U-037 | U | LDAP: server failover to second server |
| IDN-U-038 | U | LDAP: unreachable → directory.unavailable |
| IDN-U-039 | U | LDAP: insecure transport refused unless allowed |
| IDN-U-040 | U | Directory users have no local password; local password endpoints refuse |
| IDN-U-041 | U | OIDC start: state, nonce, PKCE S256 generated; redirect URL correct |
| IDN-U-042 | U | OIDC callback: state single use, expiry |
| IDN-U-043 | U | OIDC ID token validation: signature, iss, aud, nonce, exp |
| IDN-U-044 | U | OIDC claim mapping configurable (preferred_username vs email) |
| IDN-U-045 | U | OIDC groups claim → mappings |
| IDN-U-046 | U | OIDC exchange code single use, 60 s |
| IDN-U-047 | U | Desktop loopback redirect allowed only for 127.0.0.1 with any port |
| IDN-U-048 | U | Mobile custom scheme redirect allowed only for `watchpoint://auth/callback` |
| IDN-U-049 | U | Provider secrets read from vault |
| IDN-U-050 | U | Provider misconfiguration reported as validation errors |
| IDN-I-007 | I | LDAP end-to-end with OpenLDAP container (bind, groups, JIT) |
| IDN-I-008 | I | LDAPS with custom CA pin |
| IDN-I-009 | I | OIDC end-to-end with Keycloak container (code + PKCE) |
| IDN-I-010 | I | OIDC with groups claim and mapping |
| IDN-I-011 | I | Directory change of display name synced at next sign-in |
| IDN-I-012 | I | Directory down while break-glass works |
| IDN-U-051 | U | MFA policy resolution (user overrides, site policy, configuration users) |
| IDN-U-052 | U | TOTP enrolment URI format and confirm |
| IDN-U-053 | U | TOTP window ±1 step; replay of same step rejected |
| IDN-U-054 | U | TOTP secret stored in vault, not in DB |
| IDN-U-055 | U | Recovery codes single use, hashed |
| IDN-U-056 | U | WebAuthn registration and assertion with a software authenticator: wrong origin, RP id, challenge, user, expired or reused challenge, unknown key refused |
| IDN-U-057 | U | WebAuthn sign counter regression rejected |
| IDN-U-058 | U | MFA verify with mfaToken single use, 5 min |
| IDN-U-059 | U | 5 wrong MFA codes invalidate mfaToken and count as failures |
| IDN-U-060 | U | Step-up token bound to session, 5 min |
| IDN-U-061 | U | Admin MFA reset audited |
| IDN-U-062 | U | Break-glass: local only, reason required, desktop only, 2 h sessions |
| IDN-U-063 | U | Break-glass produces critical audit and system event |
| IDN-U-064 | U | Break-glass bypasses MFA but not rate limits |
| IDN-U-065 | U | Client kind claim set from request; web cannot claim desktop through web origin (cookie path) |
| IDN-U-066 | U | Desktop mTLS sign-in endpoint sets `wksVerified=true` |
| IDN-U-067 | U | Require enrolled workstation for configuration sessions when enabled |
| IDN-U-068 | U | Concurrent session limit enforced at sign-in (oldest not revoked; new refused) |
| IDN-U-069 | U | Session list and revoke by administrator |
| IDN-U-070 | U | Revoke where predicate (used by MNT) |
| IDN-U-071 | U | User disabled → sessions revoked |
| IDN-U-072 | U | Administrator flag removed → sessions revoked |
| IDN-U-073 | U | Account locked system event published |
| IDN-U-074 | U | Logins hashed in warning logs; tokens/passwords never logged (`SecretLeakDetector`) |
| IDN-U-075 | U | Password length > 256 rejected before hashing |
| IDN-U-076 | U | Sign-in admission by MNT returns maintenance error |
| IDN-U-077 | U | Expired user mid-session → refresh refused |
| IDN-U-078 | U | Language of user returned in token and `me` |
| IDN-U-079 | U | `me` returns effective permissions summary from RGT |
| IDN-U-080 | U | Unlock by administrator clears lockout |
| IDN-U-130 | U | TOTP matches the RFC 6238 test vectors (8 and 6 digits); base32 of RFC 4648 |
| IDN-U-131 | U | Partial sign-in cannot refresh or skip the second factor: mfaToken refused by refresh, token validation, change flow; forced password change still owes MFA; user disabled meanwhile refused at the factor |
| IDN-U-132 | U | MFA required without enrolment: enrolment token bound to the client, allows the enrolment only, wrong code counted, completion signs in with recovery codes, 10 min expiry |
| IDN-U-133 | U | Concurrent verifications of one TOTP or recovery code accept it once |
| IDN-U-134 | U | Break-glass account: reserved login and kind, local only, single, administrator forced, no silent change (kind, admin, source, MFA refused; enable and password reset audited Critical, log 2731) |

## 16. Tasks

| Task | Sections | Tests |
|---|---|---|
| T1.18 | §4–§7, §13, §14 (part 1) | IDN-U-001…IDN-U-030, IDN-I-001…IDN-I-006 |
| T1.18r | §4–§7, §13, §14 (security review and fixes of T1.18) | IDN-U-101…IDN-U-124 (+ IDN-U-001…030, IDN-I-001…006 still passing) |
| T1.18s | §6.2 issuer, §7.1 settings binding | IDN-U-125…IDN-U-129, HST-U-046, HST-U-047 |
| T3.07 | §8, §9 | IDN-U-031…IDN-U-050, IDN-I-007…IDN-I-012 |
| T3.08 | §10–§12 (+ §6.1 step 8, §6.4 manager, §7.2 part 2 endpoints) | IDN-U-051…IDN-U-080, IDN-U-130…IDN-U-134 |
