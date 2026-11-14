using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Audit;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal.Mfa;

/// <summary>Multi-factor authentication (spec IDN §10): policy, TOTP in the vault, recovery codes, WebAuthn, reset.</summary>
internal sealed class MfaService : IMfaService, IDisposable
{
    internal const string ModuleOwner = "Identity";

    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly IUserDirectory _users;
    private readonly Argon2PasswordHasher _hasher;
    private readonly TimeProvider _clock;
    private readonly MfaOptions _options;
    private readonly IPermissionSummaryProvider _permissions;
    private readonly WebAuthnService _webAuthn;
    private readonly IdentityAuditor _auditor;
    private readonly ISecretVault? _vault;
    private readonly ILogger<MfaService>? _logger;

    // One verification at a time per user in this process (replay of a code, double use of a recovery code); the
    // concurrency tokens of the rows cover other nodes.
    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1))];

    public MfaService(
        IDbContextFactory<IdentityDbContext> dbFactory,
        IUserDirectory users,
        Argon2PasswordHasher hasher,
        TimeProvider clock,
        WebAuthnService webAuthn,
        IdentityAuditor auditor,
        MfaOptions? options = null,
        IPermissionSummaryProvider? permissions = null,
        ISecretVault? vault = null,
        ILogger<MfaService>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _webAuthn = webAuthn ?? throw new ArgumentNullException(nameof(webAuthn));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _options = options ?? new MfaOptions();
        _permissions = permissions ?? new AdministratorPermissionSummaryProvider();
        _vault = vault;
        _logger = logger;
    }

    public async ValueTask<bool> IsRequiredAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Kind == UserKind.BreakGlass)
        {
            return false;
        }

        switch (user.Mfa)
        {
            case MfaPolicy.Required:
                return true;
            case MfaPolicy.NotRequired:
                return false;
        }

        switch (_options.RequireFor)
        {
            case MfaRequirement.None:
                return false;
            case MfaRequirement.Everyone:
                return true;
            case MfaRequirement.Administrators:
                return user.Administrator;
            default:
                if (user.Administrator)
                {
                    return true;
                }

                var permissions = await _permissions.GetEffectivePermissionsAsync(user, ct).ConfigureAwait(false);
                return PermissionSummary.IsConfigurationUser(permissions);
        }
    }

    public async Task<MfaStatus> GetStatusAsync(UserId user, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var totp = await db.Totp.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
        var remaining = await db.RecoveryCodes.CountAsync(r => r.UserId == user && r.UsedAt == null, ct).ConfigureAwait(false);
        var credentials = await _webAuthn.ListAsync(user, ct).ConfigureAwait(false);
        return new MfaStatus(totp?.ConfirmedAt is not null, credentials, remaining);
    }

    public async Task<Result<TotpEnrolment>> EnrollTotpAsync(UserId user, CancellationToken ct)
    {
        if (_vault is null)
        {
            return IdentityErrors.MfaVaultUnavailable();
        }

        if (!_users.Get(user).TryGetValue(out var account, out var userError))
        {
            return userError;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Totp.FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
        if (existing?.ConfirmedAt is not null)
        {
            return IdentityErrors.MfaAlreadyEnrolled();
        }

        var owner = new SecretOwner(ModuleOwner, user.Value);
        var secret = Totp.NewSecret();
        try
        {
            var created = await _vault.CreateAsync("identity.totp." + user, SecretKind.Generic, owner, secret, null, ct).ConfigureAwait(false);
            if (!created.TryGetValue(out var secretId, out var vaultError))
            {
                return vaultError;
            }

            var now = _clock.GetUtcNow();
            if (existing is not null)
            {
                // A pending enrolment is replaced: the old secret dies with it.
                await _vault.DeleteAsync(SecretId.From(existing.SecretId), owner, ct).ConfigureAwait(false);
                existing.SecretId = secretId.Value;
                existing.CreatedAt = now;
                existing.LastUsedStep = -1;
            }
            else
            {
                db.Totp.Add(new TotpEntity { UserId = user, SecretId = secretId.Value, CreatedAt = now });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new TotpEnrolment(Totp.ToBase32(secret), Totp.OtpAuthUri(account.Login, secret));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<Result<RecoveryCodes>> ConfirmTotpAsync(UserId user, string code, CancellationToken ct)
    {
        var gate = GateOf(user);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var totp = await db.Totp.FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
            if (totp is null)
            {
                return IdentityErrors.MfaNotEnrolled();
            }

            if (totp.ConfirmedAt is not null)
            {
                return IdentityErrors.MfaAlreadyEnrolled();
            }

            var step = await VerifyTotpStepAsync(user, totp, code, ct).ConfigureAwait(false);
            if (!step.TryGetValue(out var accepted, out var error))
            {
                return error.Code.Value == "identity.signin.mfa_invalid" ? IdentityErrors.MfaCodeInvalid() : error;
            }

            var now = _clock.GetUtcNow();
            totp.ConfirmedAt = now;
            totp.LastUsedStep = accepted;
            var codes = await ReplaceRecoveryCodesAsync(db, user, now, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (_logger is not null)
            {
                IdentityLog.MfaEnrolled(_logger, user.ToString(), "totp");
            }

            await _auditor.RecordAsync("identity.mfaEnrolled", IdentityAuditOutcome.Success, user, user, IdentityAuditSeverity.Sensitive, ct, null, ("method", "totp")).ConfigureAwait(false);
            return new RecoveryCodes(codes);
        }
        catch (HashPoolBusyException)
        {
            return IdentityErrors.RateLimited();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<RecoveryCodes>> RegenerateRecoveryCodesAsync(UserId user, CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var totp = await db.Totp.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
            if (totp?.ConfirmedAt is null)
            {
                return IdentityErrors.MfaNotEnrolled();
            }

            var codes = await ReplaceRecoveryCodesAsync(db, user, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await _auditor.RecordAsync("identity.recoveryCodesRegenerated", IdentityAuditOutcome.Success, user, user, IdentityAuditSeverity.Sensitive, ct).ConfigureAwait(false);
            return new RecoveryCodes(codes);
        }
        catch (HashPoolBusyException)
        {
            return IdentityErrors.RateLimited();
        }
    }

    public Task<Result<WebAuthnCeremony>> BeginWebAuthnRegistrationAsync(UserId user, CancellationToken ct)
    {
        if (!_users.Get(user).TryGetValue(out var account, out var error))
        {
            return Task.FromResult<Result<WebAuthnCeremony>>(error);
        }

        return _webAuthn.BeginRegistrationAsync(account, ct);
    }

    public async Task<Result<WebAuthnCredentialInfo>> CompleteWebAuthnRegistrationAsync(UserId user, string attestationResponseJson, string name, CancellationToken ct)
    {
        var result = await _webAuthn.CompleteRegistrationAsync(user, attestationResponseJson, name, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            if (_logger is not null)
            {
                IdentityLog.MfaEnrolled(_logger, user.ToString(), "webauthn");
            }

            await _auditor.RecordAsync("identity.mfaEnrolled", IdentityAuditOutcome.Success, user, user, IdentityAuditSeverity.Sensitive, ct, null,
                ("method", "webauthn"), ("credential", result.Value.Id.ToString())).ConfigureAwait(false);
        }

        return result;
    }

    public Task<Result> RemoveWebAuthnCredentialAsync(UserId user, Guid credentialId, CancellationToken ct) => _webAuthn.RemoveAsync(user, credentialId, ct);

    public async Task<Result> ResetAsync(UserId user, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Actor is { } actorId)
        {
            if (!_users.Get(actorId).TryGetValue(out var actor, out _) || !actor.Administrator || !actor.Enabled)
            {
                await _auditor.RecordAsync("identity.mfaReset", IdentityAuditOutcome.Denied, ctx.Actor, user, IdentityAuditSeverity.Sensitive, ct).ConfigureAwait(false);
                return IdentityErrors.AdminRequired();
            }
        }

        if (!_users.Get(user).TryGetValue(out _, out var userError))
        {
            return userError;
        }

        var gate = GateOf(user);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var totp = await db.Totp.FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
            if (totp is not null)
            {
                if (_vault is not null)
                {
                    await _vault.DeleteAsync(SecretId.From(totp.SecretId), new SecretOwner(ModuleOwner, user.Value), ct).ConfigureAwait(false);
                }

                db.Totp.Remove(totp);
            }

            db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(r => r.UserId == user).ToListAsync(ct).ConfigureAwait(false));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        await _webAuthn.RemoveAllAsync(user, ct).ConfigureAwait(false);

        if (_logger is not null)
        {
            IdentityLog.MfaReset(_logger, user.ToString(), ctx.Actor?.ToString() ?? "system");
        }

        await _auditor.RecordAsync("identity.mfaReset", IdentityAuditOutcome.Success, ctx.Actor, user, IdentityAuditSeverity.Sensitive, ct, ctx.Comment, ("origin", ctx.Origin)).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Starts an assertion ceremony for a sign-in or step-up of the user.</summary>
    public Task<Result<WebAuthnCeremony>> BeginAssertionAsync(UserId user, CancellationToken ct) => _webAuthn.BeginAssertionAsync(user, ct);

    /// <summary>
    /// Verifies one factor of a confirmed enrolment: TOTP (±1 step, no replay), a recovery code (single use) or a WebAuthn
    /// assertion. Any refusal is <c>identity.signin.mfa_invalid</c> without detail.
    /// </summary>
    public async Task<Result> VerifyAsync(UserId user, MfaMethod method, string? code, string? assertion, CancellationToken ct)
    {
        var gate = GateOf(user);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (method)
            {
                case MfaMethod.Totp:
                    return await VerifyTotpAsync(user, code, ct).ConfigureAwait(false);
                case MfaMethod.Recovery:
                    return await VerifyRecoveryAsync(user, code, ct).ConfigureAwait(false);
                case MfaMethod.WebAuthn:
                    var webAuthn = await _webAuthn.VerifyAssertionAsync(user, assertion, ct).ConfigureAwait(false);
                    return webAuthn.IsSuccess ? Result.Success() : IdentityErrors.MfaInvalid();
                default:
                    return IdentityErrors.MfaInvalid();
            }
        }
        catch (HashPoolBusyException)
        {
            return IdentityErrors.RateLimited();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result> VerifyTotpAsync(UserId user, string? code, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var totp = await db.Totp.FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
        if (totp?.ConfirmedAt is null)
        {
            return IdentityErrors.MfaInvalid();
        }

        var step = await VerifyTotpStepAsync(user, totp, code, ct).ConfigureAwait(false);
        if (!step.TryGetValue(out var accepted, out var error))
        {
            return error;
        }

        totp.LastUsedStep = accepted;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error.Code.Value == "storage.concurrency.conflict")
        {
            // Another node accepted a code meanwhile: this one may be its replay.
            return IdentityErrors.MfaInvalid();
        }

        return Result.Success();
    }

    // Reads the secret from the vault for the duration of the check only; the result is the accepted step.
    private async Task<Result<long>> VerifyTotpStepAsync(UserId user, TotpEntity totp, string? code, CancellationToken ct)
    {
        if (_vault is null)
        {
            return IdentityErrors.MfaVaultUnavailable();
        }

        var read = await _vault.ReadAsync(SecretId.From(totp.SecretId), new SecretOwner(ModuleOwner, user.Value), "totp", ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var secret, out var vaultError))
        {
            return vaultError.Kind == ErrorKind.NotFound ? IdentityErrors.MfaInvalid() : vaultError;
        }

        using (secret)
        {
            var now = _clock.GetUtcNow();
            var accepted = secret.Use(bytes => Totp.Verify(bytes, code, now, totp.LastUsedStep));
            return accepted is { } step ? step : IdentityErrors.MfaInvalid();
        }
    }

    private async Task<Result> VerifyRecoveryAsync(UserId user, string? code, CancellationToken ct)
    {
        var normalized = RecoveryCodeGenerator.Normalize(code);
        if (normalized.Length != RecoveryCodeGenerator.Length)
        {
            return IdentityErrors.MfaInvalid();
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var totp = await db.Totp.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == user, ct).ConfigureAwait(false);
        if (totp?.ConfirmedAt is null)
        {
            return IdentityErrors.MfaInvalid();
        }

        var unused = await db.RecoveryCodes.Where(r => r.UserId == user && r.UsedAt == null).ToListAsync(ct).ConfigureAwait(false);

        // Every stored hash is checked, whatever matches: the response time does not tell which code was used.
        RecoveryCodeEntity? match = null;
        foreach (var row in unused)
        {
            var (verified, _) = await _hasher.VerifyPasswordAsync(normalized, row.Hash, ct).ConfigureAwait(false);
            if (verified)
            {
                match ??= row;
            }
        }

        if (match is null)
        {
            return IdentityErrors.MfaInvalid();
        }

        match.UsedAt = _clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error.Code.Value == "storage.concurrency.conflict")
        {
            return IdentityErrors.MfaInvalid();
        }

        return Result.Success();
    }

    private async Task<string[]> ReplaceRecoveryCodesAsync(IdentityDbContext db, UserId user, DateTimeOffset now, CancellationToken ct)
    {
        var old = await db.RecoveryCodes.Where(r => r.UserId == user).ToListAsync(ct).ConfigureAwait(false);
        db.RecoveryCodes.RemoveRange(old);
        var codes = RecoveryCodeGenerator.Generate();
        foreach (var code in codes)
        {
            db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                Id = Guid.CreateVersion7(),
                UserId = user,
                Hash = await _hasher.HashPasswordAsync(RecoveryCodeGenerator.Normalize(code), ct).ConfigureAwait(false),
                CreatedAt = now,
            });
        }

        return codes;
    }

    private SemaphoreSlim GateOf(UserId user) => _gates[(int)((uint)user.GetHashCode() % (uint)_gates.Length)];

    public void Dispose()
    {
        foreach (var gate in _gates)
        {
            gate.Dispose();
        }
    }
}
