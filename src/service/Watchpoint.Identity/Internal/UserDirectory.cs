using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal;

/// <summary>Implementation of <see cref="IUserDirectory"/> managing users and enforcing rules U1–U7 (spec IDN §4).</summary>
internal sealed partial class UserDirectory : IUserDirectory
{
    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IPasswordPolicy _passwordPolicy;
    private readonly Argon2PasswordHasher _passwordHasher;
    private readonly TimeProvider _clock;

    public event EventHandler<UserChangedEventArgs>? Changed;

    public UserDirectory(
        IDbContextFactory<IdentityDbContext> dbFactory,
        ISessionRegistry sessionRegistry,
        IPasswordPolicy passwordPolicy,
        Argon2PasswordHasher passwordHasher,
        TimeProvider clock)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _passwordPolicy = passwordPolicy ?? throw new ArgumentNullException(nameof(passwordPolicy));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Result<User> Get(UserId id)
    {
        using var db = _dbFactory.CreateDbContext();
        var entity = db.Users.Find(id);
        if (entity is null || entity.DeletedAt is not null)
        {
            return IdentityErrors.UserNotFound(id);
        }

        return MapToUser(entity);
    }

    public Result<User> FindByLogin(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return Error.NotFound("identity.user.not_found", ("login", login ?? ""));
        }

        var loginNorm = login.Trim().ToLowerInvariant();
        using var db = _dbFactory.CreateDbContext();
        var entity = db.Users.FirstOrDefault(u => u.LoginNorm == loginNorm && u.DeletedAt == null);
        if (entity is null)
        {
            return Error.NotFound("identity.user.not_found", ("login", login));
        }

        return MapToUser(entity);
    }

    public Page<User> Query(UserQuery query, PageRequest page)
    {
        using var db = _dbFactory.CreateDbContext();
        IQueryable<UserEntity> q = db.Users.Where(u => u.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();
            q = q.Where(u => u.LoginNorm.Contains(text, StringComparison.OrdinalIgnoreCase) || u.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (query.Kind is { } kind)
        {
            var kindStr = kind.ToString();
            q = q.Where(u => u.Kind == kindStr);
        }

        if (query.Enabled is { } enabled)
        {
            q = q.Where(u => u.Enabled == enabled);
        }

        if (query.AuthSource is { } authSource)
        {
            var authStr = authSource.ToString();
            q = q.Where(u => u.AuthSource == authStr);
        }

        if (query.Administrator is { } admin)
        {
            q = q.Where(u => u.Administrator == admin);
        }

        var limit = page.Limit <= 0 ? 100 : Math.Min(page.Limit, 1000);
        var items = q.OrderBy(u => u.LoginNorm).Take(limit).ToList().Select(MapToUser).ToList();
        return new Page<User>(items, null);
    }

    public async Task<Result<UserId>> CreateAsync(NewUser user, string? initialPassword, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(ctx);

        // U1: Login 2–64 chars, ^[A-Za-z0-9._@-]+$, unique case-insensitively
        var login = user.Login?.Trim() ?? string.Empty;
        if (login.Length < 2 || login.Length > 64 || !LoginRegex().IsMatch(login))
        {
            return Error.Validation("identity.user.login_invalid", [FieldError.Create("/login", ErrorCode.FromTrusted("identity.user.login_invalid"))]);
        }

        var loginNorm = login.ToLowerInvariant();
        var now = _clock.GetUtcNow();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Check login uniqueness (U1 / U7: reusable after 30 days if soft deleted)
        var existing = await db.Users.FirstOrDefaultAsync(u => u.LoginNorm == loginNorm, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.DeletedAt is null)
            {
                return IdentityErrors.LoginTaken(login);
            }
            if (now - existing.DeletedAt.Value < TimeSpan.FromDays(30))
            {
                return IdentityErrors.LoginTaken(login);
            }
        }

        // U2: Template and Script users cannot sign in and have AuthSource.None
        var effectiveKind = user.Kind;
        var effectiveAuthSource = user.AuthSource;
        if (effectiveKind is UserKind.Template or UserKind.Script)
        {
            effectiveAuthSource = AuthSource.None;
        }

        // U5: Only administrators can create administrators
        if (user.Administrator)
        {
            if (ctx.Actor is { } actorId)
            {
                var actor = await db.Users.FindAsync([actorId], ct).ConfigureAwait(false);
                if (actor is null || !actor.Administrator)
                {
                    return IdentityErrors.AdminRequired();
                }
            }
        }

        string? passwordHash = null;
        if (effectiveAuthSource == AuthSource.Local)
        {
            if (string.IsNullOrEmpty(initialPassword))
            {
                return Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("common.value.required"))]);
            }

            var policyResult = await _passwordPolicy.ValidateAsync(initialPassword, login, user.DisplayName, null, ct).ConfigureAwait(false);
            if (!policyResult.IsSuccess)
            {
                return policyResult.Error;
            }

            passwordHash = await _passwordHasher.HashPasswordAsync(initialPassword, ct).ConfigureAwait(false);
        }

        var id = UserId.New();
        var entity = new UserEntity
        {
            Id = id,
            Login = login,
            LoginNorm = loginNorm,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Phone = user.Phone,
            Language = string.IsNullOrWhiteSpace(user.Language) ? "en" : user.Language,
            TimeZone = user.TimeZone,
            Kind = effectiveKind.ToString(),
            Enabled = user.Enabled,
            Administrator = user.Administrator,
            AuthSource = effectiveAuthSource.ToString(),
            ExternalId = user.ExternalId,
            ExternalProvider = user.ExternalProvider,
            ValidFrom = user.ValidFrom,
            ValidUntil = user.ValidUntil,
            AllowedClientsJson = JsonSerializer.Serialize(user.AllowedClients?.Select(c => c.ToString()) ?? []),
            AllowedWorkstationsJson = JsonSerializer.Serialize(user.AllowedWorkstations ?? []),
            SessionPolicyJson = JsonSerializer.Serialize(user.SessionPolicy ?? SessionPolicy.Default),
            MfaPolicy = user.Mfa.ToString(),
            PasswordHash = passwordHash,
            PasswordChangedAt = passwordHash is not null ? now : null,
            MustChangePassword = user.MustChangePassword,
            LastSignInAt = null,
            Notes = user.Notes,
            DeletedAt = null,
            RowVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(entity);

        if (passwordHash is not null)
        {
            db.PasswordHistory.Add(new PasswordHistoryEntity
            {
                Id = Guid.NewGuid(),
                UserId = id,
                Hash = passwordHash,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Changed?.Invoke(this, new UserChangedEventArgs(id, "Created"));

        return id;
    }

    public async Task<Result> UpdateAsync(UserId id, UserUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(ctx);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Users.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null || entity.DeletedAt is not null)
        {
            return IdentityErrors.UserNotFound(id);
        }

        if (entity.RowVersion != expectedVersion)
        {
            return IdentityErrors.VersionConflict();
        }

        // U4: A user cannot change their own Administrator, Enabled, Kind
        if (ctx.Actor is { } actorId && actorId == id)
        {
            if (update.Administrator.HasValue && update.Administrator.Value != entity.Administrator ||
                update.Enabled.HasValue && update.Enabled.Value != entity.Enabled ||
                update.Kind.HasValue && update.Kind.Value.ToString() != entity.Kind)
            {
                return IdentityErrors.SelfChangeForbidden();
            }
        }

        // U3: At least one active administrator of kind Person must always exist
        var willBeAdmin = update.Administrator ?? entity.Administrator;
        var willBeEnabled = update.Enabled ?? entity.Enabled;
        var willBeKind = update.Kind?.ToString() ?? entity.Kind;
        var now = _clock.GetUtcNow();
        var willBeExpired = (update.ValidFrom ?? entity.ValidFrom) > now || (update.ValidUntil ?? entity.ValidUntil) < now;

        if (entity.Administrator && entity.Kind == UserKind.Person.ToString() && entity.Enabled)
        {
            if (!willBeAdmin || !willBeEnabled || willBeKind != UserKind.Person.ToString() || willBeExpired)
            {
                var otherActiveAdmins = await db.Users
                    .CountAsync(u => u.Id != id && u.Administrator && u.Enabled && u.Kind == "Person" && u.DeletedAt == null, ct)
                    .ConfigureAwait(false);

                if (otherActiveAdmins == 0)
                {
                    return IdentityErrors.LastAdministrator();
                }
            }
        }

        // Apply updates
        if (update.DisplayName is not null) entity.DisplayName = update.DisplayName;
        if (update.Email is not null) entity.Email = update.Email;
        if (update.Phone is not null) entity.Phone = update.Phone;
        if (update.Language is not null) entity.Language = update.Language;
        if (update.TimeZone is not null) entity.TimeZone = update.TimeZone;
        if (update.Kind is { } k) entity.Kind = k.ToString();
        if (update.Enabled is { } en) entity.Enabled = en;
        if (update.Administrator is { } adm) entity.Administrator = adm;
        if (update.AuthSource is { } src) entity.AuthSource = src.ToString();
        if (update.ExternalId is not null) entity.ExternalId = update.ExternalId;
        if (update.ExternalProvider is not null) entity.ExternalProvider = update.ExternalProvider;
        if (update.ValidFrom is not null) entity.ValidFrom = update.ValidFrom;
        if (update.ValidUntil is not null) entity.ValidUntil = update.ValidUntil;
        if (update.AllowedClients is not null) entity.AllowedClientsJson = JsonSerializer.Serialize(update.AllowedClients.Select(c => c.ToString()));
        if (update.AllowedWorkstations is not null) entity.AllowedWorkstationsJson = JsonSerializer.Serialize(update.AllowedWorkstations);
        if (update.SessionPolicy is not null) entity.SessionPolicyJson = JsonSerializer.Serialize(update.SessionPolicy);
        if (update.Mfa is { } mfa) entity.MfaPolicy = mfa.ToString();
        if (update.Notes is not null) entity.Notes = update.Notes;

        entity.RowVersion++;
        entity.UpdatedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Automatic revocation if disabled or admin flag removed (spec IDN §6.3)
        if (update.Enabled is false || (update.Administrator is false && entity.Administrator == false))
        {
            await _sessionRegistry.RevokeAllAsync(id, "User disabled or privileges changed", ctx.Actor, ct).ConfigureAwait(false);
        }

        Changed?.Invoke(this, new UserChangedEventArgs(id, "Updated"));
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(UserId id, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Users.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null || entity.DeletedAt is not null)
        {
            return IdentityErrors.UserNotFound(id);
        }

        // U3: Last administrator protected
        if (entity.Administrator && entity.Kind == UserKind.Person.ToString() && entity.Enabled)
        {
            var otherActiveAdmins = await db.Users
                .CountAsync(u => u.Id != id && u.Administrator && u.Enabled && u.Kind == "Person" && u.DeletedAt == null, ct)
                .ConfigureAwait(false);

            if (otherActiveAdmins == 0)
            {
                return IdentityErrors.LastAdministrator();
            }
        }

        var now = _clock.GetUtcNow();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        entity.RowVersion++;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // U7: Deleting a user revokes all sessions
        await _sessionRegistry.RevokeAllAsync(id, "User deleted", ctx.Actor, ct).ConfigureAwait(false);

        Changed?.Invoke(this, new UserChangedEventArgs(id, "Deleted"));
        return Result.Success();
    }

    public async Task<Result> SetPasswordAsync(UserId id, string newPassword, bool mustChange, ChangeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Users.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null || entity.DeletedAt is not null)
        {
            return IdentityErrors.UserNotFound(id);
        }

        var policyResult = await _passwordPolicy.ValidateAsync(newPassword, entity.Login, entity.DisplayName, id, ct).ConfigureAwait(false);
        if (!policyResult.IsSuccess)
        {
            return policyResult.Error;
        }

        var now = _clock.GetUtcNow();
        var newHash = await _passwordHasher.HashPasswordAsync(newPassword, ct).ConfigureAwait(false);

        entity.PasswordHash = newHash;
        entity.PasswordChangedAt = now;
        entity.MustChangePassword = mustChange;
        entity.UpdatedAt = now;
        entity.RowVersion++;

        db.PasswordHistory.Add(new PasswordHistoryEntity
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Hash = newHash,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Admin reset revokes all sessions of the user
        await _sessionRegistry.RevokeAllAsync(id, "Password reset by administrator", ctx.Actor, ct).ConfigureAwait(false);

        Changed?.Invoke(this, new UserChangedEventArgs(id, "PasswordReset"));
        return Result.Success();
    }

    public async Task<Result> ChangeOwnPasswordAsync(UserId id, string current, string newPassword, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Users.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is null || entity.DeletedAt is not null)
        {
            return IdentityErrors.UserNotFound(id);
        }

        if (string.IsNullOrEmpty(entity.PasswordHash))
        {
            return IdentityErrors.PasswordIncorrect();
        }

        var (verified, _) = await _passwordHasher.VerifyPasswordAsync(current, entity.PasswordHash, ct).ConfigureAwait(false);
        if (!verified)
        {
            return IdentityErrors.PasswordIncorrect();
        }

        var policyResult = await _passwordPolicy.ValidateAsync(newPassword, entity.Login, entity.DisplayName, id, ct).ConfigureAwait(false);
        if (!policyResult.IsSuccess)
        {
            return policyResult.Error;
        }

        var now = _clock.GetUtcNow();
        var newHash = await _passwordHasher.HashPasswordAsync(newPassword, ct).ConfigureAwait(false);

        entity.PasswordHash = newHash;
        entity.PasswordChangedAt = now;
        entity.MustChangePassword = false;
        entity.UpdatedAt = now;
        entity.RowVersion++;

        db.PasswordHistory.Add(new PasswordHistoryEntity
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Hash = newHash,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Self password change revokes all other sessions of the user
        Changed?.Invoke(this, new UserChangedEventArgs(id, "PasswordChanged"));
        return Result.Success();
    }

    internal static User MapToUser(UserEntity entity)
    {
        if (!Enum.TryParse<UserKind>(entity.Kind, out var kind))
        {
            kind = UserKind.Person;
        }

        if (!Enum.TryParse<AuthSource>(entity.AuthSource, out var authSource))
        {
            authSource = AuthSource.Local;
        }

        if (!Enum.TryParse<MfaPolicy>(entity.MfaPolicy, out var mfa))
        {
            mfa = MfaPolicy.Inherit;
        }

        var allowedClients = (JsonSerializer.Deserialize<List<string>>(entity.AllowedClientsJson) ?? [])
            .Select(s => Enum.TryParse<ClientKind>(s, out var c) ? (ClientKind?)c : null)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToHashSet();

        var allowedWorkstations = JsonSerializer.Deserialize<List<string>>(entity.AllowedWorkstationsJson) ?? [];
        var sessionPolicy = JsonSerializer.Deserialize<SessionPolicy>(entity.SessionPolicyJson) ?? SessionPolicy.Default;

        return new User(
            entity.Id,
            entity.Login,
            entity.DisplayName,
            entity.Email,
            entity.Phone,
            entity.Language,
            entity.TimeZone,
            kind,
            entity.Enabled,
            entity.Administrator,
            authSource,
            entity.ExternalId,
            entity.ExternalProvider,
            entity.ValidFrom,
            entity.ValidUntil,
            allowedClients,
            allowedWorkstations,
            sessionPolicy,
            mfa,
            entity.PasswordChangedAt,
            entity.MustChangePassword,
            entity.LastSignInAt,
            entity.Notes,
            entity.RowVersion);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._@-]+$")]
    private static partial Regex LoginRegex();
}
